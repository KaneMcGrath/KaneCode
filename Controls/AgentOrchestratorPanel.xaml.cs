using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Agents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>Displays the live agent hierarchy owned by an <see cref="AgentOrchestrator"/>.</summary>
public partial class AgentOrchestratorPanel : UserControl
{
    private AgentOrchestrator? _orchestrator;
    private bool _isRefreshing;

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

    internal sealed class AgentTreeItem
    {
        public IAgent Agent { get; }
        public List<AgentTreeItem> Children { get; } = [];
        public bool IsSelected { get; set; }
        public bool IsExpanded { get; set; } = true;
        public string Glyph => Agent.Role == AgentRole.Root ? "👑" : "🔧";
        public string DisplayName => Agent.DisplayName;
        public string Details => $"{Agent.Provider.DisplayName} · {Agent.Model} · {Agent.Messages.Count} msgs";

        public AgentTreeItem(IAgent agent)
        {
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        }
    }
}
