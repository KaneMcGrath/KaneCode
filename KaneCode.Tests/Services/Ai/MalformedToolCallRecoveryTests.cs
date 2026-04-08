using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public class MalformedToolCallRecoveryTests
{
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

        using JsonDocument argsDocument = JsonDocument.Parse(recoveredToolCall.ArgumentsJson);
        Assert.Equal("README.md", argsDocument.RootElement.GetProperty("filePath").GetString());
    }

    [Fact]
    public void WhenToolCallMarkupIsIncompleteThenRecoveryReturnsInvalidToolCallFailure()
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
        Assert.Equal("invalid_tool_call", recoveredToolCall.FunctionName);
        Assert.Contains("did not contain a complete function block", recoveredToolCall.Error, StringComparison.Ordinal);
        Assert.Equal("{}", recoveredToolCall.ArgumentsJson);
    }

    [Fact]
    public void WhenResponseDoesNotContainToolMarkupThenRecoveryReturnsEmpty()
    {
        IReadOnlyList<RecoveredMalformedToolCall> result = MalformedToolCallRecovery.Recover("Thinking", "Done");

        Assert.Empty(result);
    }
}
