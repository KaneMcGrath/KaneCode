using KaneCode.Models;
using KaneCode.Services.Tickets;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace KaneCode.Controls;

/// <summary>
/// The Tickets panel — lists every KaneCode ticket found under
/// <c>.kanecode/tickets</c>, shows which ones have active agents, and provides an
/// Initialize button to start the autonomous ticket dispatch loop.
///
/// Listing tickets is independent of the dispatch loop: the panel rescans whenever the
/// loaded project changes, the panel comes back into view, or the tickets folder changes
/// on disk, so tickets are visible (and can be opened, ignored, reopened, or deleted)
/// before Initialize is ever pressed.
/// </summary>
public partial class TicketPanel : UserControl
{
    /// <summary>
    /// How long file-change notifications are coalesced before rescanning. A single
    /// ticket write raises several events, and an agent can touch a batch of tickets
    /// at once, so the rescan waits for the burst to settle.
    /// </summary>
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(300);

    private readonly ObservableCollection<KaneCodeTicket> _tickets = [];
    private TicketSystem? _ticketSystem;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherRefreshTimer;
    private string? _watchedDirectory;

    /// <summary>Raised when the user double-clicks an active ticket (to watch its agent).</summary>
    internal event EventHandler<KaneCodeTicket>? TicketActivated;

    /// <summary>Raised when the user clicks the New Ticket button.</summary>
    internal event EventHandler? NewTicketRequested;

    /// <summary>Raised when the user chooses "Open" on a ticket (open the file in the editor).</summary>
    internal event EventHandler<KaneCodeTicket>? TicketOpenRequested;

    public TicketPanel()
    {
        InitializeComponent();
        TicketList.ItemsSource = _tickets;
        IsVisibleChanged += TicketPanel_IsVisibleChanged;
        Unloaded += (_, _) => StopWatching();
        UpdateStatusText();
    }

    /// <summary>
    /// Rescans when the panel is shown, and runs the tickets-folder watcher only while
    /// the panel is in view. Tickets are plain files that agents and external tools
    /// create at any time, so the list would otherwise go stale whenever the dispatch
    /// loop is not running to refresh it — but a watcher left armed behind a hidden tab
    /// would be pure overhead.
    /// </summary>
    private void TicketPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RefreshFromSystem();
            StartWatching();
            return;
        }

        StopWatching();
    }

    internal void SetTicketSystem(TicketSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        if (_ticketSystem is not null)
        {
            _ticketSystem.TicketsChanged -= TicketSystem_TicketsChanged;
            _ticketSystem.StateChanged -= TicketSystem_StateChanged;
            _ticketSystem.DispatchIssue -= TicketSystem_DispatchIssue;
        }

        _ticketSystem = system;
        _ticketSystem.TicketsChanged += TicketSystem_TicketsChanged;
        _ticketSystem.StateChanged += TicketSystem_StateChanged;
        _ticketSystem.DispatchIssue += TicketSystem_DispatchIssue;

        RefreshFromSystem();
        ApplyDispatchIssue(new TicketDispatchIssueEventArgs(null, system.LastDispatchIssue));

        // The panel is wired to the ticket system after it is first shown, so the
        // watcher has to be armed here rather than only on the visibility change.
        if (IsVisible)
        {
            StartWatching();
        }
    }

    /// <summary>
    /// Updates the provider/model/mode summary shown in the header.
    /// </summary>
    internal void SetHeaderInfo(string provider, string model, string mode)
    {
        HeaderInfoText.Text = $"{provider} · {model} · {mode}";
    }

    /// <summary>Refreshes the ticket list from the ticket system.</summary>
    internal void Refresh()
    {
        RefreshFromSystem();

        // A refresh usually follows a project load, which moves the tickets folder, so
        // the watcher is re-pointed at the new location.
        if (IsVisible)
        {
            StartWatching();
        }
    }

    // ── Tickets folder watcher ──────────────────────────────────────

    /// <summary>
    /// Arms the tickets-folder watcher. When <c>.kanecode/tickets</c> does not exist yet,
    /// the nearest existing ancestor is watched instead — non-recursively, so watching a
    /// project root stays cheap — and the watcher re-points itself one level deeper once
    /// the folder appears.
    /// </summary>
    private void StartWatching()
    {
        string? target = ResolveWatchTarget(_ticketSystem?.TicketsDirectory);
        if (target is null)
        {
            StopWatching();
            return;
        }

        if (_watcher is not null &&
            string.Equals(_watchedDirectory, target, StringComparison.OrdinalIgnoreCase))
        {
            _watcher.EnableRaisingEvents = true;
            return;
        }

        StopWatching();

        try
        {
            FileSystemWatcher watcher = new(target)
            {
                // DirectoryName is needed for the ancestor case (spotting the tickets
                // folder being created); LastWrite and Size catch header rewrites made
                // by agents and external tools.
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                IncludeSubdirectories = false
            };

            watcher.Created += Watcher_Changed;
            watcher.Deleted += Watcher_Changed;
            watcher.Changed += Watcher_Changed;
            watcher.Renamed += Watcher_Changed;
            watcher.Error += Watcher_Error;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _watchedDirectory = target;
        }
        catch (ArgumentException)
        {
            // The folder disappeared between the existence check and the watch.
        }
        catch (IOException)
        {
            // No watch handle available (e.g. a network path). The panel still
            // refreshes on visibility changes and project loads.
        }
    }

    /// <summary>Disarms and releases the watcher, and cancels any pending rescan.</summary>
    internal void StopWatching()
    {
        _watcherRefreshTimer?.Stop();

        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= Watcher_Changed;
        _watcher.Deleted -= Watcher_Changed;
        _watcher.Changed -= Watcher_Changed;
        _watcher.Renamed -= Watcher_Changed;
        _watcher.Error -= Watcher_Error;
        _watcher.Dispose();
        _watcher = null;
        _watchedDirectory = null;
    }

    /// <summary>
    /// Returns the deepest existing folder on the path to the tickets directory, or
    /// null when no project is loaded.
    /// </summary>
    private static string? ResolveWatchTarget(string? ticketsDirectory)
    {
        string? candidate = ticketsDirectory;
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        return null;
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs e) => RequestWatcherRefresh();

    private void Watcher_Error(object sender, ErrorEventArgs e)
    {
        // The watch handle was lost (folder deleted or renamed, or the event buffer
        // overflowed). Re-arm from scratch so the list does not silently freeze.
        bool queued = TryBeginInvoke(() =>
        {
            StopWatching();
            if (IsVisible)
            {
                StartWatching();
                RefreshFromSystem();
            }
        });

        if (queued)
        {
            return;
        }

        // The UI thread is gone, and StopWatching touches a DispatcherTimer that has
        // thread affinity, so the dead watch is simply silenced here.
        try
        {
            if (sender is FileSystemWatcher watcher)
            {
                watcher.EnableRaisingEvents = false;
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Queues a debounced rescan. Watcher events arrive on a thread-pool thread, so the
    /// timer is started on the UI thread.
    /// </summary>
    private void RequestWatcherRefresh()
    {
        TryBeginInvoke(() =>
        {
            if (_watcherRefreshTimer is null)
            {
                _watcherRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = WatcherDebounce
                };
                _watcherRefreshTimer.Tick += WatcherRefreshTimer_Tick;
            }

            _watcherRefreshTimer.Stop();
            _watcherRefreshTimer.Start();
        });
    }

    private void WatcherRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _watcherRefreshTimer?.Stop();

        if (!IsVisible)
        {
            return;
        }

        // The tickets folder may have just been created, so the watch target is
        // re-evaluated before rescanning.
        StartWatching();
        RefreshFromSystem();
    }

    /// <summary>
    /// Marshals an action to the UI thread, returning false when the dispatcher is
    /// shutting down (watcher events can still arrive while the window closes).
    /// </summary>
    private bool TryBeginInvoke(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            Dispatcher.BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void TicketSystem_TicketsChanged(object? sender, TicketsChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyTickets(e.Tickets);
            return;
        }

        Dispatcher.BeginInvoke(() => ApplyTickets(e.Tickets));
    }

    private void TicketSystem_StateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateStatusText();
            return;
        }

        Dispatcher.BeginInvoke(UpdateStatusText);
    }

    private void TicketSystem_DispatchIssue(object? sender, TicketDispatchIssueEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyDispatchIssue(e);
            return;
        }

        Dispatcher.BeginInvoke(() => ApplyDispatchIssue(e));
    }

    private void ApplyDispatchIssue(TicketDispatchIssueEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Reason))
        {
            IssueText.Text = string.Empty;
            IssueText.Visibility = Visibility.Collapsed;
            return;
        }

        string prefix = string.IsNullOrWhiteSpace(e.TicketTitle)
            ? string.Empty
            : $"[{e.TicketTitle}] ";

        IssueText.Text = "⚠ " + prefix + e.Reason;
        IssueText.Visibility = Visibility.Visible;
    }

    private void RefreshFromSystem()
    {
        if (_ticketSystem is null)
        {
            UpdateStatusText();
            return;
        }

        IReadOnlyList<KaneCodeTicket> tickets = _ticketSystem.Rescan();
        ApplyTickets(tickets);
        UpdateStatusText();
    }

    private void ApplyTickets(IReadOnlyList<KaneCodeTicket> tickets)
    {
        _tickets.Clear();
        foreach (KaneCodeTicket ticket in tickets)
        {
            _tickets.Add(ticket);
        }

        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_ticketSystem is null)
        {
            StatusText.Text = "not connected";
            InitializeButton.Content = "Initialize";
            UpdateEmptyState();
            return;
        }

        int working = 0;
        int open = 0;
        int completed = 0;
        foreach (KaneCodeTicket ticket in _tickets)
        {
            switch (ticket.Status)
            {
                case TicketStatus.Working:
                case TicketStatus.Paused:
                    working++;
                    break;
                case TicketStatus.Open:
                    open++;
                    break;
                case TicketStatus.Complete:
                    completed++;
                    break;
            }
        }

        string state = _ticketSystem.IsRunning ? "running" : "stopped";
        StatusText.Text = $"{state} · {working} active · {open} open · {completed} complete";
        InitializeButton.Content = _ticketSystem.IsRunning ? "Stop" : "Initialize";
        UpdateEmptyState();
    }

    /// <summary>
    /// Explains an empty list. The distinction that matters is "no project loaded"
    /// (so there is nowhere to look for tickets) versus "project loaded, no ticket
    /// files yet" — neither of which has anything to do with the dispatch loop.
    /// </summary>
    private void UpdateEmptyState()
    {
        if (_tickets.Count > 0)
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStateText.Text = _ticketSystem?.TicketsDirectory is null
            ? "No project is loaded. Open a project, solution, or folder to see its tickets."
            : "No tickets yet. Use ＋ New Ticket, or drop a .txt file into .kanecode\\tickets.";
        EmptyStateText.Visibility = Visibility.Visible;
    }

    private void InitializeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ticketSystem is null)
        {
            return;
        }

        if (_ticketSystem.IsRunning)
        {
            _ticketSystem.Stop();
        }
        else
        {
            _ticketSystem.Start();
        }

        UpdateStatusText();
        RefreshFromSystem();
    }

    private void NewTicketButton_Click(object sender, RoutedEventArgs e)
    {
        NewTicketRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TicketList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TicketList.SelectedItem is KaneCodeTicket ticket && !string.IsNullOrWhiteSpace(ticket.ActiveAgentId))
        {
            TicketActivated?.Invoke(this, ticket);
        }
    }

    private void TicketList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TicketList.SelectedItem is not KaneCodeTicket ticket || _ticketSystem is null)
        {
            return;
        }

        ContextMenu menu = new();
        KaneCodeTicket captured = ticket;
        TicketSystem system = _ticketSystem;

        MenuItem openItem = new() { Header = "Open Ticket File" };
        openItem.Click += (_, _) => TicketOpenRequested?.Invoke(this, captured);
        menu.Items.Add(openItem);

        menu.Items.Add(new Separator());

        bool isActive = !string.IsNullOrWhiteSpace(ticket.ActiveAgentId);

        if (isActive)
        {
            if (ticket.Status == TicketStatus.Paused)
            {
                MenuItem resumeItem = new() { Header = "Resume Agent" };
                resumeItem.Click += (_, _) =>
                {
                    system.ResumeTicket(captured.Title);
                    RefreshFromSystem();
                };
                menu.Items.Add(resumeItem);
            }
            else if (ticket.Status == TicketStatus.Working)
            {
                MenuItem pauseItem = new() { Header = "Pause Agent" };
                pauseItem.Click += (_, _) =>
                {
                    system.PauseTicket(captured.Title);
                    RefreshFromSystem();
                };
                menu.Items.Add(pauseItem);
            }
        }

        if (ticket.Status is not TicketStatus.Complete and not TicketStatus.Unable and not TicketStatus.Failed)
        {
            MenuItem ignoreItem = new()
            {
                Header = ticket.Status == TicketStatus.Ignore ? "Stop Ignoring" : "Ignore"
            };
            ignoreItem.Click += (_, _) =>
            {
                system.SetIgnored(captured, ticket.Status != TicketStatus.Ignore);
                RefreshFromSystem();
            };
            menu.Items.Add(ignoreItem);
        }

        if (ticket.Status is TicketStatus.Complete or TicketStatus.Unable or TicketStatus.Failed or TicketStatus.Ignore)
        {
            MenuItem reopenItem = new() { Header = "Reopen" };
            reopenItem.Click += (_, _) =>
            {
                system.ReopenTicket(captured.Title);
                RefreshFromSystem();
            };
            menu.Items.Add(reopenItem);
        }

        if (ticket.Status is TicketStatus.Open or TicketStatus.Ignore or TicketStatus.Initialize
            or TicketStatus.Error or TicketStatus.Blocked)
        {
            MenuItem completeItem = new() { Header = "Mark Complete" };
            completeItem.Click += (_, _) =>
            {
                system.MarkTicketCompleteManually(captured.Title);
                RefreshFromSystem();
            };
            menu.Items.Add(completeItem);
        }

        menu.Items.Add(new Separator());

        // Deleting an active ticket would remove the worktree out from under a
        // running agent, so it is only offered for non-active tickets.
        if (!isActive)
        {
            MenuItem deleteItem = new() { Header = "Delete Ticket" };
            deleteItem.Click += (_, _) =>
            {
                system.DeleteTicket(captured);
                RefreshFromSystem();
            };
            menu.Items.Add(deleteItem);
        }

        menu.PlacementTarget = TicketList;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
