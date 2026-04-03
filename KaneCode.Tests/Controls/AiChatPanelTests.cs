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
}
