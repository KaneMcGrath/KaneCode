using KaneCode.Controls;

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
    public void WhenFormattingToolCallHeaderThenStatusAndArgumentsAreFlattenedIntoSingleLine()
    {
        string result = AiChatPanel.FormatToolCallHeader("search_files", "{\"query\":\"hotkey\",\"limit\":5}", "Running");

        Assert.Equal("Running | search_files — query: hotkey • limit: 5", result);
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
}
