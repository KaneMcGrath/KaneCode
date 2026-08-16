using System.IO;
using System.Text.Json;
using KaneCode.Models;
using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Agents;
using KaneCode.Services.Ai.Modes;

namespace KaneCode.Services.Tickets;

/// <summary>
/// The ticket dispatch engine.
///
/// Responsibilities:
/// - Scan <c>.kanecode/tickets</c> for ticket files.
/// - Pick the next eligible ticket (oldest by creation time, then alphabetically,
///   with higher <see cref="KaneCodeTicket.Priority"/> values dispatched first).
/// - Create a Git worktree per ticket and dispatch an autonomous agent to work on it.
/// - Track agent lifecycle and update ticket status as work progresses.
///
/// The ticket system reuses the active provider, model, and agent mode unless a
/// ticket opts into per-ticket overrides (which require <see cref="TicketSettings.AllowTicketOverrides"/>).
/// </summary>
internal sealed class TicketSystem : ITicketStatusService, IDisposable
{
    private const int MaxIterations = 150;

    /// <summary>
    /// How many finished ticket agents stay registered with the orchestrator. Their
    /// runs are over, but keeping them in the agent tree lets the user open and read
    /// the session afterwards — removing an agent the moment it finishes pulls the
    /// conversation out of the chat panel while it is being watched. Older agents are
    /// released so a long session does not accumulate them without bound.
    /// </summary>
    private const int RetainedFinishedAgents = 10;

    private readonly TicketFileStore _store;
    private readonly TicketWorktreeManager _worktreeManager = new();
    private readonly AgentToolRegistry _toolRegistry;
    private readonly AiProviderRegistry _providerRegistry;
    private readonly AiChatModeRegistry _modeRegistry;
    private readonly AgentOrchestrator _orchestrator;
    private readonly Func<string?> _projectRootProvider;
    private readonly Func<string?> _repositoryRootProvider;
    private readonly Func<IAiProvider?> _currentProviderProvider;
    private readonly Func<string?> _currentModelProvider;
    private readonly Func<IAiChatMode?> _currentModeProvider;

    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, TicketRun> _activeRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _finishedAgentIds = new();
    private System.Threading.Timer? _timer;

    /// <summary>
    /// Raised after the ticket list changes (new files, status changes, active agents).
    /// May be raised on a background thread; subscribers marshal to the UI thread.
    /// </summary>
    internal event EventHandler<TicketsChangedEventArgs>? TicketsChanged;

    /// <summary>
    /// Raised when the ticket system starts or stops. May be raised on a background thread.
    /// </summary>
    internal event EventHandler? StateChanged;

    /// <summary>
    /// Raised when the dispatcher declines a ticket or hits an error while trying to
    /// start one. The payload carries a human-readable reason (and the ticket title when
    /// the issue is ticket-specific). Raised with a null reason when the issue is cleared
    /// (e.g. on a fresh <see cref="Start"/>). May be raised on a background thread;
    /// subscribers marshal to the UI thread.
    /// </summary>
    internal event EventHandler<TicketDispatchIssueEventArgs>? DispatchIssue;

    public TicketSystem(
        TicketFileStore store,
        AgentToolRegistry toolRegistry,
        AiProviderRegistry providerRegistry,
        AiChatModeRegistry modeRegistry,
        AgentOrchestrator orchestrator,
        Func<string?> projectRootProvider,
        Func<string?> repositoryRootProvider,
        Func<IAiProvider?> currentProviderProvider,
        Func<string?> currentModelProvider,
        Func<IAiChatMode?> currentModeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _modeRegistry = modeRegistry ?? throw new ArgumentNullException(nameof(modeRegistry));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _projectRootProvider = projectRootProvider ?? throw new ArgumentNullException(nameof(projectRootProvider));
        _repositoryRootProvider = repositoryRootProvider ?? throw new ArgumentNullException(nameof(repositoryRootProvider));
        _currentProviderProvider = currentProviderProvider ?? throw new ArgumentNullException(nameof(currentProviderProvider));
        _currentModelProvider = currentModelProvider ?? throw new ArgumentNullException(nameof(currentModelProvider));
        _currentModeProvider = currentModeProvider ?? throw new ArgumentNullException(nameof(currentModeProvider));
    }

    /// <summary>Whether the ticket system is currently running its dispatch loop.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Settings currently in effect.</summary>
    public TicketSettings Settings => TicketSettingsManager.Load();

    /// <summary>Number of active (working or paused) ticket runs.</summary>
    public int ActiveRunCount
    {
        get
        {
            lock (_activeRuns)
            {
                return _activeRuns.Count;
            }
        }
    }

    /// <summary>
    /// Starts the ticket system: ensures the tickets folder exists, promotes
    /// headerless tickets, and begins the background dispatch loop.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _store.EnsureTicketsDirectory();
        _store.InitializeHeaderlessTickets();

        // A fresh start clears any issue left over from a previous session so the
        // panel does not show a stale reason after the user fixed the underlying problem.
        SetDispatchIssue(null, null);

        _timer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    _ = TryDispatchNextAsync();
                }
                catch (Exception)
                {
                    // Never let a dispatch failure take down the timer loop.
                }
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = TryDispatchNextAsync();
    }

    /// <summary>
    /// Stops the dispatch loop. Agents already running are left to finish their
    /// current work unless <see cref="StopAll"/> is used.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops the dispatch loop and cancels every active ticket run.
    /// </summary>
    public void StopAll()
    {
        Stop();

        List<TicketRun> runs;
        lock (_activeRuns)
        {
            runs = _activeRuns.Values.ToList();
        }

        foreach (TicketRun run in runs)
        {
            run.CancellationTokenSource?.Cancel();
        }
    }

    /// <summary>
    /// Rescans the tickets folder and refreshes statuses/active-agent metadata.
    /// Safe to call from any thread; raises <see cref="TicketsChanged"/>.
    /// </summary>
    public IReadOnlyList<KaneCodeTicket> Rescan()
    {
        return Refresh();
    }

    /// <summary>Creates a new ticket file and returns the created ticket.</summary>
    public KaneCodeTicket CreateTicket(
        string title,
        string description,
        string? provider = null,
        string? model = null,
        string? agentMode = null,
        int priority = 0,
        string? startAfter = null)
    {
        string filePath = _store.CreateTicket(
            title,
            description,
            TicketStatus.Open,
            provider,
            model,
            agentMode,
            priority,
            startAfter);

        KaneCodeTicket ticket = _store.ReadTicket(filePath);
        Refresh();
        return ticket;
    }

    /// <summary>Deletes a ticket, its worktree, and its agent branch.</summary>
    public void DeleteTicket(KaneCodeTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        string? repositoryRoot = _repositoryRootProvider();
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            _worktreeManager.RemoveWorktree(repositoryRoot, ticket.Title);
        }

        _store.DeleteTicket(ticket);
        Refresh();
    }

    /// <summary>Marks a ticket ignored (skipped by the dispatcher).</summary>
    public void SetIgnored(KaneCodeTicket ticket, bool ignored)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        KaneCodeTicket? current = FindTicketByTitle(ticket.Title);
        if (current is null)
        {
            return;
        }

        if (ignored && current.Status is not TicketStatus.Complete and not TicketStatus.Unable and not TicketStatus.Failed)
        {
            UpdateTicketStatus(current, TicketStatus.Ignore);
        }
        else if (!ignored && current.Status == TicketStatus.Ignore)
        {
            UpdateTicketStatus(current, TicketStatus.Open);
        }

        Refresh();
    }

    /// <summary>Pauses an active ticket run, keeping its agent in memory for later resume.</summary>
    public bool PauseTicket(string ticketId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);

        lock (_activeRuns)
        {
            if (!_activeRuns.TryGetValue(ticketId, out TicketRun? run) || run.IsPaused)
            {
                return false;
            }

            run.IsPaused = true;
            run.CancellationTokenSource.Cancel();
        }

        UpdateTicketStatus(ticketId, TicketStatus.Paused);
        Refresh();
        return true;
    }

    /// <summary>Resumes a paused ticket run.</summary>
    public bool ResumeTicket(string ticketId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);

        TicketRun? run;
        lock (_activeRuns)
        {
            if (!_activeRuns.TryGetValue(ticketId, out run) || !run.IsPaused)
            {
                return false;
            }

            run.IsPaused = false;
            run.Cancelled = false;
            run.Error = null;
            run.Result = null;
            run.CancellationTokenSource = new CancellationTokenSource();
        }

        UpdateTicketStatus(ticketId, TicketStatus.Working);

        // Continue the agent loop with the same agent instance so the existing
        // conversation history is preserved.
        _ = RunTicketAgentAsync(run, "Continue working on this ticket until it is done, then call complete_ticket or unable_to_complete.");
        Refresh();
        return true;
    }

    /// <summary>Reopens a terminal or ignored ticket so it can be dispatched again.</summary>
    public void ReopenTicket(string ticketId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);

        KaneCodeTicket? ticket = FindTicketByTitle(ticketId);
        if (ticket is null)
        {
            return;
        }

        UpdateTicketStatus(ticket, TicketStatus.Open);
        Refresh();
    }

    /// <summary>Manually marks a ticket complete (user action).</summary>
    public void MarkTicketCompleteManually(string ticketId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);

        KaneCodeTicket? ticket = FindTicketByTitle(ticketId);
        if (ticket is null)
        {
            return;
        }

        UpdateTicketStatus(ticket, TicketStatus.Complete);
        Refresh();
    }

    /// <inheritdoc />
    public Task<bool> MarkTicketCompleteAsync(string ticketId, string? summary)
    {
        return Task.FromResult(SetTerminalStatus(ticketId, TicketStatus.Complete, summary));
    }

    /// <inheritdoc />
    public Task<bool> MarkTicketUnableAsync(string ticketId, string? reason)
    {
        return Task.FromResult(SetTerminalStatus(ticketId, TicketStatus.Unable, reason));
    }

    // ── Internals ───────────────────────────────────────────────────

    private bool SetTerminalStatus(string ticketId, TicketStatus status, string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);

        KaneCodeTicket? ticket = FindTicketByTitle(ticketId);
        if (ticket is null)
        {
            return false;
        }

        UpdateTicketStatus(ticket, status);

        if (!string.IsNullOrWhiteSpace(note))
        {
            _lastNote = note;
        }

        return true;
    }

    private string? _lastNote;

    /// <summary>The most recent completion/unable note, surfaced in the status text.</summary>
    internal string? LastNote => _lastNote;

    private string? _lastDispatchIssue;

    /// <summary>
    /// The most recent reason a ticket could not be dispatched (provider/mode not
    /// available, worktree failure, etc.), or null when there is nothing to report.
    /// </summary>
    internal string? LastDispatchIssue => _lastDispatchIssue;

    private IReadOnlyList<KaneCodeTicket> Refresh()
    {
        IReadOnlyList<KaneCodeTicket> tickets = _store.ScanTickets();
        bool overridesAllowed = TicketSettingsManager.Load().AllowTicketOverrides;

        Dictionary<string, TicketRun> runs;
        lock (_activeRuns)
        {
            runs = new Dictionary<string, TicketRun>(_activeRuns, StringComparer.OrdinalIgnoreCase);
        }

        foreach (KaneCodeTicket ticket in tickets)
        {
            if (runs.TryGetValue(ticket.Title, out TicketRun? run))
            {
                ticket.ActiveAgentId = run.AgentId;
                ticket.ActiveAgentDisplayName = run.AgentDisplayName;
                ticket.WorktreePath = run.WorktreePath;

                // Reflect in-memory run state over any stale on-disk status, but never
                // overwrite a terminal status already written by the agent's status tool.
                if (ticket.Status is not TicketStatus.Complete and not TicketStatus.Unable)
                {
                    ticket.Status = run.IsPaused ? TicketStatus.Paused : TicketStatus.Working;
                }

                continue;
            }

            // Apply the override policy. A blocked ticket stays blocked until the user
            // enables overrides or clears the offending option.
            if (ticket.Status == TicketStatus.Open && ticket.HasOverrides && !overridesAllowed)
            {
                ticket.Status = TicketStatus.Blocked;
                try
                {
                    _store.WriteHeader(ticket);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        TicketsChanged?.Invoke(this, new TicketsChangedEventArgs(tickets));
        return tickets;
    }

    private async Task TryDispatchNextAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                return;
            }

            IReadOnlyList<KaneCodeTicket> tickets = Refresh();
            int capacity = Math.Max(0, Settings.MaxConcurrentTickets - ActiveRunCount);
            if (capacity <= 0)
            {
                return;
            }

            foreach (KaneCodeTicket ticket in OrderForDispatch(tickets))
            {
                if (capacity <= 0)
                {
                    break;
                }

                if (!IsEligible(ticket, tickets))
                {
                    continue;
                }

                try
                {
                    if (TryPrepareDispatch(ticket))
                    {
                        capacity--;
                    }
                }
                catch (Exception ex)
                {
                    // A single misbehaving ticket must never take down the dispatch
                    // loop or crash Start()'s direct dispatch call.
                    SetDispatchIssue(ticket.Title, $"Unexpected error dispatching '{ticket.Title}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Never let a dispatch failure take down the timer loop or crash Start().
            SetDispatchIssue(null, $"Ticket dispatch loop error: {ex.Message}");
        }
        finally
        {
            _sync.Release();
        }
    }

    private static IReadOnlyList<KaneCodeTicket> OrderForDispatch(IReadOnlyList<KaneCodeTicket> tickets)
    {
        return tickets
            .Where(ticket => ticket.Status == TicketStatus.Open)
            .OrderByDescending(ticket => ticket.Priority)
            .ThenBy(ticket => ticket.CreatedUtc)
            .ThenBy(ticket => ticket.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsEligible(KaneCodeTicket ticket, IReadOnlyList<KaneCodeTicket> allTickets)
    {
        if (ticket.Status != TicketStatus.Open)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ticket.StartAfter))
        {
            KaneCodeTicket? prerequisite = allTickets.FirstOrDefault(candidate =>
                string.Equals(candidate.Title, ticket.StartAfter, StringComparison.OrdinalIgnoreCase));

            if (prerequisite is null || prerequisite.Status != TicketStatus.Complete)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves configuration and marks the ticket working + creates its worktree.
    /// Returns true when a run was dispatched.
    /// </summary>
    private bool TryPrepareDispatch(KaneCodeTicket ticket)
    {
        if (!TryResolveConfiguration(ticket, out IAiProvider provider, out string model, out IAiChatMode mode, out string? configFailure))
        {
            SetDispatchIssue(
                ticket.Title,
                configFailure ?? $"Could not resolve AI configuration for '{ticket.Title}'.");
            return false;
        }

        // Create the worktree (when a repository is available). Without a repository
        // the agent works directly in the loaded project root.
        string? repositoryRoot = _repositoryRootProvider();
        string? worktreeRoot = null;
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            try
            {
                worktreeRoot = _worktreeManager.CreateWorktree(repositoryRoot, ticket.Title);
            }
            catch (Exception ex)
            {
                // Could not create an isolated worktree. Leave the ticket open rather
                // than dispatching an agent that would mutate the user's workspace.
                SetDispatchIssue(
                    ticket.Title,
                    $"Could not create an isolated Git worktree for '{ticket.Title}': {ex.Message}");
                return false;
            }
        }

        string? projectRootOverride = TicketWorktreeManager.ComputeWorktreeProjectPath(
            worktreeRoot,
            _projectRootProvider(),
            repositoryRoot);

        // Build the effective tool set: the mode's allowed tools plus the two ticket
        // status tools so the agent can report completion regardless of mode.
        HashSet<string>? allowedTools = null;
        if (mode.AllowedTools is not null)
        {
            allowedTools = new HashSet<string>(mode.AllowedTools, StringComparer.Ordinal)
            {
                "complete_ticket",
                "unable_to_complete"
            };
        }

        AiPreset? preset = (mode as PresetMode)?.Preset;
        JsonElement toolsDef = _toolRegistry.SerializeToolDefinitions(allowedTools, preset);
        string? modePrompt = mode.BuildSystemPrompt(toolsDef);
        string systemPrompt = BuildTicketSystemPrompt(ticket, modePrompt);

        string agentId = $"ticket_{SanitizeAgentId(ticket.Title)}_{Guid.NewGuid():N}";
        string displayName = $"Ticket: {ticket.Title}";

        IAgent agent = _orchestrator.CreateTicketAgent(agentId, displayName, provider, model, mode, systemPrompt);

        TicketRun run = new()
        {
            TicketId = ticket.Title,
            AgentId = agentId,
            AgentDisplayName = displayName,
            WorktreePath = worktreeRoot,
            ProjectRootOverride = projectRootOverride,
            Agent = agent,
            ToolsDef = toolsDef,
            CancellationTokenSource = new CancellationTokenSource()
        };

        lock (_activeRuns)
        {
            _activeRuns[ticket.Title] = run;
        }

        UpdateTicketStatus(ticket, TicketStatus.Working);

        string task = BuildTicketTask(ticket);
        _ = RunTicketAgentAsync(run, task);
        return true;
    }

    private bool TryResolveConfiguration(
        KaneCodeTicket ticket,
        out IAiProvider provider,
        out string model,
        out IAiChatMode mode,
        out string? failureReason)
    {
        bool overridesAllowed = TicketSettingsManager.Load().AllowTicketOverrides;

        if (ticket.HasOverrides && !overridesAllowed)
        {
            ticket.Status = TicketStatus.Blocked;
            try
            {
                _store.WriteHeader(ticket);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            provider = null!;
            model = string.Empty;
            mode = null!;
            failureReason =
                $"Ticket '{ticket.Title}' requests a provider/model/mode override, but ticket-side overrides are " +
                "disabled. Enable \"Allow ticket overrides\" in the ticket settings, or remove the Provider/Model/" +
                "AgentMode options from the ticket header.";
            return false;
        }

        IAiProvider? resolvedProvider = _currentProviderProvider();
        if (resolvedProvider is null)
        {
            // Leave the ticket open until a provider is configured.
            provider = null!;
            model = string.Empty;
            mode = null!;
            failureReason =
                "No AI provider is configured. Add and configure a provider in AI Settings, " +
                "then initialize tickets again.";
            return false;
        }

        if (!resolvedProvider.IsConfigured)
        {
            provider = null!;
            model = string.Empty;
            mode = null!;
            failureReason =
                $"The active AI provider '{resolvedProvider.DisplayName}' is not configured (missing API key or " +
                "endpoint). Open AI Settings to configure it, then initialize tickets again.";
            return false;
        }

        string resolvedModel = _currentModelProvider()
            ?? resolvedProvider.AvailableModels.FirstOrDefault()
            ?? "default";

        IAiChatMode? resolvedMode = _currentModeProvider();
        if (resolvedMode is null || !resolvedMode.ToolsEnabled)
        {
            resolvedMode = _modeRegistry.Get("agent") ?? _modeRegistry.Default;
        }

        if (resolvedMode is null)
        {
            provider = null!;
            model = string.Empty;
            mode = null!;
            failureReason =
                "No AI mode with tools enabled is available. Switch the chat panel to a mode such as Agent, " +
                "then initialize tickets again.";
            return false;
        }

        // Per-ticket overrides (only when enabled).
        if (overridesAllowed && ticket.HasProviderOverride)
        {
            IAiProvider? found = FindProvider(ticket.Provider!);
            if (found is not null && found.IsConfigured)
            {
                resolvedProvider = found;
            }
        }

        if (overridesAllowed && ticket.HasModelOverride)
        {
            resolvedModel = ticket.Model!;
        }

        if (overridesAllowed && ticket.HasAgentModeOverride)
        {
            IAiChatMode? found = ResolveMode(ticket.AgentMode!);
            if (found is not null && found.ToolsEnabled)
            {
                resolvedMode = found;
            }
        }

        provider = resolvedProvider;
        model = resolvedModel;
        mode = resolvedMode;
        failureReason = null;
        return true;
    }

    private async Task RunTicketAgentAsync(TicketRun run, string task)
    {
        try
        {
            using (AgentToolContext.PushRunContext(run.ProjectRootOverride, run.TicketId))
            {
                run.Result = await run.Agent.RunAsync(
                    task,
                    run.ToolsDef,
                    _toolRegistry,
                    _orchestrator.FileLockManager,
                    _orchestrator,
                    MaxIterations,
                    run.CancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            run.Cancelled = true;
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
        }

        if (run.IsPaused)
        {
            // The run was paused mid-flight; keep the agent in memory for resume.
            return;
        }

        FinalizeTicketRun(run);
    }

    private void FinalizeTicketRun(TicketRun run)
    {
        try
        {
            KaneCodeTicket? ticket = FindTicketByTitle(run.TicketId);

            if (ticket is not null)
            {
                TicketStatus currentStatus = ticket.Status;

                if (currentStatus is not TicketStatus.Complete and not TicketStatus.Unable)
                {
                    TicketStatus finalStatus;
                    if (run.Cancelled)
                    {
                        finalStatus = TicketStatus.Failed;
                    }
                    else if (run.Result?.Success == true)
                    {
                        // The agent finished without calling a status tool; treat a
                        // successful run as complete.
                        finalStatus = TicketStatus.Complete;
                    }
                    else
                    {
                        finalStatus = TicketStatus.Failed;
                    }

                    UpdateTicketStatus(ticket, finalStatus);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        // The run is over, so its file locks must go back even though the agent itself
        // stays registered for inspection (RemoveAgent would normally have released them).
        try
        {
            _orchestrator.FileLockManager.ReleaseAll(run.AgentId);
        }
        catch (Exception)
        {
        }

        // Keep the agent in the tree so its session remains readable, retiring the
        // oldest finished agents once the retention limit is reached. The worktree and
        // branch are kept too so the user can review the agent's commits later.
        RetainFinishedAgent(run.AgentId);

        lock (_activeRuns)
        {
            _activeRuns.Remove(run.TicketId);
        }

        Refresh();

        // Dispatch the next eligible ticket.
        _ = TryDispatchNextAsync();
    }

    /// <summary>
    /// Records a finished ticket agent as retained, removing the ones that have aged
    /// past <see cref="RetainedFinishedAgents"/> from the orchestrator.
    /// </summary>
    private void RetainFinishedAgent(string agentId)
    {
        List<string> retired = [];

        lock (_finishedAgentIds)
        {
            _finishedAgentIds.Enqueue(agentId);
            while (_finishedAgentIds.Count > RetainedFinishedAgents)
            {
                retired.Add(_finishedAgentIds.Dequeue());
            }
        }

        foreach (string retiredAgentId in retired)
        {
            try
            {
                _orchestrator.RemoveAgent(retiredAgentId);
            }
            catch (Exception)
            {
            }
        }
    }

    private void UpdateTicketStatus(string ticketId, TicketStatus status)
    {
        KaneCodeTicket? ticket = FindTicketByTitle(ticketId);
        if (ticket is not null)
        {
            UpdateTicketStatus(ticket, status);
        }
    }

    private void UpdateTicketStatus(KaneCodeTicket ticket, TicketStatus status)
    {
        ticket.Status = status;
        try
        {
            _store.WriteHeader(ticket);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SetDispatchIssue(string? ticketTitle, string? reason)
    {
        _lastDispatchIssue = reason;
        DispatchIssue?.Invoke(this, new TicketDispatchIssueEventArgs(ticketTitle, reason));
    }

    private KaneCodeTicket? FindTicketByTitle(string title)
    {
        return _store.ScanTickets().FirstOrDefault(ticket =>
            string.Equals(ticket.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    private IAiProvider? FindProvider(string providerRef)
    {
        return _providerRegistry.Providers.FirstOrDefault(provider =>
            string.Equals(provider.ProviderId, providerRef, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider.DisplayName, providerRef, StringComparison.OrdinalIgnoreCase));
    }

    private IAiChatMode? ResolveMode(string modeId)
    {
        IAiChatMode? mode = _modeRegistry.Get(modeId);
        if (mode is not null)
        {
            return mode;
        }

        // Built-in modes may be referenced by their display name (e.g. "Agent").
        mode = _modeRegistry.Modes.FirstOrDefault(m =>
            string.Equals(m.DisplayName, modeId, StringComparison.OrdinalIgnoreCase));
        if (mode is not null)
        {
            return mode;
        }

        if (modeId.StartsWith("preset:", StringComparison.Ordinal))
        {
            string presetId = modeId["preset:".Length..];
            AiPreset? preset = AiPresetManager.Load()
                .FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.Ordinal));
            if (preset is not null)
            {
                return new PresetMode(preset, _toolRegistry);
            }
        }

        // Fall back to a preset referenced by name.
        AiPreset? presetByName = AiPresetManager.Load()
            .FirstOrDefault(p => string.Equals(p.Name, modeId, StringComparison.OrdinalIgnoreCase));
        if (presetByName is not null)
        {
            return new PresetMode(presetByName, _toolRegistry);
        }

        return null;
    }

    private static string BuildTicketTask(KaneCodeTicket ticket)
    {
        string description = string.IsNullOrWhiteSpace(ticket.Description)
            ? "(no additional details were provided)"
            : ticket.Description.Trim();

        return
            $"Ticket title: {ticket.Title}\n" +
            $"Priority: {ticket.Priority}\n\n" +
            $"{description}";
    }

    private static string BuildTicketSystemPrompt(KaneCodeTicket ticket, string? modePrompt)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(modePrompt))
        {
            parts.Add(modePrompt.TrimEnd());
        }

        parts.Add(
            "You are working autonomously on a KaneCode ticket. " +
            "Complete the requested work using the available tools, verify it where possible " +
            "(build and run tests when appropriate), and then report your outcome.");

        parts.Add(
            "When you are finished, you MUST make one of these tool calls as your final action:\n" +
            "- complete_ticket (with a summary) when the work is done.\n" +
            "- unable_to_complete (with a reason) when you cannot finish without something you lack.");

        parts.Add(
            "Only modify files inside the project you are working in. Do not touch the user's " +
            "active workspace or unrelated files. If a Git repository is available, commit your " +
            "work so it can be reviewed later.");

        return string.Join("\n\n", parts);
    }

    private static string SanitizeAgentId(string title)
    {
        System.Text.StringBuilder builder = new(title.Length);
        foreach (char c in title)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        Stop();

        // Intentionally do not dispose the semaphore here. A background run may
        // still be inside TryDispatchNextAsync and release the semaphore after
        // Stop() is observed; disposing it would turn that release into an
        // ObjectDisposedException during shutdown.
    }

    private sealed class TicketRun
    {
        public required string TicketId { get; init; }
        public required string AgentId { get; init; }
        public required string AgentDisplayName { get; init; }
        public string? WorktreePath { get; init; }
        public string? ProjectRootOverride { get; init; }
        public required IAgent Agent { get; init; }
        public JsonElement ToolsDef { get; init; }
        public required CancellationTokenSource CancellationTokenSource { get; set; }
        public AgentRunResult? Result { get; set; }
        public bool IsPaused { get; set; }
        public bool Cancelled { get; set; }
        public string? Error { get; set; }
    }
}

/// <summary>Event arguments carrying the refreshed ticket list.</summary>
internal sealed class TicketsChangedEventArgs : EventArgs
{
    public IReadOnlyList<KaneCodeTicket> Tickets { get; }

    public TicketsChangedEventArgs(IReadOnlyList<KaneCodeTicket> tickets)
    {
        Tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
    }
}

/// <summary>
/// Event arguments describing why the dispatcher declined a ticket or hit an error.
/// A null <see cref="Reason"/> means the issue was cleared.
/// </summary>
internal sealed class TicketDispatchIssueEventArgs : EventArgs
{
    /// <summary>The ticket title the issue applies to, or null when the issue is system-wide.</summary>
    public string? TicketTitle { get; }

    /// <summary>Human-readable explanation of the dispatch problem, or null when cleared.</summary>
    public string? Reason { get; }

    public TicketDispatchIssueEventArgs(string? ticketTitle, string? reason)
    {
        TicketTitle = ticketTitle;
        Reason = reason;
    }
}
