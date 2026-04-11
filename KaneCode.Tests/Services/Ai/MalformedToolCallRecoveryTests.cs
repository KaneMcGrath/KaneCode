using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public class MalformedToolCallRecoveryTests
{
    [Fact]
    public void WhenReasoningStartsMalformedToolCallThenStreamingDetectionReturnsPreview()
    {
        string reasoning = """
            Thinking...
            <tool_call>
            <function=read_file>
            <parameter=filePath>
            README.md
            """;

        IReadOnlyList<StreamingMalformedToolCall> result = MalformedToolCallRecovery.DetectStreaming(reasoning, string.Empty);

        StreamingMalformedToolCall detectedToolCall = Assert.Single(result);
        Assert.Equal("read_file", detectedToolCall.FunctionName);
        Assert.False(detectedToolCall.IsComplete);
        Assert.Contains("<tool_call>", detectedToolCall.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenReasoningContainsMalformedToolCallMarkupThenRecoveryReturnsSyntheticFailure()
    {
        string reasoning = """
            <tool_call>
            <function=read_file>
            <parameter=filePath>
            README.md
            </parameter>
            </function>
            </tool_call>
            """;

        IReadOnlyList<RecoveredMalformedToolCall> result = MalformedToolCallRecovery.Recover(reasoning, string.Empty);

        RecoveredMalformedToolCall recoveredToolCall = Assert.Single(result);
        Assert.Equal("read_file", recoveredToolCall.FunctionName);
        Assert.Contains("assistant reasoning", recoveredToolCall.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", recoveredToolCall.Error, StringComparison.Ordinal);

        using JsonDocument argsDocument = JsonDocument.Parse(recoveredToolCall.ArgumentsJson);
        Assert.Equal("README.md", argsDocument.RootElement.GetProperty("filePath").GetString());
    }

    [Fact]
    public void WhenResponseContainsMalformedToolCallMarkupThenStripToolCallMarkupRemovesItFromContext()
    {
        string response = """
            I will inspect the file.
            <tool_call>
            <function=read_file>
            <parameter=filePath>
            README.md
            </parameter>
            </function>
            </tool_call>
            Then I will summarize the result.
            """;

        string result = MalformedToolCallRecovery.StripToolCallMarkup(response);

        Assert.Contains("I will inspect the file.", result, StringComparison.Ordinal);
        Assert.Contains("Then I will summarize the result.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", result, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTextDoesNotContainMalformedToolCallMarkupThenStripToolCallMarkupReturnsOriginalText()
    {
        string response = "No tool call markup here.";

        string result = MalformedToolCallRecovery.StripToolCallMarkup(response);

        Assert.Equal(response, result);
    }

    [Fact]
    public void WhenToolCallMarkupIsIncompleteThenRecoveryKeepsFunctionNameAndReturnsFailure()
    {
        string response = """
            <tool_call>
            <function=read_file>
            <parameter=filePath>
            README.md
            </parameter>
            </tool_call>
            """;

        IReadOnlyList<RecoveredMalformedToolCall> result = MalformedToolCallRecovery.Recover(string.Empty, response);

        RecoveredMalformedToolCall recoveredToolCall = Assert.Single(result);
        Assert.Equal("read_file", recoveredToolCall.FunctionName);
        Assert.Contains("did not contain a complete function block", recoveredToolCall.Error, StringComparison.Ordinal);
        Assert.Equal("{}", recoveredToolCall.ArgumentsJson);
    }

    [Fact]
    public void WhenToolCallMarkupDoesNotCloseThenRecoveryReturnsInvalidToolCallFailure()
    {
        string reasoning = """
            <tool_call>
            <function=read_file>
            <parameter=filePath>
            README.md
            </parameter>
            </function>
            """;

        IReadOnlyList<RecoveredMalformedToolCall> result = MalformedToolCallRecovery.Recover(reasoning, string.Empty);

        RecoveredMalformedToolCall recoveredToolCall = Assert.Single(result);
        Assert.Equal("read_file", recoveredToolCall.FunctionName);
        Assert.Contains("did not contain a closing </tool_call> tag", recoveredToolCall.Error, StringComparison.Ordinal);
        Assert.Equal("{}", recoveredToolCall.ArgumentsJson);
    }

    [Fact]
    public void WhenResponseDoesNotContainToolMarkupThenRecoveryReturnsEmpty()
    {
        IReadOnlyList<RecoveredMalformedToolCall> result = MalformedToolCallRecovery.Recover("Thinking", "Done");

        Assert.Empty(result);
    }
}
