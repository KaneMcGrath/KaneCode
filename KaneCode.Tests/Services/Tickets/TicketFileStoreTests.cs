using System.IO;
using KaneCode.Models;
using KaneCode.Services.Tickets;

namespace KaneCode.Tests.Services.Tickets;

public sealed class TicketFileStoreTests
{
    private static string CreateTempProjectRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "kanecode-tickets-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ParseHeaderLine_WithNoHeader_ReturnsNoHeader()
    {
        HeaderParseResult result = TicketFileStore.ParseHeaderLine("Fix the build");

        Assert.False(result.HasHeader);
        Assert.Equal(TicketStatus.Initialize, result.Status);
    }

    [Fact]
    public void ParseHeaderLine_WithBarePrefix_DefaultsToOpen()
    {
        HeaderParseResult result = TicketFileStore.ParseHeaderLine("KaneCodeTicket");

        Assert.True(result.HasHeader);
        Assert.Equal(TicketStatus.Open, result.Status);
        Assert.Null(result.Provider);
        Assert.Null(result.Model);
        Assert.Null(result.AgentMode);
        Assert.Equal(0, result.Priority);
        Assert.Null(result.StartAfter);
    }

    [Fact]
    public void ParseHeaderLine_WithExampleHeader_ParsesAllOptions()
    {
        HeaderParseResult result = TicketFileStore.ParseHeaderLine(
            "KaneCodeTicket|Status=Pause|Provider:\"Deepseek\"|Model:\"Deepseek-v4-flash\"|AgentMode:\"Agent3\"|Priority:2|StartAfter:\"Example Ticket\"");

        Assert.True(result.HasHeader);
        Assert.Equal(TicketStatus.Paused, result.Status);
        Assert.Equal("Deepseek", result.Provider);
        Assert.Equal("Deepseek-v4-flash", result.Model);
        Assert.Equal("Agent3", result.AgentMode);
        Assert.Equal(2, result.Priority);
        Assert.Equal("Example Ticket", result.StartAfter);
    }

    [Fact]
    public void ParseHeaderLine_WithEqualsAndColonSeparators_IsTolerant()
    {
        HeaderParseResult result = TicketFileStore.ParseHeaderLine(
            "KaneCodeTicket|Status=Complete|Provider=OpenAI|Model:\"gpt-4o\"");

        Assert.Equal(TicketStatus.Complete, result.Status);
        Assert.Equal("OpenAI", result.Provider);
        Assert.Equal("gpt-4o", result.Model);
    }

    [Fact]
    public void ParseHeaderLine_WithUnknownOptions_IgnoresThem()
    {
        HeaderParseResult result = TicketFileStore.ParseHeaderLine(
            "KaneCodeTicket|Status=Open|Something:value|Priority:5");

        Assert.Equal(TicketStatus.Open, result.Status);
        Assert.Equal(5, result.Priority);
    }

    [Fact]
    public void ParseHeaderLine_WithInvalidStatus_ReturnsError()
    {
        HeaderParseResult result = TicketFileStore.ParseHeaderLine("KaneCodeTicket|Status=Banana");

        Assert.True(result.HasHeader);
        Assert.Equal(TicketStatus.Error, result.Status);
    }

    [Fact]
    public void ParseHeaderLine_WithQuotedValueContainingPipes_PreservesValue()
    {
        HeaderParseResult result = TicketFileStore.ParseHeaderLine(
            "KaneCodeTicket|Status=Open|Model:\"a|b\"");

        Assert.Equal(TicketStatus.Open, result.Status);
        Assert.Equal("a|b", result.Model);
    }

    [Fact]
    public void BuildHeaderLine_RoundTripsThroughParse()
    {
        string header = TicketFileStore.BuildHeaderLine(
            TicketStatus.Working,
            "Deepseek",
            "Deepseek-v4-flash",
            "Agent3",
            7,
            "Parent Ticket");

        HeaderParseResult parsed = TicketFileStore.ParseHeaderLine(header);

        Assert.Equal(TicketStatus.Working, parsed.Status);
        Assert.Equal("Deepseek", parsed.Provider);
        Assert.Equal("Deepseek-v4-flash", parsed.Model);
        Assert.Equal("Agent3", parsed.AgentMode);
        Assert.Equal(7, parsed.Priority);
        Assert.Equal("Parent Ticket", parsed.StartAfter);
    }

    [Fact]
    public void BuildHeaderLine_OmitsUnsetOptions()
    {
        string header = TicketFileStore.BuildHeaderLine(TicketStatus.Open, null, null, null, 0, null);

        Assert.Equal("KaneCodeTicket|Status=Open", header);
    }

    [Fact]
    public void SplitOption_HandlesColonEqualsAndQuotes()
    {
        Assert.Equal(("Provider", "Deepseek"), TicketFileStore.SplitOption("Provider:\"Deepseek\""));
        Assert.Equal(("Status", "Pause"), TicketFileStore.SplitOption("Status=Pause"));
        Assert.Equal(("Priority", "2"), TicketFileStore.SplitOption("Priority:2"));
        Assert.Equal(("Empty", ""), TicketFileStore.SplitOption("Empty"));
    }

    [Fact]
    public void TryParseStatus_AcceptsEnumNamesAndLegacyPauseAlias()
    {
        Assert.True(TicketFileStore.TryParseStatus("Pause", out TicketStatus pauseStatus));
        Assert.Equal(TicketStatus.Paused, pauseStatus);

        Assert.True(TicketFileStore.TryParseStatus("complete", out TicketStatus completeStatus));
        Assert.Equal(TicketStatus.Complete, completeStatus);

        Assert.True(TicketFileStore.TryParseStatus("Working", out TicketStatus workingStatus));
        Assert.Equal(TicketStatus.Working, workingStatus);

        Assert.False(TicketFileStore.TryParseStatus("Nope", out _));
    }

    [Fact]
    public void SplitFirstLine_SplitsHeaderFromBody()
    {
        (string firstLine, string body) = TicketFileStore.SplitFirstLine("KaneCodeTicket|Status=Open\r\nline1\r\nline2");

        Assert.Equal("KaneCodeTicket|Status=Open", firstLine);
        Assert.Equal("line1\r\nline2", body);
    }

    [Fact]
    public void ScanTickets_ReadsAndOrdersTicketsOldestFirst()
    {
        string root = CreateTempProjectRoot();
        try
        {
            TicketFileStore store = new(() => root);

            string first = store.CreateTicket("Alpha", "body A");
            string second = store.CreateTicket("Beta", "body B");

            // Force a deterministic creation-time ordering by setting explicit times.
            DateTime baseTime = DateTime.UtcNow.AddHours(-2);
            File.SetCreationTimeUtc(first, baseTime);
            File.SetCreationTimeUtc(second, baseTime.AddHours(1));

            IReadOnlyList<KaneCodeTicket> tickets = store.ScanTickets();

            Assert.Equal(2, tickets.Count);
            Assert.Equal("Alpha", tickets[0].Title);
            Assert.Equal("Beta", tickets[1].Title);
            Assert.Equal(TicketStatus.Open, tickets[0].Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteHeader_PreservesBodyAndReplacesExistingHeader()
    {
        string root = CreateTempProjectRoot();
        try
        {
            TicketFileStore store = new(() => root);
            string path = store.CreateTicket("T", "line one\nline two");

            KaneCodeTicket ticket = store.ReadTicket(path);
            ticket.Status = TicketStatus.Complete;
            store.WriteHeader(ticket);

            KaneCodeTicket reread = store.ReadTicket(path);
            Assert.Equal(TicketStatus.Complete, reread.Status);
            Assert.Equal("line one\nline two", reread.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitializeHeaderlessTickets_AddsHeaderToPlainFiles()
    {
        string root = CreateTempProjectRoot();
        try
        {
            TicketFileStore store = new(() => root);
            string ticketsDir = store.EnsureTicketsDirectory()!;
            string plainPath = Path.Combine(ticketsDir, "Plain.txt");
            File.WriteAllText(plainPath, "Just a body");

            KaneCodeTicket before = store.ReadTicket(plainPath);
            Assert.Equal(TicketStatus.Initialize, before.Status);

            store.InitializeHeaderlessTickets();

            KaneCodeTicket after = store.ReadTicket(plainPath);
            Assert.Equal(TicketStatus.Open, after.Status);
            Assert.Equal("Just a body", after.Description);
            Assert.StartsWith("KaneCodeTicket|", File.ReadAllLines(plainPath)[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateTicket_AvoidsOverwritingExistingTitle()
    {
        string root = CreateTempProjectRoot();
        try
        {
            TicketFileStore store = new(() => root);
            string first = store.CreateTicket("Same", "one");
            string second = store.CreateTicket("Same", "two");

            Assert.NotEqual(first, second);
            Assert.Equal(2, store.ScanTickets().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryGetTicketsDirectory_ResolvesProjectFileToItsDirectory()
    {
        string root = CreateTempProjectRoot();
        try
        {
            string projectFile = Path.Combine(root, "App.csproj");
            File.WriteAllText(projectFile, "<Project />");

            TicketFileStore store = new(() => projectFile);
            string? ticketsDir = store.TryGetTicketsDirectory();

            Assert.NotNull(ticketsDir);
            Assert.Equal(
                Path.Combine(root, ".kanecode", "tickets"),
                ticketsDir);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
