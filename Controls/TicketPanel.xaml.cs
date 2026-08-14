using KaneCode.Models;
using KaneCode.Services.Tickets;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// The Tickets panel — lists every KaneCode ticket found under
/// <c>.kanecode/tickets</c>, shows which ones have active agents, and provides an
/// Initialize button to start the autonomous ticket dispatch loop.
/// </summary>
public partial class TicketPanel : UserControl
{
    private readonly ObservableCollection<KaneCodeTicket> _tickets = [];
    private TicketSystem? _ticketSystem;

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
        UpdateStatusText();
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
    }

    /// <summary>
    /// Updates the provider/model/mode summary shown in the header.
    /// </summary>
    internal void SetHeaderInfo(string provider, string model, string mode)
    {
        HeaderInfoText.Text = $"{provider} · {model} · {mode}";
    }

    /// <summary>Refreshes the ticket list from the ticket system.</summary>
    internal void Refresh() => RefreshFromSystem();

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
            StatusText.Text = "not initialized";
            InitializeButton.Content = "Initialize";
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
