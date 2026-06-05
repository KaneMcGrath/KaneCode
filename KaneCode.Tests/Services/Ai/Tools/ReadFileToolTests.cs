using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public class ReadFileToolTests : IDisposable
{
    private readonly string _tempDir;

    public ReadFileToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void WhenConstructedThenNameAndDescriptionAreSet()
    {
        ReadFileTool tool = new ReadFileTool(() => _tempDir);

        Assert.Equal("read", tool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        Assert.NotEqual(JsonValueKind.Undefined, tool.ParametersSchema.ValueKind);
    }

    [Fact]
    public void WhenConstructedWithNullProviderThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ReadFileTool(null!));
    }

    [Fact]
    public async Task WhenFileExistsThenReturnsContents()
    {
        string filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "hello world");
        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = BuildArgs(filePath);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Equal("hello world", result.Output);
    }

    [Fact]
    public async Task WhenRelativePathProvidedThenResolvesFromProjectRoot()
    {
        string filePath = Path.Combine(_tempDir, "relative.txt");
        await File.WriteAllTextAsync(filePath, "relative content");
        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "relative.txt" }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Equal("relative content", result.Output);
    }

    [Fact]
    public async Task WhenMultipleFilePathsProvidedThenReturnsContentsForEachFile()
    {
        string firstFilePath = Path.Combine(_tempDir, "first.txt");
        string secondFilePath = Path.Combine(_tempDir, "second.txt");
        await File.WriteAllTextAsync(firstFilePath, "first content");
        await File.WriteAllTextAsync(secondFilePath, "second content");
        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePaths": ["first.txt", "second.txt"] }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("[File: first.txt]", result.Output, StringComparison.Ordinal);
        Assert.Contains("first content", result.Output, StringComparison.Ordinal);
        Assert.Contains("[File: second.txt]", result.Output, StringComparison.Ordinal);
        Assert.Contains("second content", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsFailure()
    {
        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "nonexistent.txt" }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenFilePathMissingThenReturnsFailure()
    {
        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("{}").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("filePath", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenFilePathIsEmptyStringThenReturnsFailure()
    {
        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "  " }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task WhenProjectRootIsNullThenRelativePathStillUsed()
    {
        ReadFileTool tool = new ReadFileTool(() => null);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "no_root.txt" }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("No project or solution is currently loaded", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenAbsolutePathIsOutsideProjectThenReturnsFailure()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsideFilePath = Path.Combine(outsideDirectory, "outside.txt");
            await File.WriteAllTextAsync(outsideFilePath, "outside");

            ReadFileTool tool = new ReadFileTool(() => _tempDir);
            JsonElement args = BuildArgs(outsideFilePath);

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.False(result.Success);
            Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task WhenAbsolutePathIsInsideAttachedExternalFolderThenReturnsContents()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeExternal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsideFilePath = Path.Combine(outsideDirectory, "external.txt");
            await File.WriteAllTextAsync(outsideFilePath, "external context");

            ExternalContextDirectoryRegistry registry = new();
            registry.SetAllowedDirectories([outsideDirectory]);

            ReadFileTool tool = new ReadFileTool(() => _tempDir, registry);
            JsonElement args = BuildArgs(outsideFilePath);

            ToolCallResult result = await tool.ExecuteAsync(args);

            Assert.True(result.Success);
            Assert.Equal("external context", result.Output);
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task WhenFileExceedsMaxLinesThenTrimsWithMessage()
    {
        string filePath = Path.Combine(_tempDir, "long.txt");
        string[] lines = new string[2500];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = $"Line {i + 1}";
        }
        await File.WriteAllLinesAsync(filePath, lines);

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = BuildArgs(filePath);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("Line 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("Line 2000", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Line 2001", result.Output, StringComparison.Ordinal);
        Assert.Contains("trimmed to 2000 lines", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2500 lines", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenStartLineSpecifiedThenReturnsFromThatLine()
    {
        string filePath = Path.Combine(_tempDir, "range.txt");
        string[] lines = new string[100];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = $"Line {i + 1}";
        }
        await File.WriteAllLinesAsync(filePath, lines);

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "range.txt", "startLine": 10 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("Line 10", result.Output, StringComparison.Ordinal);
        Assert.Contains("Line 100", result.Output, StringComparison.Ordinal);
        string[] outputLines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("Line 9", outputLines, StringComparer.Ordinal);
    }

    [Fact]
    public async Task WhenEndLineSpecifiedThenReturnsUpToThatLine()
    {
        string filePath = Path.Combine(_tempDir, "range.txt");
        string[] lines = new string[100];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = $"Line {i + 1}";
        }
        await File.WriteAllLinesAsync(filePath, lines);

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "range.txt", "endLine": 5 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("Line 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("Line 5", result.Output, StringComparison.Ordinal);
        string[] outputLines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("Line 6", outputLines, StringComparer.Ordinal);
    }

    [Fact]
    public async Task WhenStartLineAndEndLineSpecifiedThenReturnsExactRange()
    {
        string filePath = Path.Combine(_tempDir, "range.txt");
        string[] lines = new string[100];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = $"Line {i + 1}";
        }
        await File.WriteAllLinesAsync(filePath, lines);

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "range.txt", "startLine": 10, "endLine": 15 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("Line 10", result.Output, StringComparison.Ordinal);
        Assert.Contains("Line 15", result.Output, StringComparison.Ordinal);
        string[] outputLines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("Line 9", outputLines, StringComparer.Ordinal);
        Assert.DoesNotContain("Line 16", outputLines, StringComparer.Ordinal);
    }

    [Fact]
    public async Task WhenStartLineExceedsFileLengthThenReturnsEmpty()
    {
        string filePath = Path.Combine(_tempDir, "short.txt");
        await File.WriteAllTextAsync(filePath, "only one line");

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "short.txt", "startLine": 10 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task WhenEndLineExceedsFileLengthThenCapsAtFileLength()
    {
        string filePath = Path.Combine(_tempDir, "short.txt");
        string[] lines = new string[5];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = $"Line {i + 1}";
        }
        await File.WriteAllLinesAsync(filePath, lines);

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "short.txt", "endLine": 100 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("Line 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("Line 5", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenStartLineGreaterThanEndLineThenReturnsFailure()
    {
        string filePath = Path.Combine(_tempDir, "range.txt");
        await File.WriteAllTextAsync(filePath, "some content");

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "range.txt", "startLine": 10, "endLine": 5 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("startLine must be less than or equal to endLine", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenStartLineIsZeroThenReturnsFailure()
    {
        string filePath = Path.Combine(_tempDir, "range.txt");
        await File.WriteAllTextAsync(filePath, "some content");

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "range.txt", "startLine": 0 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("startLine must be a positive integer", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenEndLineIsZeroThenReturnsFailure()
    {
        string filePath = Path.Combine(_tempDir, "range.txt");
        await File.WriteAllTextAsync(filePath, "some content");

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePath": "range.txt", "endLine": 0 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("endLine must be a positive integer", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenLineRangeUsedWithMultipleFilesThenAppliesToAll()
    {
        string firstFilePath = Path.Combine(_tempDir, "first.txt");
        string secondFilePath = Path.Combine(_tempDir, "second.txt");
        string[] lines = new string[50];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = $"Line {i + 1}";
        }
        await File.WriteAllLinesAsync(firstFilePath, lines);
        await File.WriteAllLinesAsync(secondFilePath, lines);

        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = JsonDocument.Parse("""{ "filePaths": ["first.txt", "second.txt"], "startLine": 5, "endLine": 10 }""").RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("[File: first.txt]", result.Output, StringComparison.Ordinal);
        Assert.Contains("[File: second.txt]", result.Output, StringComparison.Ordinal);
        Assert.Contains("Line 5", result.Output, StringComparison.Ordinal);
        Assert.Contains("Line 10", result.Output, StringComparison.Ordinal);
        string[] outputLines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("Line 4", outputLines, StringComparer.Ordinal);
        Assert.DoesNotContain("Line 11", outputLines, StringComparer.Ordinal);
    }

    private static JsonElement BuildArgs(string filePath)
    {
        string escaped = filePath.Replace("\\", "\\\\");
        return JsonDocument.Parse("{\"filePath\":\"" + escaped + "\"}").RootElement;
    }
}
