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

        Assert.Equal("read_file", tool.Name);
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
    public async Task WhenFileTooLargeThenReturnsFailure()
    {
        string filePath = Path.Combine(_tempDir, "large.bin");
        byte[] largeContent = new byte[201 * 1024]; // 201 KB, over the 200 KB limit
        await File.WriteAllBytesAsync(filePath, largeContent);
        ReadFileTool tool = new ReadFileTool(() => _tempDir);
        JsonElement args = BuildArgs(filePath);

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("too large", result.Error, StringComparison.OrdinalIgnoreCase);
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

    private static JsonElement BuildArgs(string filePath)
    {
        string escaped = filePath.Replace("\\", "\\\\");
        return JsonDocument.Parse("{\"filePath\":\"" + escaped + "\"}").RootElement;
    }
}
