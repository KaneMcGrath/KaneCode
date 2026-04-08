using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class DeleteDirectoryToolTests : IDisposable
{
    private readonly string _tempDir;

    public DeleteDirectoryToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeDeleteDirectoryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WhenRecursiveDeleteRequestedThenDeletesDirectoryTree()
    {
        string nestedDirectory = Path.Combine(_tempDir, "nested", "output");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "file.txt"), "content");
        DeleteDirectoryTool tool = new DeleteDirectoryTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "directoryPath": "nested",
              "recursive": true
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "nested")));
    }

    [Fact]
    public async Task WhenDirectoryPathMissingThenReturnsFailure()
    {
        DeleteDirectoryTool tool = new DeleteDirectoryTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("{}").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("directoryPath", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenPathIsProjectRootThenReturnsFailure()
    {
        DeleteDirectoryTool tool = new DeleteDirectoryTool(() => _tempDir);
        JsonElement args = BuildArgs(_tempDir, recursive: true);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("project root", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public async Task WhenPathIsOutsideProjectThenReturnsFailure()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            DeleteDirectoryTool tool = new DeleteDirectoryTool(() => _tempDir);
            JsonElement args = BuildArgs(outsideDirectory, recursive: true);

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.False(result.Success);
            Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(outsideDirectory));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    private static JsonElement BuildArgs(string directoryPath, bool recursive)
    {
        string escapedPath = directoryPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        return JsonDocument.Parse($"{{\"directoryPath\":\"{escapedPath}\",\"recursive\":{recursive.ToString().ToLowerInvariant()}}}")
            .RootElement;
    }
}
