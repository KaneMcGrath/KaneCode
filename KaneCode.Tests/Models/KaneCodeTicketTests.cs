using KaneCode.Models;

namespace KaneCode.Tests.Models;

public sealed class KaneCodeTicketTests
{
    [Fact]
    public void OverrideFlags_ReflectConfiguredOptions()
    {
        KaneCodeTicket ticket = new()
        {
            FilePath = "C:\\repo\\.kanecode\\tickets\\A.txt",
            FileName = "A.txt",
            Title = "A",
            Provider = "Deepseek",
            AgentMode = "agent"
        };

        Assert.True(ticket.HasProviderOverride);
        Assert.False(ticket.HasModelOverride);
        Assert.True(ticket.HasAgentModeOverride);
        Assert.True(ticket.HasOverrides);
    }

    [Fact]
    public void TerminalStates_AreRecognized()
    {
        KaneCodeTicket ticket = new()
        {
            FilePath = "C:\\repo\\.kanecode\\tickets\\A.txt",
            FileName = "A.txt",
            Title = "A"
        };

        Assert.False(ticket.IsTerminal);

        ticket.Status = TicketStatus.Complete;
        Assert.True(ticket.IsTerminal);

        ticket.Status = TicketStatus.Unable;
        Assert.True(ticket.IsTerminal);

        ticket.Status = TicketStatus.Failed;
        Assert.True(ticket.IsTerminal);
    }

    [Fact]
    public void ConfigurationSummary_ShowsDefaultsWhenUnset()
    {
        KaneCodeTicket ticket = new()
        {
            FilePath = "C:\\repo\\.kanecode\\tickets\\A.txt",
            FileName = "A.txt",
            Title = "A"
        };

        Assert.Equal("default · default · default", ticket.ConfigurationSummary);

        ticket.Provider = "Deepseek";
        ticket.Model = "v4";
        ticket.AgentMode = "Agent";

        Assert.Equal("Deepseek · v4 · Agent", ticket.ConfigurationSummary);
    }

    [Fact]
    public void StatusFormat_ReturnsTextAndGlyphsForEveryStatus()
    {
        foreach (TicketStatus status in Enum.GetValues<TicketStatus>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TicketStatusFormat.GetDisplayText(status)));
            Assert.False(string.IsNullOrWhiteSpace(TicketStatusFormat.GetGlyph(status)));
        }
    }
}
