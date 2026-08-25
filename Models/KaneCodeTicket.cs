using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KaneCode.Models;

/// <summary>
/// Represents a single KaneCode ticket discovered under <c>.kanecode/tickets</c>.
///
/// A ticket is a plain text file. The first line (when present) is the
/// <c>KaneCodeTicket|...</c> header carrying status and per-ticket options; the
/// remaining lines are the free-form task description handed to the agent.
/// </summary>
public sealed class KaneCodeTicket : INotifyPropertyChanged
{
    /// <summary>The full path to the ticket file on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>The ticket title — the file name without its extension.</summary>
    public required string Title { get; init; }

    /// <summary>The file name (including extension) of the ticket file.</summary>
    public required string FileName { get; init; }

    /// <summary>The current lifecycle status.</summary>
    public TicketStatus Status { get; set; } = TicketStatus.Initialize;

    /// <summary>Optional per-ticket provider (ID or user label). Null inherits the IDE default.</summary>
    public string? Provider { get; set; }

    /// <summary>Optional per-ticket model. Null inherits the IDE default.</summary>
    public string? Model { get; set; }

    /// <summary>Optional per-ticket agent mode id. Null inherits the IDE default.</summary>
    public string? AgentMode { get; set; }

    /// <summary>Dispatch priority. Higher values are dispatched first.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// Optional title of another ticket that must reach <see cref="TicketStatus.Complete"/>
    /// before this ticket may be dispatched.
    /// </summary>
    public string? StartAfter { get; set; }

    /// <summary>The free-form task description (everything after the first line).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The file creation time in UTC, used for oldest-first ordering.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>The file last-write time in UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>When non-null, the id of the agent currently working on this ticket.</summary>
    public string? ActiveAgentId { get; set; }

    /// <summary>Human-readable name of the agent currently working on this ticket.</summary>
    public string? ActiveAgentDisplayName { get; set; }

    /// <summary>The ticket worktree path, when the ticket was dispatched in a Git worktree.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// Whether the ticket's collapsible "worktree changes" section is expanded in the
    /// Tickets panel. UI state only — not persisted to the ticket file.
    /// </summary>
    public bool ChangesExpanded
    {
        get => _changesExpanded;
        set
        {
            if (_changesExpanded == value)
            {
                return;
            }

            _changesExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Status line shown above the worktree change list (e.g. "3 changed files").
    /// </summary>
    public string WorktreeChangesStatusText
    {
        get => _worktreeChangesStatusText;
        set
        {
            if (_worktreeChangesStatusText == value)
            {
                return;
            }

            _worktreeChangesStatusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The files the agent changed in the ticket's worktree, shown when the changes
    /// section is expanded. Populated lazily by the Tickets panel.
    /// </summary>
    public ObservableCollection<TicketWorktreeChange> WorktreeChanges { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool _changesExpanded;
    private string _worktreeChangesStatusText = string.Empty;

    /// <summary>Whether the ticket requests a provider override.</summary>
    public bool HasProviderOverride => !string.IsNullOrWhiteSpace(Provider);

    /// <summary>Whether the ticket requests a model override.</summary>
    public bool HasModelOverride => !string.IsNullOrWhiteSpace(Model);

    /// <summary>Whether the ticket requests an agent-mode override.</summary>
    public bool HasAgentModeOverride => !string.IsNullOrWhiteSpace(AgentMode);

    /// <summary>Whether the ticket requests any provider/model/mode override.</summary>
    public bool HasOverrides => HasProviderOverride || HasModelOverride || HasAgentModeOverride;

    /// <summary>Whether the ticket is finished and will no longer be dispatched.</summary>
    public bool IsTerminal =>
        Status is TicketStatus.Complete or TicketStatus.Unable or TicketStatus.Failed;

    /// <summary>Human-readable status text.</summary>
    public string StatusText => TicketStatusFormat.GetDisplayText(Status);

    /// <summary>A single glyph used to render the status visually in the panel.</summary>
    public string StatusGlyph => TicketStatusFormat.GetGlyph(Status);

    /// <summary>A short summary line shown under the title (provider/model/mode).</summary>
    public string ConfigurationSummary
    {
        get
        {
            string provider = string.IsNullOrWhiteSpace(Provider) ? "default" : Provider!;
            string model = string.IsNullOrWhiteSpace(Model) ? "default" : Model!;
            string mode = string.IsNullOrWhiteSpace(AgentMode) ? "default" : AgentMode!;
            return $"{provider} · {model} · {mode}";
        }
    }
}

/// <summary>
/// Formatting helpers for <see cref="TicketStatus"/> values.
/// </summary>
public static class TicketStatusFormat
{
    /// <summary>Returns the human-readable display text for a status.</summary>
    public static string GetDisplayText(TicketStatus status)
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

    /// <summary>Returns the glyph used to render a status in the ticket panel.</summary>
    public static string GetGlyph(TicketStatus status)
    {
        return status switch
        {
            TicketStatus.Initialize => "\u2B50",       // ⭐
            TicketStatus.Error => "\u26D4",            // ⛔
            TicketStatus.Blocked => "\uD83D\uDEAB",    // 🚫
            TicketStatus.Ignore => "\uD83D\uDE37",     // 😷 (skipped)
            TicketStatus.Open => "\uD83D\uDCE5",       // 📥
            TicketStatus.Working => "\uD83D\uDD27",    // 🔧
            TicketStatus.Paused => "\u23F8\uFE0F",     // ⏸️
            TicketStatus.Complete => "\u2705",         // ✅
            TicketStatus.Unable => "\u2753",           // ❓
            TicketStatus.Failed => "\u274C",           // ❌
            _ => "\u26AA"                              // ⚪
        };
    }
}
