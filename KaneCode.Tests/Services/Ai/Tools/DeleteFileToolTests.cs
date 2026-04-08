using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class DeleteFileToolTests : IDisposable
{
    private readonly string _tempDir;

    public DeleteFileToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeDeleteFileTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WhenRelativePathIsInsideProjectThenDeletesFile()
    {
        string filePath = Path.Combine(_tempDir, "nested", "delete-me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "content");
        DeleteFileTool tool = new DeleteFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "nested/delete-me.txt"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task WhenFilePathMissingThenReturnsFailure()
    {
        DeleteFileTool tool = new DeleteFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("{}").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("filePath", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenPathIsOutsideProjectThenReturnsFailure()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsideFilePath = Path.Combine(outsideDirectory, "outside.txt");
            await File.WriteAllTextAsync(outsideFilePath, "outside");
            DeleteFileTool tool = new DeleteFileTool(() => _tempDir);
            JsonElement args = BuildArgs(outsideFilePath);

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.False(result.Success);
            Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(outsideFilePath));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task WhenPathIsDirectoryThenReturnsFailure()
    {
        string directoryPath = Path.Combine(_tempDir, "folder");
        Directory.CreateDirectory(directoryPath);
        DeleteFileTool tool = new DeleteFileTool(() => _tempDir);
        JsonElement args = BuildArgs(directoryPath);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("directory", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(directoryPath));
    }

    private static JsonElement BuildArgs(string filePath)
    {
        string escapedPath = filePath.Replace("\\", "\\\\", StringComparison.Ordinal);
        return JsonDocument.Parse($"{{\"filePath\":\"{escapedPath}\"}}")
            .RootElement;
    }
}
