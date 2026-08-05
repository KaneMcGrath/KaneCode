using System.IO;
using System.Text.Json;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class RequestToolTests : IDisposable
{
    private readonly string _tempDir;

    public RequestToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeRequestToolTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WhenValidRequestThenSavesTxtFileAndSucceeds()
    {
        RequestTool tool = new(_tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "toolName": "update_json",
              "description": "A tool to update a JSON file.",
              "reasoning": "I need it to modify project configuration files."
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        Assert.Contains("update_json", result.Output, StringComparison.Ordinal);

        string[] savedFiles = Directory.GetFiles(_tempDir, "*.txt");
        Assert.Single(savedFiles);

        string contents = await File.ReadAllTextAsync(savedFiles[0]);
        Assert.Contains("update_json", contents, StringComparison.Ordinal);
        Assert.Contains("A tool to update a JSON file.", contents, StringComparison.Ordinal);
        Assert.Contains("I need it to modify project configuration files.", contents, StringComparison.Ordinal);
        Assert.Contains("no immediate effect", contents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("improve future versions", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenReasoningOmittedThenRequestStillSaved()
    {
        RequestTool tool = new(_tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "toolName": "format_code",
              "description": "A tool to format code."
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.True(result.Success);
        string[] savedFiles = Directory.GetFiles(_tempDir, "*.txt");
        Assert.Single(savedFiles);
    }

    [Fact]
    public async Task WhenToolNameMissingThenReturnsFailureAndSavesNothing()
    {
        RequestTool tool = new(_tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "description": "A tool to update a JSON file."
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("toolName", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.txt"));
    }

    [Fact]
    public async Task WhenDescriptionMissingThenReturnsFailureAndSavesNothing()
    {
        RequestTool tool = new(_tempDir);
        JsonElement args = JsonDocument.Parse("""
            {
              "toolName": "update_json"
            }
            """).RootElement;

        ToolCallResult result = await tool.ExecuteAsync(args);

        Assert.False(result.Success);
        Assert.Contains("description", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.txt"));
    }

    [Fact]
    public void ToolMetadataExposesNameCategoryAndFutureOnlyDescription()
    {
        RequestTool tool = new(_tempDir);

        Assert.Equal("request_tool", tool.Name);
        Assert.Equal("Debug", tool.Category);
        Assert.False(tool.RequiresConfirmation);
        Assert.Contains("no immediate effect", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("improve future versions", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continue finishing your task", tool.Description, StringComparison.OrdinalIgnoreCase);
    }
}
