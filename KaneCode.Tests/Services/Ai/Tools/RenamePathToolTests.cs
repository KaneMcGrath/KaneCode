using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class RenamePathToolTests : IDisposable
{
    private readonly string _tempDir;

    public RenamePathToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeRenamePathTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WhenFilePathIsInsideProjectThenRenamesFile()
    {
        string sourcePath = Path.Combine(_tempDir, "nested", "old-name.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "content");
        RenamePathTool tool = new RenamePathTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "sourcePath": "nested/old-name.txt",
              "destinationPath": "nested/new-name.txt"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);
        string destinationPath = Path.Combine(_tempDir, "nested", "new-name.txt");

        Assert.True(result.Success);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
    }

    [Fact]
    public async Task WhenDirectoryPathIsInsideProjectThenRenamesDirectory()
    {
        string sourcePath = Path.Combine(_tempDir, "old-folder");
        string filePath = Path.Combine(sourcePath, "file.txt");
        Directory.CreateDirectory(sourcePath);
        await File.WriteAllTextAsync(filePath, "content");
        RenamePathTool tool = new RenamePathTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "sourcePath": "old-folder",
              "destinationPath": "renamed-folder"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);
        string destinationPath = Path.Combine(_tempDir, "renamed-folder");

        Assert.True(result.Success);
        Assert.False(Directory.Exists(sourcePath));
        Assert.True(Directory.Exists(destinationPath));
        Assert.True(File.Exists(Path.Combine(destinationPath, "file.txt")));
    }

    [Fact]
    public async Task WhenDestinationAlreadyExistsThenReturnsFailure()
    {
        string sourcePath = Path.Combine(_tempDir, "source.txt");
        string destinationPath = Path.Combine(_tempDir, "destination.txt");
        await File.WriteAllTextAsync(sourcePath, "source");
        await File.WriteAllTextAsync(destinationPath, "destination");
        RenamePathTool tool = new RenamePathTool(() => _tempDir);
        JsonElement args = BuildArgs(sourcePath, destinationPath);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("Destination already exists", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
    }

    [Fact]
    public async Task WhenSourcePathIsOutsideProjectThenReturnsFailure()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsidePath = Path.Combine(outsideDirectory, "outside.txt");
            await File.WriteAllTextAsync(outsidePath, "outside");
            RenamePathTool tool = new RenamePathTool(() => _tempDir);
            JsonElement args = BuildArgs(outsidePath, Path.Combine(_tempDir, "renamed.txt"));

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.False(result.Success);
            Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(outsidePath));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task WhenSourcePathIsProjectRootThenReturnsFailure()
    {
        string nestedDirectory = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nestedDirectory);
        RenamePathTool tool = new RenamePathTool(() => _tempDir);
        JsonElement args = BuildArgs(_tempDir, nestedDirectory);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("project root", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(_tempDir));
    }

    private static JsonElement BuildArgs(string sourcePath, string destinationPath)
    {
        string escapedSourcePath = sourcePath.Replace("\\", "\\\\", StringComparison.Ordinal);
        string escapedDestinationPath = destinationPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        return JsonDocument.Parse($"{{\"sourcePath\":\"{escapedSourcePath}\",\"destinationPath\":\"{escapedDestinationPath}\"}}")
            .RootElement;
    }
}
