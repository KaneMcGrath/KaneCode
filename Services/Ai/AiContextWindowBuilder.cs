namespace KaneCode.Services.Ai;

/// <summary>
/// Summary of the conversation window selected for an outbound AI request.
/// </summary>
internal sealed record AiContextWindowInfo(
    int BudgetTokens,
    int SelectedTokens,
    int TotalHistoryMessages,
    int TotalConsideredMessages,
    int IncludedMessages,
    int DroppedMessages,
    int ExcludedMessages,
    bool CutoffOccurred,
    bool IncludesToolMessages);

/// <summary>
/// The outbound conversation window and the metadata describing it.
/// </summary>
internal sealed record AiContextWindowSnapshot(
    IReadOnlyList<AiChatMessage> Messages,
    AiContextWindowInfo Info);

/// <summary>
/// Builds a budgeted conversation window for outbound chat requests.
/// </summary>
internal static class AiContextWindowBuilder
{
    internal static AiContextWindowSnapshot Build(
        IReadOnlyList<AiChatMessage> conversationHistory,
        int outboundTokenBudget,
        bool includeToolMessages)
    {
        ArgumentNullException.ThrowIfNull(conversationHistory);

        if (outboundTokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outboundTokenBudget));
        }

        List<AiChatMessage> consideredMessages = conversationHistory
            .Where(message => includeToolMessages || !IsToolRelatedMessage(message))
            .ToList();

        List<AiChatMessage> selectedMessages = [];
        int selectedTokens = 0;
        bool hasUserMessage = false;

        for (int index = consideredMessages.Count - 1; index >= 0; index--)
        {
            AiChatMessage message = consideredMessages[index];
            int cost = EstimateTokens(message.Content);

            if (selectedMessages.Count > 0 && selectedTokens + cost > outboundTokenBudget)
            {
                break;
            }

            selectedMessages.Add(message);
            selectedTokens += cost;

            if (message.Role == AiChatRole.User)
            {
                hasUserMessage = true;
            }
        }

        if (!hasUserMessage)
        {
            AiChatMessage? latestUserMessage = consideredMessages.LastOrDefault(static message => message.Role == AiChatRole.User);
            if (latestUserMessage is not null && !selectedMessages.Any(message => ReferenceEquals(message, latestUserMessage)))
            {
                int latestUserCost = EstimateTokens(latestUserMessage.Content);

                while (selectedMessages.Count > 0 && selectedTokens + latestUserCost > outboundTokenBudget)
                {
                    int removeIndex = selectedMessages.Count - 1;
                    selectedTokens -= EstimateTokens(selectedMessages[removeIndex].Content);
                    selectedMessages.RemoveAt(removeIndex);
                }

                selectedMessages.Add(latestUserMessage);
                selectedTokens += latestUserCost;
            }
        }

        selectedMessages.Reverse();

        int excludedMessages = conversationHistory.Count - consideredMessages.Count;
        int droppedMessages = Math.Max(0, consideredMessages.Count - selectedMessages.Count);
        AiContextWindowInfo info = new(
            outboundTokenBudget,
            selectedTokens,
            conversationHistory.Count,
            consideredMessages.Count,
            selectedMessages.Count,
            droppedMessages,
            excludedMessages,
            droppedMessages > 0,
            includeToolMessages);

        return new AiContextWindowSnapshot(selectedMessages, info);
    }

    internal static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 1;
        }

        return Math.Max(1, text.Length / 4);
    }

    private static bool IsToolRelatedMessage(AiChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Role == AiChatRole.Tool ||
               (message.Role == AiChatRole.Assistant && message.ToolCalls is { Count: > 0 });
    }
}
