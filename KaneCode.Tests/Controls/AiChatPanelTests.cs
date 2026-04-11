using KaneCode.Controls;
using KaneCode.Models;
using KaneCode.Services.Ai;

namespace KaneCode.Tests.Controls;

public class AiChatPanelTests
{
    [Fact]
    public void WhenRawTextModeDisabledThenDisplayedUserContentMatchesTypedText()
    {
        string typedText = "Explain this file";
        string outboundText = "Attached file context\n\nExplain this file";

        string result = AiChatPanel.GetDisplayedUserMessageContent(typedText, outboundText, showRawText: false);

        Assert.Equal(typedText, result);
    }

    [Fact]
    public void WhenRawTextModeEnabledThenDisplayedUserContentMatchesOutboundText()
    {
        string typedText = "Explain this file";
        string outboundText = "Attached file context\n\nExplain this file";

        string result = AiChatPanel.GetDisplayedUserMessageContent(typedText, outboundText, showRawText: true);

        Assert.Equal(outboundText, result);
    }

    [Fact]
    public void WhenTypedTextIsBlankThenDisplayedUserContentThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => AiChatPanel.GetDisplayedUserMessageContent(" ", "payload", showRawText: false));
    }

    [Fact]
    public void WhenFormattingRawTranscriptEntryThenLabelAndContentAreIncluded()
    {
        string result = AiChatPanel.FormatRawTranscriptEntry("Assistant", "Hello");

        Assert.Equal("Assistant:\nHello", result);
    }

    [Fact]
    public void WhenFormattingRawTranscriptEntryWithEmptyContentThenOnlyLabelIsIncluded()
    {
        string result = AiChatPanel.FormatRawTranscriptEntry("Assistant", string.Empty);

        Assert.Equal("Assistant:", result);
    }

    [Fact]
    public void WhenVerticalWhitespaceRemovalDisabledThenDisplayedAssistantContentIsUnchanged()
    {
        string content = "Line 1\n\nLine 2";

        string result = AiChatPanel.FormatDisplayedAssistantContent(content, removeVerticalWhitespace: false);

        Assert.Equal(content, result);
    }

    [Fact]
    public void WhenVerticalWhitespaceRemovalEnabledThenBlankLinesOutsideCodeBlocksAreRemoved()
    {
        string content = "Intro\n\nDetails\n\n- Item";

        string result = AiChatPanel.FormatDisplayedAssistantContent(content, removeVerticalWhitespace: true);

        Assert.Equal("Intro\nDetails\n- Item", result);
    }

    [Fact]
    public void WhenVerticalWhitespaceRemovalEnabledThenBlankLinesInsideCodeBlocksArePreserved()
    {
        string content = "Before\n\n```csharp\nint a = 1;\n\nint b = 2;\n```\n\nAfter";

        string result = AiChatPanel.FormatDisplayedAssistantContent(content, removeVerticalWhitespace: true);

        Assert.Equal("Before\n```csharp\nint a = 1;\n\nint b = 2;\n```\nAfter", result);
    }

    [Fact]
    public void WhenFormattingToolCallHeaderThenOnlyToolNameIsShown()
    {
        string result = AiChatPanel.FormatToolCallHeader("search_files");

        Assert.Equal("search_files", result);
    }

    [Fact]
    public void WhenFormattingToolCallBodyThenArgumentsRemainMultiLine()
    {
        string result = AiChatPanel.FormatToolCallBody("{\"query\":\"hotkey\",\"limit\":5}");

        Assert.Equal("query: hotkey\nlimit: 5", result);
    }

    [Fact]
    public void WhenSelectingPinnedSectionsThenOnlyExpandedSectionsCrossingThePinLineAreReturned()
    {
        IReadOnlyList<int> result = AiChatPanel.GetPinnedSectionIndexes(
            [(12d, 48d, true), (-20d, 36d, true), (-30d, -4d, true), (-8d, 28d, false)],
            0d);

        Assert.Equal([1], result);
    }

    [Fact]
    public void WhenMultipleExpandedSectionsCrossThePinLineThenTheirIndexesRemainInStreamOrder()
    {
        IReadOnlyList<int> result = AiChatPanel.GetPinnedSectionIndexes(
            [(-40d, 40d, true), (-10d, 60d, true), (24d, 72d, true)],
            0d);

        Assert.Equal([0, 1], result);
    }

    [Fact]
    public void WhenBuildingSelectableModelListThenPreferredModelIsPrependedOnce()
    {
        IReadOnlyList<string> result = AiChatPanel.BuildSelectableModelList(["gpt-4.1", "gpt-4o"], "gpt-4o");

        Assert.Equal(["gpt-4o", "gpt-4.1"], result);
    }

    [Fact]
    public void WhenSelectingInitialModelThenPreferredMatchIsReturnedIgnoringCase()
    {
        string? result = AiChatPanel.SelectInitialModel(["gpt-4o", "gpt-4.1"], "GPT-4.1");

        Assert.Equal("gpt-4.1", result);
    }

    [Fact]
    public void WhenBuildingPendingPromptContextThenReferencesAndSelectionAreCombinedOnce()
    {
        AiChatReference reference = new(AiReferenceKind.File, @"K:\Project\Example.cs")
        {
            Content = "class Example { }"
        };
        string result = AiChatPanel.BuildPendingPromptContext([reference], "Selection context:");
        string normalizedResult = result.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal("The user has attached the following context for this request:\n\n[File: Example.cs]\n```\nclass Example { }\n```\n\nSelection context:", normalizedResult);
    }

    [Fact]
    public void WhenBuildingRequestConversationHistoryThenPersistedUserMessageRemainsUnchanged()
    {
        List<AiChatMessage> persistedConversationHistory =
        [
            new(AiChatRole.System, "System prompt"),
            new(AiChatRole.User, "Explain this file")
        ];

        List<AiChatMessage> requestConversationHistory = AiChatPanel.BuildRequestConversationHistory(
            persistedConversationHistory,
            "Attached context\n\nExplain this file");

        Assert.Equal("Explain this file", persistedConversationHistory[^1].Content);
        Assert.Equal("Attached context\n\nExplain this file", requestConversationHistory[^1].Content);
    }
}
