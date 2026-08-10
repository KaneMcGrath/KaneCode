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
    public async Task WhenWriteSucceedsThenDetailsContainPathStatsAndPreview()
    {
        WriteFileTool tool = new WriteFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "nested/output.txt",
              "content": "line one\nline two\nline three\n"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.NotNull(result.Details);
        // Path is shown relative to the project root for readability.
        Assert.Contains(Path.Combine("nested", "output.txt"), result.Details, StringComparison.Ordinal);
        // Size stats: 29 bytes, 29 chars, 3 lines.
        Assert.Contains("29 B", result.Details, StringComparison.Ordinal);
        Assert.Contains("29 chars", result.Details, StringComparison.Ordinal);
        Assert.Contains("3 lines", result.Details, StringComparison.Ordinal);
        // Preview shows the first lines of content.
        Assert.Contains("line one", result.Details, StringComparison.Ordinal);
        Assert.Contains("line two", result.Details, StringComparison.Ordinal);
        // Model-facing output stays concise.
        Assert.StartsWith("Wrote 29 bytes to '", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOverwritingExistingFileThenDetailsSayOverwrote()
    {
        string path = Path.Combine(_tempDir, "existing.txt");
        await File.WriteAllTextAsync(path, "old content");

        WriteFileTool tool = new WriteFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "existing.txt",
              "content": "new content"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.NotNull(result.Details);
        Assert.Contains("Overwrote", result.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Created", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenWriteSucceedsThenDetailsDoNotConsumeModelOutput()
    {
        WriteFileTool tool = new WriteFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "filePath": "output.txt",
              "content": "hello"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        // Output stays the concise, model-bound text; the rich detail is separate.
        Assert.Equal("Wrote 5 bytes to '" + Path.Combine(_tempDir, "output.txt") + "'.", result.Output);
        Assert.NotEqual(result.Output, result.Details);
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
