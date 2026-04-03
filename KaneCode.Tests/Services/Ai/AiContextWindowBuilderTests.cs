using KaneCode.Services.Ai;

namespace KaneCode.Tests.Services.Ai;

public class AiContextWindowBuilderTests
{
    [Fact]
    public void WhenHistoryFitsBudgetThenNoCutoffOccurs()
    {
        List<AiChatMessage> history =
        [
            new AiChatMessage(AiChatRole.System, new string('s', 16)),
            new AiChatMessage(AiChatRole.User, new string('u', 16)),
            new AiChatMessage(AiChatRole.Assistant, new string('a', 16))
        ];

        AiContextWindowSnapshot result = AiContextWindowBuilder.Build(history, 20, includeToolMessages: true);

        Assert.False(result.Info.CutoffOccurred);
        Assert.Equal(3, result.Info.IncludedMessages);
        Assert.Equal(0, result.Info.DroppedMessages);
        Assert.Equal(12, result.Info.SelectedTokens);
    }

    [Fact]
    public void WhenBudgetWouldDropLatestUserThenLatestUserIsRetained()
    {
        List<AiChatMessage> history =
        [
            new AiChatMessage(AiChatRole.User, new string('u', 20)),
            new AiChatMessage(AiChatRole.Assistant, new string('a', 20)),
            new AiChatMessage(AiChatRole.Assistant, new string('b', 20))
        ];

        AiContextWindowSnapshot result = AiContextWindowBuilder.Build(history, 8, includeToolMessages: true);

        Assert.True(result.Info.CutoffOccurred);
        Assert.Single(result.Messages);
        Assert.Equal(AiChatRole.User, result.Messages[0].Role);
        Assert.Equal(2, result.Info.DroppedMessages);
        Assert.Equal(5, result.Info.SelectedTokens);
    }

    [Fact]
    public void WhenToolMessagesAreExcludedThenTheyAreReportedAsHidden()
    {
        AiChatMessage assistantToolCall = new(AiChatRole.Assistant, string.Empty)
        {
            ToolCalls =
            [
                new AiToolCallRequest("tool-1", "read_file", "{}")
            ]
        };

        List<AiChatMessage> history =
        [
            new AiChatMessage(AiChatRole.User, new string('u', 16)),
            assistantToolCall,
            new AiChatMessage(AiChatRole.Tool, new string('t', 16)),
            new AiChatMessage(AiChatRole.Assistant, new string('a', 16))
        ];

        AiContextWindowSnapshot result = AiContextWindowBuilder.Build(history, 20, includeToolMessages: false);

        Assert.Equal(2, result.Info.ExcludedMessages);
        Assert.Equal(2, result.Info.IncludedMessages);
        Assert.DoesNotContain(result.Messages, static message => message.Role == AiChatRole.Tool);
        Assert.DoesNotContain(result.Messages, static message => message.ToolCalls is { Count: > 0 });
    }
}
