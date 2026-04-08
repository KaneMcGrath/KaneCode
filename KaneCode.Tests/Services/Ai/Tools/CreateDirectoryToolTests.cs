using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class CreateDirectoryToolTests : IDisposable
{
    private readonly string _tempDir;

    public CreateDirectoryToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeCreateDirectoryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WhenRelativePathIsInsideProjectThenCreatesDirectory()
    {
        CreateDirectoryTool tool = new CreateDirectoryTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "directoryPath": "nested/output"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);
        string createdPath = Path.Combine(_tempDir, "nested", "output");

        Assert.True(result.Success);
        Assert.True(Directory.Exists(createdPath));
    }

    [Fact]
    public async Task WhenDirectoryPathMissingThenReturnsFailure()
    {
        CreateDirectoryTool tool = new CreateDirectoryTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("{}").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("directoryPath", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenDirectoryAlreadyExistsThenReturnsSuccess()
    {
        string existingPath = Path.Combine(_tempDir, "existing");
        Directory.CreateDirectory(existingPath);
        CreateDirectoryTool tool = new CreateDirectoryTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "directoryPath": "existing" }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(existingPath));
    }

    [Fact]
    public async Task WhenPathIsOutsideProjectThenReturnsFailure()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsidePath = Path.Combine(outsideDirectory, "blocked");
            CreateDirectoryTool tool = new CreateDirectoryTool(() => _tempDir);
            JsonElement args = BuildArgs(outsidePath);

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.False(result.Success);
            Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(outsidePath));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    private static JsonElement BuildArgs(string directoryPath)
    {
        string escapedPath = directoryPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        return JsonDocument.Parse($"{{\"directoryPath\":\"{escapedPath}\"}}")
            .RootElement;
    }
}
