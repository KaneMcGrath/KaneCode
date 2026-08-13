using System.Globalization;
using System.IO;
using KaneCode.Models;

namespace KaneCode.Services.Tickets;

/// <summary>
/// Reads and writes KaneCode ticket files stored under <c>.kanecode/tickets</c>.
///
/// A ticket is a plain text file. Its optional first line is the
/// <c>KaneCodeTicket|...</c> header, and the remaining lines are the task
/// description. Keeping tickets as plain text files lets external tooling and
/// other agents add, remove, and re-prioritize work while the IDE is running.
/// </summary>
internal sealed class TicketFileStore
{
    /// <summary>The per-project folder name holding IDE-specific state.</summary>
    internal const string KaneCodeFolderName = ".kanecode";

    /// <summary>The subfolder holding ticket files.</summary>
    internal const string TicketsFolderName = "tickets";

    /// <summary>The header prefix marking the first line of a ticket file.</summary>
    internal const string HeaderPrefix = "KaneCodeTicket";

    private readonly Func<string?> _projectRootProvider;

    public TicketFileStore(Func<string?> projectRootProvider)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
    }

    /// <summary>
    /// Returns the <c>.kanecode/tickets</c> directory for the loaded project,
    /// or null when no project is loaded.
    /// </summary>
    public string? TryGetTicketsDirectory()
    {
        string? projectRoot = _projectRootProvider();
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(projectRoot);
        string? rootDirectory = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath)
            : fullPath;

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return null;
        }

        return Path.Combine(rootDirectory, KaneCodeFolderName, TicketsFolderName);
    }

    /// <summary>
    /// Ensures the tickets directory exists, creating it (and the parent
    /// <c>.kanecode</c> folder) as needed. Returns null when no project is loaded.
    /// </summary>
    public string? EnsureTicketsDirectory()
    {
        string? directory = TryGetTicketsDirectory();
        if (directory is null)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Scans the tickets directory and returns all ticket files, ordered oldest-first
    /// by file creation time (then alphabetically by file name). Tickets that cannot
    /// be read are skipped rather than throwing, so a single transient IO problem
    /// never blocks the whole scan.
    /// </summary>
    public IReadOnlyList<KaneCodeTicket> ScanTickets()
    {
        string? directory = TryGetTicketsDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        List<KaneCodeTicket> tickets = [];
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string filePath in files)
        {
            try
            {
                tickets.Add(ReadTicket(filePath));
            }
            catch (IOException)
            {
                // Skip unreadable tickets.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip unreadable tickets.
            }
        }

        return tickets
            .OrderBy(ticket => ticket.CreatedUtc)
            .ThenBy(ticket => ticket.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads a single ticket file from disk.
    /// </summary>
    public KaneCodeTicket ReadTicket(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        string content = File.ReadAllText(fullPath);
        (string firstLine, string body) = SplitFirstLine(content);

        HeaderParseResult header = ParseHeaderLine(firstLine);

        KaneCodeTicket ticket = new()
        {
            FilePath = fullPath,
            FileName = Path.GetFileName(fullPath),
            Title = Path.GetFileNameWithoutExtension(fullPath),
            CreatedUtc = File.GetCreationTimeUtc(fullPath),
            UpdatedUtc = File.GetLastWriteTimeUtc(fullPath),
            Description = body
        };

        if (!header.HasHeader)
        {
            ticket.Status = TicketStatus.Initialize;
            // No header line exists, so the entire file content is the description.
            ticket.Description = content;
            return ticket;
        }

        ticket.Status = header.Status;
        ticket.Provider = header.Provider;
        ticket.Model = header.Model;
        ticket.AgentMode = header.AgentMode;
        ticket.Priority = header.Priority;
        ticket.StartAfter = header.StartAfter;
        return ticket;
    }

    /// <summary>
    /// Writes a <c>KaneCodeTicket</c> header line into a ticket file, preserving the
    /// task description. When the file already has a header, it is replaced in place;
    /// otherwise a header is prepended to the top of the file.
    /// </summary>
    public void WriteHeader(KaneCodeTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        string headerLine = BuildHeaderLine(
            ticket.Status,
            ticket.Provider,
            ticket.Model,
            ticket.AgentMode,
            ticket.Priority,
            ticket.StartAfter);

        string content = File.ReadAllText(ticket.FilePath);
        bool hasHeader = HasHeaderLine(content);

        string newContent;
        if (hasHeader)
        {
            (_, string body) = SplitFirstLine(content);
            newContent = headerLine + "\n" + body;
        }
        else
        {
            // No header existed — the entire original content is the description.
            newContent = content.Length == 0
                ? headerLine + "\n"
                : headerLine + "\n" + content;
        }

        File.WriteAllText(ticket.FilePath, newContent);
        ticket.UpdatedUtc = File.GetLastWriteTimeUtc(ticket.FilePath);
    }

    /// <summary>
    /// Returns true when the file content begins with a <c>KaneCodeTicket</c> header
    /// line on its first line.
    /// </summary>
    private static bool HasHeaderLine(string content)
    {
        (string firstLine, _) = SplitFirstLine(content);
        return !string.IsNullOrWhiteSpace(firstLine) &&
            firstLine.TrimStart().StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a new ticket file from a title and description. Returns the path of
    /// the created file. A unique file name is generated when a file with the same
    /// title already exists.
    /// </summary>
    public string CreateTicket(
        string title,
        string description,
        TicketStatus status = TicketStatus.Open,
        string? provider = null,
        string? model = null,
        string? agentMode = null,
        int priority = 0,
        string? startAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        string? directory = EnsureTicketsDirectory()
            ?? throw new InvalidOperationException("No project is loaded; cannot create a ticket.");

        string safeTitle = SanitizeFileName(title);
        string filePath = Path.Combine(directory, safeTitle + ".txt");

        // Avoid silently overwriting an existing ticket with the same title.
        int suffix = 2;
        while (File.Exists(filePath))
        {
            filePath = Path.Combine(directory, $"{safeTitle} ({suffix}).txt");
            suffix++;
        }

        string headerLine = BuildHeaderLine(status, provider, model, agentMode, priority, startAfter);
        string content = headerLine + "\n" + (description ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        File.WriteAllText(filePath, content);
        return filePath;
    }

    /// <summary>
    /// Deletes a ticket file from disk.
    /// </summary>
    public void DeleteTicket(KaneCodeTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (File.Exists(ticket.FilePath))
        {
            File.Delete(ticket.FilePath);
        }
    }

    /// <summary>
    /// Ensures every <see cref="TicketStatus.Initialize"/> ticket (a file with no
    /// header) has a header written, promoting it to <see cref="TicketStatus.Open"/>.
    /// </summary>
    public void InitializeHeaderlessTickets()
    {
        foreach (KaneCodeTicket ticket in ScanTickets())
        {
            if (ticket.Status != TicketStatus.Initialize)
            {
                continue;
            }

            try
            {
                ticket.Status = TicketStatus.Open;
                WriteHeader(ticket);
            }
            catch (IOException)
            {
                // Leave for the next scan.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave for the next scan.
            }
        }
    }

    /// <summary>
    /// Splits a file's text into its first line (without any trailing CR) and the
    /// remainder of the text.
    /// </summary>
    internal static (string FirstLine, string Body) SplitFirstLine(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        int newlineIndex = content.IndexOf('\n');
        if (newlineIndex < 0)
        {
            return (content.TrimEnd('\r'), string.Empty);
        }

        string firstLine = content[..newlineIndex].TrimEnd('\r');
        string body = content[(newlineIndex + 1)..];
        return (firstLine, body);
    }

    /// <summary>
    /// Parses a ticket header line. A line that does not start with
    /// <see cref="HeaderPrefix"/> yields <see cref="HeaderParseResult.HasHeader"/> = false.
    /// A malformed header yields <see cref="TicketStatus.Error"/>.
    /// </summary>
    internal static HeaderParseResult ParseHeaderLine(string? firstLine)
    {
        if (string.IsNullOrWhiteSpace(firstLine) ||
            !firstLine.TrimStart().StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return HeaderParseResult.NoHeader();
        }

        string[] segments = SplitHeaderSegments(firstLine);
        if (segments.Length == 0 ||
            !string.Equals(segments[0].Trim(), HeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return HeaderParseResult.Error();
        }

        string? provider = null;
        string? model = null;
        string? agentMode = null;
        int priority = 0;
        string? startAfter = null;
        TicketStatus? status = null;
        bool statusInvalid = false;

        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i];
            (string key, string value) = SplitOption(segment);
            if (key.Length == 0)
            {
                continue;
            }

            if (string.Equals(key, "Status", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseStatus(value, out TicketStatus parsed))
                {
                    status = parsed;
                }
                else
                {
                    statusInvalid = true;
                }

                continue;
            }

            if (string.Equals(key, "Provider", StringComparison.OrdinalIgnoreCase))
            {
                provider = NullIfEmpty(value);
                continue;
            }

            if (string.Equals(key, "Model", StringComparison.OrdinalIgnoreCase))
            {
                model = NullIfEmpty(value);
                continue;
            }

            if (string.Equals(key, "AgentMode", StringComparison.OrdinalIgnoreCase))
            {
                agentMode = NullIfEmpty(value);
                continue;
            }

            if (string.Equals(key, "Priority", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPriority))
                {
                    priority = parsedPriority;
                }

                continue;
            }

            if (string.Equals(key, "StartAfter", StringComparison.OrdinalIgnoreCase))
            {
                startAfter = NullIfEmpty(value);
                continue;
            }

            // Unknown options are ignored for forward compatibility.
        }

        if (statusInvalid)
        {
            return HeaderParseResult.Error();
        }

        return HeaderParseResult.Ok(
            status ?? TicketStatus.Open,
            provider,
            model,
            agentMode,
            priority,
            startAfter);
    }

    /// <summary>
    /// Serializes a ticket header line. Status is written with an <c>=</c> separator
    /// (matching the documented example) and options use a <c>:</c> separator.
    /// String values are double-quoted so values containing spaces round-trip safely.
    /// </summary>
    internal static string BuildHeaderLine(
        TicketStatus status,
        string? provider,
        string? model,
        string? agentMode,
        int priority,
        string? startAfter)
    {
        List<string> segments = [HeaderPrefix, $"Status={GetStatusToken(status)}"];

        if (!string.IsNullOrWhiteSpace(provider))
        {
            segments.Add($"Provider:{Quote(provider)}");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            segments.Add($"Model:{Quote(model)}");
        }

        if (!string.IsNullOrWhiteSpace(agentMode))
        {
            segments.Add($"AgentMode:{Quote(agentMode)}");
        }

        if (priority != 0)
        {
            segments.Add($"Priority:{priority.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(startAfter))
        {
            segments.Add($"StartAfter:{Quote(startAfter)}");
        }

        return string.Join('|', segments);
    }

    /// <summary>
    /// Splits a header line into <c>|</c>-separated segments while respecting double
    /// quotes, so option values containing a pipe character round-trip correctly.
    /// </summary>
    internal static string[] SplitHeaderSegments(string headerLine)
    {
        ArgumentNullException.ThrowIfNull(headerLine);

        List<string> segments = [];
        System.Text.StringBuilder current = new();
        bool inQuotes = false;

        foreach (char c in headerLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == '|' && !inQuotes)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        segments.Add(current.ToString());
        return segments.ToArray();
    }

    /// <summary>
    /// Splits an <c>Option:Value</c> or <c>Option=Value</c> segment, stripping
    /// surrounding double quotes from the value.
    /// </summary>
    internal static (string Key, string Value) SplitOption(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return (string.Empty, string.Empty);
        }

        int separatorIndex = -1;
        for (int i = 0; i < segment.Length; i++)
        {
            if (segment[i] is ':' or '=')
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex < 0)
        {
            return (segment.Trim(), string.Empty);
        }

        string key = segment[..separatorIndex].Trim();
        string value = segment[(separatorIndex + 1)..].Trim();
        value = Unquote(value);
        return (key, value);
    }

    /// <summary>
    /// Parses a status token, accepting both the enum names and the legacy
    /// <c>Pause</c> alias used by the original example header.
    /// </summary>
    internal static bool TryParseStatus(string value, out TicketStatus status)
    {
        if (string.Equals(value, "Pause", StringComparison.OrdinalIgnoreCase))
        {
            status = TicketStatus.Paused;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out status);
    }

    internal static string GetStatusToken(TicketStatus status)
    {
        return status switch
        {
            TicketStatus.Initialize => "Initialize",
            TicketStatus.Error => "Error",
            TicketStatus.Blocked => "Blocked",
            TicketStatus.Ignore => "Ignore",
            TicketStatus.Open => "Open",
            TicketStatus.Working => "Working",
            TicketStatus.Paused => "Paused",
            TicketStatus.Complete => "Complete",
            TicketStatus.Unable => "Unable",
            TicketStatus.Failed => "Failed",
            _ => status.ToString()
        };
    }

    private static string Quote(string value)
    {
        // Escape any embedded double quote by doubling it, then wrap in quotes.
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string SanitizeFileName(string title)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        System.Text.StringBuilder builder = new(title.Length);
        foreach (char c in title.Trim())
        {
            builder.Append(invalidChars.Contains(c) ? '_' : c);
        }

        string result = builder.ToString().Trim();
        return result.Length == 0 ? "Untitled" : result;
    }
}

/// <summary>
/// The result of parsing a ticket header line.
/// </summary>
internal readonly record struct HeaderParseResult(
    bool HasHeader,
    TicketStatus Status,
    string? Provider,
    string? Model,
    string? AgentMode,
    int Priority,
    string? StartAfter)
{
    internal static HeaderParseResult NoHeader()
    {
        return new HeaderParseResult(false, TicketStatus.Initialize, null, null, null, 0, null);
    }

    internal static HeaderParseResult Error()
    {
        return new HeaderParseResult(true, TicketStatus.Error, null, null, null, 0, null);
    }

    internal static HeaderParseResult Ok(
        TicketStatus status,
        string? provider,
        string? model,
        string? agentMode,
        int priority,
        string? startAfter)
    {
        return new HeaderParseResult(true, status, provider, model, agentMode, priority, startAfter);
    }
}
