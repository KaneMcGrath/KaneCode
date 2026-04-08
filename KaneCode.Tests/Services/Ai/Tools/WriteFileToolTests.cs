using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class WriteFileToolTests : IDisposable
{
    private readonly string _tempDir;

    public WriteFileToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeWriteTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WhenRelativePathIsInsideProjectThenWritesFile()
    {
        WriteFileTool tool = new WriteFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "nested/output.txt",
              "content": "hello"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);
        string writtenPath = Path.Combine(_tempDir, "nested", "output.txt");

        Assert.True(result.Success);
        Assert.True(File.Exists(writtenPath));
        Assert.Equal("hello", await File.ReadAllTextAsync(writtenPath));
    }

    [Fact]
    public async Task WhenPathIsOutsideProjectThenReturnsFailure()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsideFilePath = Path.Combine(outsideDirectory, "outside.txt");
            WriteFileTool tool = new WriteFileTool(() => _tempDir);
            JsonElement args = BuildArgs(outsideFilePath, "blocked");

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.False(result.Success);
            Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outsideFilePath));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    private static JsonElement BuildArgs(string filePath, string content)
    {
        string escapedPath = filePath.Replace("\\", "\\\\", StringComparison.Ordinal);
        string escapedContent = content.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return JsonDocument.Parse($"{{\"filePath\":\"{escapedPath}\",\"content\":\"{escapedContent}\"}}")
            .RootElement;
    }
}
