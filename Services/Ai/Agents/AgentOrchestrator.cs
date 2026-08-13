using KaneCode.Models;
using KaneCode.Services.Ai.Modes;
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
        AgentChanged?.Invoke(this, new AgentEventArgs(agent, AgentEventType.Created));
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
    /// Creates a top-level ticket agent. Ticket agents are independent of the main
    /// chat root and are dispatched by the ticket system to work on a ticket.
    /// </summary>
    public IAgent CreateTicketAgent(
        string id,
        string displayName,
        IAiProvider provider,
        string model,
        IAiChatMode mode,
        string? systemPrompt = null)
    {
        Agent agent = new(id, AgentRole.Ticket, displayName, provider, model, mode, systemPrompt)
        {
            ToolExecutionInterceptor = OnToolExecution
        };

        RegisterAgent(agent);
        AgentChanged?.Invoke(this, new AgentEventArgs(agent, AgentEventType.Created));
        return agent;
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

        bool isTicketStatusTool = toolName is "complete_ticket" or "unable_to_complete";
        if (!agent.Mode.IsToolAllowed(toolName) &&
            !(isTicketStatusTool && agent.Role == AgentRole.Ticket))
        {
            return ToolCallResult.Fail($"Tool '{toolName}' is not allowed in {agent.Mode.DisplayName} mode.");
        }

        // Acquire file locks for write tools. When another agent holds a lock on
        // the target file, wait for it to be released (up to the lock wait timeout)
        // instead of failing the tool call immediately, so concurrent agents queue
        // their edits rather than erroring out.
        FileLockResult? lockResult = await AcquireFileLockIfNeeded(
            toolName, args, agent.Id, cancellationToken).ConfigureAwait(false);
        if (lockResult is not null && !lockResult.Acquired)
        {
            return ToolCallResult.Conflict(
                $"File is locked by agent '{lockResult.ConflictingAgentId}' " +
                $"since {lockResult.ConflictingLockAcquiredAt:O}. " +
                $"Timed out after {FileLockManager.DefaultLockWaitTimeout.TotalSeconds:F0} seconds " +
                "waiting for the lock to be released. Retry once the other agent has finished editing the file.");
        }

        // Resolve the preset's backend options for this tool and execute within
        // that context so the tool reads its effective configuration.
        AiPreset? preset = (agent.Mode as PresetMode)?.Preset;
        IReadOnlyDictionary<string, JsonElement> options = AgentToolContext.Resolve(tool, preset);

        try
        {
            using (AgentToolContext.Push(options))
            {
                ToolCallResult result = await tool.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
                return result;
            }
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

    /// <summary>
    /// Public entry point for tool execution used by the main AI chat panel.
    /// Delegates to <see cref="OnToolExecution"/> for file-locking and
    /// spawn_agent interception. The <paramref name="agentId"/> is used to
    /// look up the agent whose mode/permissions apply.
    /// </summary>
    public async Task<ToolCallResult> ExecuteToolAsync(
        string agentId,
        string toolName,
        JsonElement args,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        IAgent? agent = GetAgent(agentId);
        if (agent is not Agent agentImpl)
        {
            return ToolCallResult.Fail($"Agent '{agentId}' not found or is not an Agent instance.");
        }

        return await OnToolExecution(agentImpl, toolName, args, cancellationToken)
            .ConfigureAwait(false);
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
        string? providerRef = null;
        string? model = null;
        string? presetName = null;
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
                providerRef = providerElement.GetString();
            }

            if (args.TryGetProperty("model", out JsonElement modelElement) &&
                modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString();
            }

            if (args.TryGetProperty("preset", out JsonElement presetElement) &&
                presetElement.ValueKind == JsonValueKind.String)
            {
                presetName = presetElement.GetString();
            }

            // Legacy fallback: the 'mode' parameter (built-in mode IDs and
            // "preset:&lt;guid&gt;" references) is still honored so older tool
            // calls keep working after the schema switched to 'preset'.
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

        // Resolve provider — accepts either the provider ID or the user-created
        // label from the AI settings (e.g. "v1chatcompletions" or "My OpenAI Key").
        IAiProvider? provider = parentAgent.Provider;
        bool providerExplicitlyResolved = false;
        if (!string.IsNullOrWhiteSpace(providerRef))
        {
            IAiProvider? found = FindProvider(providerRef);
            if (found is not null)
            {
                provider = found;
                providerExplicitlyResolved = true;
            }
        }

        // Resolve the parent preset's spawn_agent backend options so a per-agent
        // allow-list of spawnable subagent presets is enforced at execution time.
        // Null means unrestricted (all subagent presets may be spawned).
        AiPreset? parentPreset = (parentAgent.Mode as PresetMode)?.Preset;
        HashSet<string>? allowedPresets = SpawnAgentTool.GetAllowedSubagentPresets(parentPreset);

        // Resolve mode. The 'preset' parameter accepts the name of a preset marked
        // as a subagent (see the spawn_agent tool description, e.g. "Code Reviewer").
        // Falls back to the parent's mode when omitted. The legacy 'mode' parameter
        // (built-in mode IDs and "preset:&lt;guid&gt;" refs copied from the chat
        // panel's preset dropdown) is still honored for backward compatibility.
        IAiChatMode? mode = parentAgent.Mode;
        if (!string.IsNullOrWhiteSpace(presetName))
        {
            if (allowedPresets is not null && !allowedPresets.Contains(presetName))
            {
                string allowedText = allowedPresets.Count == 0
                    ? "(none — spawning sub-agents is disabled for this agent)"
                    : string.Join(", ", allowedPresets);
                return ToolCallResult.Fail(
                    $"Subagent preset '{presetName}' is not allowed for this agent. " +
                    $"Allowed subagent presets: {allowedText}.");
            }

            IReadOnlyList<AiPreset> subagentPresets = AiPresetManager.LoadSubagentPresets();
            AiPreset? preset = subagentPresets.FirstOrDefault(p =>
                string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));

            if (preset is not null)
            {
                mode = new PresetMode(preset, _toolRegistry);
            }
            else if (subagentPresets.Count == 0)
            {
                return ToolCallResult.Fail(
                    $"Unknown subagent preset '{presetName}': no subagent presets are configured. " +
                    "Open the Preset Editor and check \"Set as subagent\" on a preset to make it available.");
            }
            else
            {
                return ToolCallResult.Fail(
                    $"Unknown subagent preset '{presetName}'. Available subagent presets: " +
                    string.Join(", ", subagentPresets.Select(p => p.Name)) + ".");
            }
        }
        else if (!string.IsNullOrWhiteSpace(modeId))
        {
            IAiChatMode? found = _modeRegistry.Get(modeId);
            if (found is not null)
            {
                mode = found;
            }
            else if (modeId.StartsWith("preset:", StringComparison.Ordinal))
            {
                string presetId = modeId["preset:".Length..];
                AiPreset? preset = AiPresetManager.Load()
                    .FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.Ordinal));
                if (preset is not null)
                {
                    mode = new PresetMode(preset, _toolRegistry);
                }
            }
        }

        // Resolve model. Also accepts a combined "providerRef/model" reference where
        // providerRef is a registered provider ID or its user-created label
        // (e.g. "v1chatcompletions/gpt-4o" or "My OpenAI Key/gpt-4o") copied from the
        // chat panel's model picker, so a single string pins both the provider and
        // the model. The provider part only switches the provider when no explicit
        // provider parameter was supplied.
        string effectiveModel = model ?? parentAgent.Model;
        if (!string.IsNullOrWhiteSpace(model) &&
            TrySplitProviderPrefixedModel(
                model,
                BuildProviderRefs(),
                out string prefixedProviderRef,
                out string prefixedModel))
        {
            if (!providerExplicitlyResolved)
            {
                IAiProvider? prefixedProvider = FindProvider(prefixedProviderRef);
                if (prefixedProvider is not null)
                {
                    provider = prefixedProvider;
                }
            }

            effectiveModel = prefixedModel;
        }

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
                ? _toolRegistry.SerializeToolDefinitions(subAgent.Mode.AllowedTools, (subAgent.Mode as PresetMode)?.Preset)
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

            // Release the sub-agent's file locks so other agents can edit the same files.
            // The agent remains in the tree so the user can inspect its conversation
            // via the agent session dropdown.
            FileLockManager.ReleaseAll(subAgent.Id);

            return result.Success
                ? ToolCallResult.Ok($"Sub-agent completed: {result.Summary}")
                : ToolCallResult.Fail($"Sub-agent failed: {result.Summary}");
        }
        catch (OperationCanceledException)
        {
            FileLockManager.ReleaseAll(subAgent.Id);
            return ToolCallResult.Fail("Sub-agent execution was cancelled.");
        }
        catch (Exception ex)
        {
            FileLockManager.ReleaseAll(subAgent.Id);
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

    /// <summary>
    /// Waits to acquire a file lock for write-type tools, delaying until the
    /// current holder releases it (or the lock wait timeout elapses).
    /// Returns null if the tool doesn't need locking (read-only tools).
    /// </summary>
    private async Task<FileLockResult?> AcquireFileLockIfNeeded(
        string toolName,
        JsonElement args,
        string agentId,
        CancellationToken cancellationToken)
    {
        string? filePath = ExtractFilePath(toolName, args);
        if (filePath is null)
        {
            return null;
        }

        return await FileLockManager.WaitForWriteLockAsync(
            filePath,
            agentId,
            FileLockManager.DefaultLockWaitTimeout,
            cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Finds a registered provider whose ID or user-created label matches
    /// <paramref name="providerRef"/> (case-insensitive), or null.
    /// </summary>
    private IAiProvider? FindProvider(string providerRef)
    {
        return _providerRegistry.Providers
            .FirstOrDefault(p =>
                string.Equals(p.ProviderId, providerRef, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.DisplayName, providerRef, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the identifiers (provider IDs and user-created labels) that can be
    /// used to reference each registered provider.
    /// </summary>
    private List<string> BuildProviderRefs()
    {
        List<string> refs = new(_providerRegistry.Providers.Count * 2);
        foreach (IAiProvider provider in _providerRegistry.Providers)
        {
            refs.Add(provider.ProviderId);
            refs.Add(provider.DisplayName);
        }

        return refs;
    }

    /// <summary>
    /// Attempts to split a provider-prefixed model reference of the form
    /// "&lt;providerRef&gt;/&lt;model&gt;" (e.g. "v1chatcompletions/gpt-4o" or
    /// "My OpenAI Key/gpt-4o") into its parts. <paramref name="knownProviderRefs"/>
    /// is the set of registered provider IDs and user-created labels. Returns true
    /// only when the model starts with one of those references followed by a
    /// separator and a non-empty model suffix. The longest matching reference wins
    /// so labels containing spaces or slashes resolve correctly. Any other string
    /// is left untouched so ordinary model IDs (including ones containing slashes)
    /// pass through unchanged.
    /// </summary>
    internal static bool TrySplitProviderPrefixedModel(
        string model,
        IReadOnlyList<string> knownProviderRefs,
        out string providerRef,
        out string modelName)
    {
        providerRef = string.Empty;
        modelName = string.Empty;

        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        string? bestRef = null;
        foreach (string known in knownProviderRefs)
        {
            if (string.IsNullOrWhiteSpace(known))
            {
                continue;
            }

            string prefix = known + "/";
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (bestRef is null || known.Length > bestRef.Length))
            {
                bestRef = known;
            }
        }

        if (bestRef is null)
        {
            return false;
        }

        providerRef = bestRef;
        modelName = model[(bestRef.Length + 1)..].Trim();
        return modelName.Length > 0;
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
