using KaneCode.Services.Ai;
using System.IO;

namespace KaneCode.Tests.Services.Ai;

public class AiDebugLogServiceTests
{
    [Fact]
    public void WhenLoggingToolFailureThenNewestEntryIsInsertedFirst()
    {
        AiDebugLogService service = new();

        service.LogToolFailure("read_file", "{\"path\":\"a.txt\"}", "first error", "call-1");
        service.LogToolFailure("write_file", "{\"path\":\"b.txt\"}", "second error", "call-2");

        Assert.Equal(2, service.ToolFailures.Count);
        Assert.Equal("write_file", service.ToolFailures[0].ToolName);
        Assert.Equal("second error", service.ToolFailures[0].Error);
        Assert.Equal("call-2", service.ToolFailures[0].ToolCallId);
    }

    [Fact]
    public void WhenLoggingToolFailureWithBlankArgumentsThenFallbackTextIsUsed()
    {
        AiDebugLogService service = new();

        service.LogToolFailure("search_files", " ", "bad arguments", "call-3");

        Assert.Single(service.ToolFailures);
        Assert.Equal("(no arguments)", service.ToolFailures[0].Arguments);
    }

    [Fact]
    public void WhenLoggingMoreThanMaxFailuresThenOldestEntriesAreTrimmed()
    {
        AiDebugLogService service = new();

        for (int i = 0; i < 205; i++)
        {
            service.LogToolFailure($"tool_{i}", null, $"error_{i}", $"call_{i}");
        }

        Assert.Equal(200, service.ToolFailures.Count);
        Assert.Equal("tool_204", service.ToolFailures[0].ToolName);
        Assert.Equal("tool_5", service.ToolFailures[^1].ToolName);
    }

    [Fact]
    public void WhenExportingToolFailureEntryThenFileContainsErrorAndArguments()
    {
        AiDebugLogService service = new();
        string exportDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeAiDebugTests_{Guid.NewGuid():N}");

        service.LogToolFailure("read_file", "{\"path\":\"a.txt\"}", "bad path", "call-9");

        string exportedPath = AiDebugLogService.ExportToolFailureEntry(service.ToolFailures[0], exportDirectory);
        string exportedText = File.ReadAllText(exportedPath);

        Assert.True(File.Exists(exportedPath));
        Assert.Contains("Error:", exportedText, StringComparison.Ordinal);
        Assert.Contains("bad path", exportedText, StringComparison.Ordinal);
        Assert.Contains("Arguments:", exportedText, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"a.txt\"", exportedText, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenExportingToolFailureEntryWithBlankDirectoryThenArgumentExceptionIsThrown()
    {
        AiDebugLogService service = new();

        service.LogToolFailure("read_file", "{\"path\":\"a.txt\"}", "bad path", "call-10");

        Assert.Throws<ArgumentException>(() => AiDebugLogService.ExportToolFailureEntry(service.ToolFailures[0], " "));
    }
}
