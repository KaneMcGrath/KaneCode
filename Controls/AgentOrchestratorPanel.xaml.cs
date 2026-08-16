using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Agents;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>Displays the live agent hierarchy owned by an <see cref="AgentOrchestrator"/>.</summary>
public partial class AgentOrchestratorPanel : UserControl
{
    private AgentOrchestrator? _orchestrator;
    private bool _isRefreshing;

    /// <summary>
    /// The tree items currently bound, keyed by agent ID, together with the agents
    /// whose <see cref="IAgent.Activity"/> event they are subscribed to. Kept so the
    /// per-agent detail line ("… · N msgs") updates while an agent works, and so the
    /// subscriptions are released when the tree is rebuilt.
    /// </summary>
    private readonly Dictionary<string, AgentTreeItem> _itemsByAgentId = new(StringComparer.Ordinal);
    private readonly List<IAgent> _subscribedAgents = [];

    /// <summary>Raised when the user selects an agent in the tree.</summary>
    internal event Action<IAgent>? AgentSelected;

    public AgentOrchestratorPanel()
    {
        InitializeComponent();
    }

    internal void SetOrchestrator(AgentOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        if (_orchestrator is not null)
        {
            _orchestrator.AgentChanged -= Orchestrator_AgentChanged;
            _orchestrator.SubAgentCompleted -= Orchestrator_SubAgentCompleted;
        }

        _orchestrator = orchestrator;
        _orchestrator.AgentChanged += Orchestrator_AgentChanged;
        _orchestrator.SubAgentCompleted += Orchestrator_SubAgentCompleted;
        RefreshAgents();
    }

    private void Orchestrator_AgentChanged(object? sender, AgentEventArgs e) => QueueRefresh();
    private void Orchestrator_SubAgentCompleted(object? sender, SubAgentCompletedEventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshAgents();
            return;
        }

        Dispatcher.BeginInvoke(RefreshAgents);
    }

    private void RefreshAgents()
    {
        if (_orchestrator is null)
        {
            return;
        }

        string? selectedId = (AgentTree.SelectedItem as AgentTreeItem)?.Agent.Id;
        IReadOnlyCollection<IAgent> agents = _orchestrator.GetAllAgents();

        // Drop the previous tree's activity subscriptions before building the new one;
        // agents that are still present are re-subscribed as their items are created.
        UnsubscribeFromAgents();

        List<AgentTreeItem> roots = agents
            .Where(agent => agent.ParentId is null)
            .OrderBy(agent => agent.Role)
            .Select(agent => BuildItem(agent, agents))
            .ToList();

        _isRefreshing = true;
        try
        {
            AgentTree.ItemsSource = roots;
            SummaryText.Text = agents.Count == 0
                ? "No active agents"
                : $"{agents.Count} agent{(agents.Count == 1 ? "" : "s")} · select one to view its conversation";

            AgentTree.UpdateLayout();
            AgentTreeItem? selected = FindItem(roots, selectedId);
            if (selected is not null)
            {
                selected.IsSelected = true;
            }
            else if (roots.Count > 0)
            {
                roots[0].IsSelected = true;
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private AgentTreeItem BuildItem(IAgent agent, IReadOnlyCollection<IAgent> allAgents)
    {
        AgentTreeItem item = new(agent);
        _itemsByAgentId[agent.Id] = item;
        agent.Activity += Agent_Activity;
        _subscribedAgents.Add(agent);

        foreach (string childId in agent.ChildIds)
        {
            IAgent? child = allAgents.FirstOrDefault(candidate => string.Equals(candidate.Id, childId, StringComparison.Ordinal));
            if (child is not null)
            {
                item.Children.Add(BuildItem(child, allAgents));
            }
        }

        return item;
    }

    private void UnsubscribeFromAgents()
    {
        foreach (IAgent agent in _subscribedAgents)
        {
            agent.Activity -= Agent_Activity;
        }

        _subscribedAgents.Clear();
        _itemsByAgentId.Clear();
    }

    /// <summary>
    /// Refreshes a single agent's detail line when it reports progress. Raised on the
    /// agent's run thread, so the update is marshalled to the UI thread.
    /// </summary>
    private void Agent_Activity(object? sender, AgentActivityEventArgs e)
    {
        if (sender is not IAgent agent)
        {
            return;
        }

        string agentId = agent.Id;
        Dispatcher.BeginInvoke(() =>
        {
            if (_itemsByAgentId.TryGetValue(agentId, out AgentTreeItem? item))
            {
                item.NotifyDetailsChanged();
            }
        });
    }

    private static AgentTreeItem? FindItem(IEnumerable<AgentTreeItem> items, string? id)
    {
        if (id is null)
        {
            return null;
        }

        foreach (AgentTreeItem item in items)
        {
            if (string.Equals(item.Agent.Id, id, StringComparison.Ordinal))
            {
                return item;
            }

            AgentTreeItem? child = FindItem(item.Children, id);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private void AgentTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isRefreshing || e.NewValue is not AgentTreeItem item)
        {
            return;
        }

        AgentSelected?.Invoke(item.Agent);
    }

    /// <summary>
    /// A node in the agent tree.
    ///
    /// Implements <see cref="INotifyPropertyChanged"/> because the TreeViewItem style
    /// binds <c>IsSelected</c>/<c>IsExpanded</c> two-way: without change notification,
    /// the selection restored by <see cref="RefreshAgents"/> after a rebuild never
    /// reaches the already-realized containers and the tree loses its selection every
    /// time an agent is created or removed.
    /// </summary>
    internal sealed class AgentTreeItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isExpanded = true;

        public IAgent Agent { get; }
        public List<AgentTreeItem> Children { get; } = [];

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        public string Glyph => Agent.Role switch
        {
            AgentRole.Root => "👑",
            AgentRole.Ticket => "🎫",
            _ => "🔧"
        };
        public string DisplayName => Agent.DisplayName;
        public string Details => $"{Agent.Provider.DisplayName} · {Agent.Model} · {Agent.MessageCount} msgs";

        public AgentTreeItem(IAgent agent)
        {
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        }

        /// <summary>Re-reads <see cref="Details"/> from the agent (message count changed).</summary>
        public void NotifyDetailsChanged() => OnPropertyChanged(nameof(Details));

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
