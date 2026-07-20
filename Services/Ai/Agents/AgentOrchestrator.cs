using System.Collections.Concurrent;
using System.Text.Json;

namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// Central orchestrator for the multi-agent system.
///
/// Responsibilities:
/// - Creates and manages the agent tree (root + sub-agents)
/// - Routes messages between parent and child agents
/// - Provides shared resources (file lock manager, tool registry)
/// - Handles the "spawn_agent" tool call to create sub-agents
/// - Tracks agent lifecycle and cleans up completed sub-agents
///
/// The root agent is the main AI chat panel's agent. Sub-agents are spawned
/// by the root (or other sub-agents) to handle delegated tasks with their
/// own provider, model, mode, and context window.
/// </summary>
internal sealed class AgentOrchestrator : IDisposable
{
    private readonly ConcurrentDictionary<string, IAgent> _agents = new(StringComparer.Ordinal);
    private readonly AgentToolRegistry _toolRegistry;
    private readonly AiProviderRegistry _providerRegistry;
    private readonly AiChatModeRegistry _modeRegistry;
    private readonly object _spawnLock = new();

    /// <summary>
    /// Shared file lock manager used by all agents.
    /// </summary>
    public FileLockManager FileLockManager { get; } = new();

    /// <summary>
    /// The root agent (main AI chat).
    /// </summary>
    public IAgent? RootAgent { get; private set; }

    /// <summary>
    /// Raised when an agent is created or destroyed.
    /// </summary>
    public event EventHandler<AgentEventArgs>? AgentChanged;

    /// <summary>
    /// Raised when a sub-agent completes its task and reports back.
    /// </summary>
    public event EventHandler<SubAgentCompletedEventArgs>? SubAgentCompleted;

    public AgentOrchestrator(
        AgentToolRegistry toolRegistry,
        AiProviderRegistry providerRegistry,
        AiChatModeRegistry modeRegistry)
    {
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _modeRegistry = modeRegistry ?? throw new ArgumentNullException(nameof(modeRegistry));
    }

    // ── Agent lifecycle ─────────────────────────────────────────────

    /// <summary>
    /// Creates and registers the root agent.
    /// Only one root agent can exist at a time.
    /// </summary>
    public IAgent CreateRootAgent(
        string id,
        string displayName,
        IAiProvider provider,
        string model,
        IAiChatMode mode,
        string? systemPrompt = null)
    {
        if (RootAgent is not null)
        {
            throw new InvalidOperationException("A root agent already exists. Remove it first.");
        }

        Agent agent = new(id, AgentRole.Root, displayName, provider, model, mode, systemPrompt)
        {
            ToolExecutionInterceptor = OnToolExecution
        };

        RegisterAgent(agent);
        RootAgent = agent;
        return agent;
    }

    /// <summary>
    /// Spawns a sub-agent with the given configuration.
    /// The sub-agent inherits some settings from its parent but can use
    /// a different provider, model, or mode.
    /// </summary>
    public IAgent SpawnSubAgent(
        string parentAgentId,
        string displayName,
        IAiProvider? provider = null,
        string? model = null,
        IAiChatMode? mode = null,
        string? systemPrompt = null)
    {
        IAgent? parent = GetAgent(parentAgentId);
        if (parent is null)
        {
            throw new ArgumentException($"Parent agent '{parentAgentId}' not found.", nameof(parentAgentId));
        }

        string childId = $"{parentAgentId}_child_{Guid.NewGuid():N}";

        IAiProvider effectiveProvider = provider ?? parent.Provider;
        string effectiveModel = model ?? parent.Model;
        IAiChatMode effectiveMode = mode ?? parent.Mode;

        Agent childAgent = new(
            childId,
            AgentRole.SubAgent,
            displayName,
            effectiveProvider,
            effectiveModel,
            effectiveMode,
            systemPrompt,
            parentAgentId)
        {
            ToolExecutionInterceptor = OnToolExecution
        };

        RegisterAgent(childAgent);
        parent.RegisterChild(childId);

        AgentChanged?.Invoke(this, new AgentEventArgs(childAgent, AgentEventType.Created));

        return childAgent;
    }

    /// <summary>
    /// Removes an agent and all its descendants from the orchestrator.
    /// Releases all file locks held by the removed agents.
    /// </summary>
    public void RemoveAgent(string agentId)
    {
        IAgent? agent = GetAgent(agentId);
        if (agent is null)
        {
            return;
        }

        // Recursively remove children first
        foreach (string childId in agent.ChildIds.ToList())
        {
            RemoveAgent(childId);
        }

        // Unregister from parent
        if (agent.ParentId is not null)
        {
            IAgent? parent = GetAgent(agent.ParentId);
            parent?.UnregisterChild(agentId);
        }

        // Release file locks
        FileLockManager.ReleaseAll(agentId);

        // Remove from registry
        _agents.TryRemove(agentId, out _);

        if (ReferenceEquals(RootAgent, agent))
        {
            RootAgent = null;
        }

        AgentChanged?.Invoke(this, new AgentEventArgs(agent, AgentEventType.Removed));
    }

    // ── Tool execution interceptor ──────────────────────────────────

    /// <summary>
    /// Intercepts tool execution to handle spawn_agent specially and
    /// to integrate file-locking for all write tools.
    /// </summary>
    private async Task<ToolCallResult> OnToolExecution(
        Agent agent,
        string toolName,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        // Handle spawn_agent specially — it creates a new sub-agent
        if (string.Equals(toolName, "spawn_agent", StringComparison.Ordinal))
        {
            return await ExecuteSpawnAgentAsync(agent, args, cancellationToken)
                .ConfigureAwait(false);
        }

        // For all other tools, look up and execute with file-lock awareness
        IAgentTool? tool = _toolRegistry.Get(toolName);
        if (tool is null)
        {
            return ToolCallResult.Fail($"Unknown tool: {toolName}");
        }

        if (!agent.Mode.IsToolAllowed(toolName))
        {
            return ToolCallResult.Fail($"Tool '{toolName}' is not allowed in {agent.Mode.DisplayName} mode.");
        }

        // Acquire file locks for write tools
        FileLockResult? lockResult = AcquireFileLockIfNeeded(toolName, args);
        if (lockResult is not null && !lockResult.Acquired)
        {
            return ToolCallResult.Conflict(
                $"File is locked by agent '{lockResult.ConflictingAgentId}' " +
                $"since {lockResult.ConflictingLockAcquiredAt:O}. " +
                "Wait for the lock to be released and retry.");
        }

        try
        {
            ToolCallResult result = await tool.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            return ToolCallResult.Fail("Tool execution was cancelled.");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail($"Tool execution error: {ex.Message}");
        }
    }

    // ── spawn_agent tool ────────────────────────────────────────────

    /// <summary>
    /// Executes the spawn_agent tool, which creates a new sub-agent,
    /// runs it to completion, and returns its result.
    /// </summary>
    private async Task<ToolCallResult> ExecuteSpawnAgentAsync(
        Agent parentAgent,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        // Parse arguments
        string? task = null;
        string? agentDisplayName = null;
        string? providerId = null;
        string? model = null;
        string? modeId = null;
        string? systemPrompt = null;
        int maxIterations = 50;

        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("task", out JsonElement taskElement) &&
                taskElement.ValueKind == JsonValueKind.String)
            {
                task = taskElement.GetString();
            }

            if (args.TryGetProperty("displayName", out JsonElement nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                agentDisplayName = nameElement.GetString();
            }

            if (args.TryGetProperty("maxIterations", out JsonElement iterationsElement) &&
                iterationsElement.ValueKind == JsonValueKind.Number)
            {
                maxIterations = iterationsElement.GetInt32();
                maxIterations = Math.Clamp(maxIterations, 1, 200);
            }

            if (args.TryGetProperty("provider", out JsonElement providerElement) &&
                providerElement.ValueKind == JsonValueKind.String)
            {
                providerId = providerElement.GetString();
            }

            if (args.TryGetProperty("model", out JsonElement modelElement) &&
                modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString();
            }

            if (args.TryGetProperty("mode", out JsonElement modeElement) &&
                modeElement.ValueKind == JsonValueKind.String)
            {
                modeId = modeElement.GetString();
            }

            if (args.TryGetProperty("systemPrompt", out JsonElement promptElement) &&
                promptElement.ValueKind == JsonValueKind.String)
            {
                systemPrompt = promptElement.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return ToolCallResult.Fail("The 'task' parameter is required for spawn_agent.");
        }

        // Resolve provider
        IAiProvider? provider = parentAgent.Provider;
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            IAiProvider? found = _providerRegistry.Providers
                .FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                provider = found;
            }
        }

        // Resolve mode
        IAiChatMode? mode = parentAgent.Mode;
        if (!string.IsNullOrWhiteSpace(modeId))
        {
            IAiChatMode? found = _modeRegistry.Get(modeId);
            if (found is not null)
            {
                mode = found;
            }
        }

        // Resolve model
        string effectiveModel = model ?? parentAgent.Model;

        // Create display name
        string displayName = !string.IsNullOrWhiteSpace(agentDisplayName)
            ? agentDisplayName
            : $"Sub-agent for: {Truncate(task, 60)}";

        // Spawn and run the sub-agent (with lock to prevent concurrent spawns)
        IAgent subAgent;
        lock (_spawnLock)
        {
            subAgent = SpawnSubAgent(
                parentAgent.Id,
                displayName,
                provider,
                effectiveModel,
                mode,
                systemPrompt);
        }

        try
        {
            JsonElement toolsDef = subAgent.Mode.ToolsEnabled
                ? _toolRegistry.SerializeToolDefinitions(subAgent.Mode.AllowedTools)
                : default;

            AgentRunResult result = await subAgent.RunAsync(
                task,
                toolsDef,
                _toolRegistry,
                FileLockManager,
                this,
                maxIterations,
                cancellationToken).ConfigureAwait(false);

            // Notify parent
            parentAgent.ReceiveChildResult(subAgent.Id, result);

            SubAgentCompleted?.Invoke(this, new SubAgentCompletedEventArgs(
                subAgent.Id,
                parentAgent.Id,
                result));

            // Clean up the sub-agent (release locks, remove from tree)
            RemoveAgent(subAgent.Id);

            return result.Success
                ? ToolCallResult.Ok($"Sub-agent completed: {result.Summary}")
                : ToolCallResult.Fail($"Sub-agent failed: {result.Summary}");
        }
        catch (OperationCanceledException)
        {
            RemoveAgent(subAgent.Id);
            return ToolCallResult.Fail("Sub-agent execution was cancelled.");
        }
        catch (Exception ex)
        {
            RemoveAgent(subAgent.Id);
            return ToolCallResult.Fail($"Sub-agent execution error: {ex.Message}");
        }
    }

    // ── Agent lookup ────────────────────────────────────────────────

    /// <summary>
    /// Gets an agent by ID, or null if not found.
    /// </summary>
    public IAgent? GetAgent(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        _agents.TryGetValue(agentId, out IAgent? agent);
        return agent;
    }

    /// <summary>
    /// Returns all currently registered agents (including root and all sub-agents).
    /// </summary>
    public IReadOnlyCollection<IAgent> GetAllAgents()
    {
        return _agents.Values.ToList();
    }

    /// <summary>
    /// Returns all agents at a given depth in the tree (root = depth 0).
    /// </summary>
    public IReadOnlyList<IAgent> GetAgentsAtDepth(int depth)
    {
        List<IAgent> result = [];

        foreach (IAgent agent in _agents.Values)
        {
            if (GetAgentDepth(agent) == depth)
            {
                result.Add(agent);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the depth of an agent in the tree.
    /// Root = 0, children of root = 1, etc.
    /// </summary>
    public int GetAgentDepth(IAgent agent)
    {
        int depth = 0;
        string? currentParentId = agent.ParentId;

        while (currentParentId is not null)
        {
            depth++;
            IAgent? parent = GetAgent(currentParentId);
            if (parent is null)
            {
                break;
            }

            currentParentId = parent.ParentId;
        }

        return depth;
    }

    /// <summary>
    /// Returns the full ancestor chain of an agent, from root down to the agent.
    /// </summary>
    public IReadOnlyList<IAgent> GetAncestorChain(IAgent agent)
    {
        List<IAgent> chain = [agent];
        string? currentParentId = agent.ParentId;

        while (currentParentId is not null)
        {
            IAgent? parent = GetAgent(currentParentId);
            if (parent is null)
            {
                break;
            }

            chain.Add(parent);
            currentParentId = parent.ParentId;
        }

        chain.Reverse();
        return chain;
    }

    // ── Disposal ────────────────────────────────────────────────────

    public void Dispose()
    {
        // Remove all agents bottom-up (children first)
        foreach (string agentId in _agents.Keys.ToList())
        {
            RemoveAgent(agentId);
        }
    }

    // ── Private helpers ─────────────────────────────────────────────

    private void RegisterAgent(IAgent agent)
    {
        if (!_agents.TryAdd(agent.Id, agent))
        {
            throw new InvalidOperationException($"An agent with ID '{agent.Id}' already exists.");
        }
    }

    private FileLockResult? AcquireFileLockIfNeeded(string toolName, JsonElement args)
    {
        string? filePath = ExtractFilePath(toolName, args);
        if (filePath is null)
        {
            return null;
        }

        return FileLockManager.TryAcquireWriteLockWithResult(filePath, "orchestrator");
    }

    private static string? ExtractFilePath(string toolName, JsonElement args)
    {
        string[] writeTools = ["write", "edit", "delete", "rename_path"];
        bool isWriteTool = false;
        foreach (string writeTool in writeTools)
        {
            if (string.Equals(toolName, writeTool, StringComparison.Ordinal))
            {
                isWriteTool = true;
                break;
            }
        }

        if (!isWriteTool || args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string[] pathPropertyNames = ["filePath", "destinationPath", "sourcePath"];
        foreach (string propName in pathPropertyNames)
        {
            if (args.TryGetProperty(propName, out JsonElement element) &&
                element.ValueKind == JsonValueKind.String)
            {
                string? path = element.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "…";
    }
}

// ── Event argument types ──────────────────────────────────────────

/// <summary>
/// Event arguments for agent lifecycle events.
/// </summary>
internal sealed class AgentEventArgs : EventArgs
{
    public IAgent Agent { get; }

    public AgentEventType EventType { get; }

    public AgentEventArgs(IAgent agent, AgentEventType eventType)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        EventType = eventType;
    }
}

/// <summary>
/// The type of agent lifecycle event.
/// </summary>
internal enum AgentEventType
{
    Created,
    Removed,
    Started,
    Completed
}

/// <summary>
/// Event arguments for sub-agent completion.
/// </summary>
internal sealed class SubAgentCompletedEventArgs : EventArgs
{
    public string AgentId { get; }

    public string ParentAgentId { get; }

    public AgentRunResult Result { get; }

    public SubAgentCompletedEventArgs(string agentId, string parentAgentId, AgentRunResult result)
    {
        AgentId = agentId;
        ParentAgentId = parentAgentId;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }
}
