namespace KaneCode.Models;

/// <summary>
/// One discrete build/run/test (or git) event shown in the Build Output panel.
/// Each event owns its own lines so the panel can render it in a separate text
/// area, visually separated from other events by a horizontal rule.
/// </summary>
public sealed class BuildOutputEvent
{
    private readonly List<string> _lines = [];

    /// <summary>The lines produced by this event, in order.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>All lines joined with newlines (no trailing newline).</summary>
    public string Text => string.Join(Environment.NewLine, _lines);

    /// <summary>
    /// Raised after a line is appended, carrying the event and the appended line.
    /// </summary>
    public event Action<BuildOutputEvent, string>? LineAppended;

    /// <summary>Appends a line to this event.</summary>
    public void AppendLine(string line)
    {
        _lines.Add(line);
        LineAppended?.Invoke(this, line);
    }
}
