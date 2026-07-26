using System.Text.Json;

namespace KaneCode.Services.Ai.Agents;

/// <summary>
/// Default implementation of <see cref="IAgent"/>.
///
/// Each agent runs an independent tool-calling loop:
///   1. Send conversation history + task to the provider
///   2. Stream tokens back, collecting response + tool calls
///   3. Execute tool calls (with file-lock awareness)
///   4. Feed tool results back into the conversation
///   5. Repeat until the task is complete or the limit is reached
///
/// File-locking integration: before executing a write tool on a file path,
/// the agent acquires a lock via <see cref="FileLockManager"/>. Conflicts
/// are reported back to the model as tool errors so it can retry or adjust.
/// </summary>
internal sealed class Agent : IAgent
{
    private readonly List<AiChatMessage> _messages = [];
    private readonly HashSet<string> _childIds = new(StringComparer.Ordinal);

    public string Id { get; }

    public AgentRole Role { get; }

    public string DisplayName { get; }

    public IAiProvider Provider { get; }

    public string Model { get; }

    public IAiChatMode Mode { get; }

    public string? SystemPrompt { get; }

    public string? ParentId { get; }

    public IReadOnlySet<string> ChildIds => _childIds;

    public IReadOnlyList<AiChatMessage> Messages => _messages;

    /// <summary>
    /// Callback invoked when the agent is about to execute a tool.
    /// The agent passes itself so the orchestrator can perform any
    /// pre-execution steps (e.g. logging, UI updates).
    /// </summary>
    internal Func<Agent, string, JsonElement, CancellationToken, Task<ToolCallResult>>? ToolExecutionInterceptor { get; set; }

    /// <summary>
    /// Callback invoked when the agent receives a streaming token.
    /// For UI integration (e.g. rendering content/thinking inline).
    /// </summary>
    internal Func<Agent, AiStreamToken, Task>? TokenCallback { get; set; }

    /// <summary>
    /// Callback invoked at the start of each tool-call loop iteration.
    /// The agent passes its current iteration index.
    /// </summary>
    internal Func<Agent, int, Task>? IterationCallback { get; set; }

    public Agent(
        string id,
        AgentRole role,
        string displayName,
        IAiProvider provider,
        string model,
        IAiChatMode mode,
        string? systemPrompt = null,
        string? parentId = null)
    {
        Id = !string.IsNullOrWhiteSpace(id)
            ? id
            : throw new ArgumentException("Agent ID cannot be empty.", nameof(id));
        Role = role;
        DisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : throw new ArgumentException("Display name cannot be empty.", nameof(displayName));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Model = !string.IsNullOrWhiteSpace(model)
            ? model
            : throw new ArgumentException("Model cannot be empty.", nameof(model));
        Mode = mode ?? throw new ArgumentNullException(nameof(mode));
        SystemPrompt = systemPrompt;
        ParentId = parentId;
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> RunAsync(
        string task,
        JsonElement toolsDef,
        AgentToolRegistry toolRegistry,
        FileLockManager fileLockManager,
        AgentOrchestrator orchestrator,
        int maxIterations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(fileLockManager);
        ArgumentNullException.ThrowIfNull(orchestrator);

        if (maxIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIterations), "Max iterations must be at least 1.");
        }

        // Add the task as a user message
        AiChatMessage taskMessage = new(AiChatRole.User, task);
        _messages.Add(taskMessage);

        // Build the request history (with system prompt if configured)
        List<AiChatMessage> requestHistory = BuildInitialRequestHistory(toolsDef);

        return await RunWithHistoryAsync(
            requestHistory, toolsDef, toolRegistry, fileLockManager, orchestrator, maxIterations, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> RunWithHistoryAsync(
        List<AiChatMessage> requestHistory,
        JsonElement toolsDef,
        AgentToolRegistry toolRegistry,
        FileLockManager fileLockManager,
        AgentOrchestrator orchestrator,
        int maxIterations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestHistory);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(fileLockManager);
        ArgumentNullException.ThrowIfNull(orchestrator);

        if (maxIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIterations), "Max iterations must be at least 1.");
        }

        int totalToolCallCount = 0;
        AiUsageStats? mergedUsageStats = null;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IterationCallback is not null)
            {
                await IterationCallback(this, iteration).ConfigureAwait(false);
            }

            // Step 1: Stream completion from provider
            System.Text.StringBuilder responseBuilder = new();
            System.Text.StringBuilder reasoningBuilder = new();
            Dictionary<int, AiStreamToolCall> streamedToolCalls = new();

            bool streamResponses = true;

            await foreach (AiStreamToken token in Provider.StreamCompletionAsync(
                requestHistory,
                Model,
                toolsDef,
                streamResponses,
                cancellationToken).ConfigureAwait(false))
            {
                if (token.Type == AiStreamTokenType.Usage)
                {
                    mergedUsageStats = MergeUsageStats(mergedUsageStats, token.UsageStats);
                    continue;
                }

                if (TokenCallback is not null)
                {
                    await TokenCallback(this, token).ConfigureAwait(false);
                }

                if (token.Type == AiStreamTokenType.ToolCall && token.ToolCall is not null)
                {
                    AiStreamToolCall toolCall = token.ToolCall!;
                    streamedToolCalls[toolCall.Index] = toolCall;
                    continue;
                }

                if (token.Type == AiStreamTokenType.Reasoning)
                {
                    reasoningBuilder.Append(token.Text);
                    continue;
                }

                // Content token
                responseBuilder.Append(token.Text);
            }

            string responseContent = responseBuilder.ToString();
            string reasoningContent = reasoningBuilder.ToString();

            // Step 2: Check for malformed tool calls
            List<RecoveredMalformedToolCall> recoveredMalformedToolCalls =
                Mode.ToolsEnabled && streamedToolCalls.Count == 0
                    ? MalformedToolCallRecovery.Recover(reasoningContent, responseContent).ToList()
                    : [];

            // Step 3: Collect pending tool calls
            List<AiStreamToolCall> pendingToolCalls = [];
            if (Mode.ToolsEnabled)
            {
                if (streamedToolCalls.Count > 0)
                {
                    pendingToolCalls = streamedToolCalls
                        .OrderBy(kv => kv.Key)
                        .Select(kv => kv.Value)
                        .Where(tc => !string.IsNullOrWhiteSpace(tc.FunctionName))
                        .ToList();
                }
                else if (recoveredMalformedToolCalls.Count > 0)
                {
                    pendingToolCalls = recoveredMalformedToolCalls
                        .Select((tc, i) => new AiStreamToolCall(
                            tc.Index,
                            $"malformed_tool_call_{iteration}_{i}",
                            tc.FunctionName,
                            tc.ArgumentsJson))
                        .ToList();
                }
            }

            // Step 4: If no tool calls, we're done — return the final response
            if (pendingToolCalls.Count == 0)
            {
                AiChatMessage finalAssistantMessage = new(AiChatRole.Assistant, responseContent)
                {
                    ThinkingContent = string.IsNullOrWhiteSpace(reasoningContent) ? null : reasoningContent
                };
                _messages.Add(finalAssistantMessage);
                requestHistory.Add(finalAssistantMessage);

                return AgentRunResult.Ok(responseContent, iteration + 1, totalToolCallCount, mergedUsageStats);
            }

            // Step 5: Record the assistant message with its tool calls
            List<AiToolCallRequest> toolCallRequests = pendingToolCalls
                .Select(tc =>
                {
                    string toolCallId = string.IsNullOrWhiteSpace(tc.Id)
                        ? $"tool_call_{tc.Index}"
                        : tc.Id;

                    return new AiToolCallRequest(toolCallId, tc.FunctionName, tc.ArgumentsJson);
                })
                .ToList();

            AiChatMessage toolCallingAssistantMessage = new(AiChatRole.Assistant, responseContent)
            {
                ThinkingContent = string.IsNullOrWhiteSpace(reasoningContent) ? null : reasoningContent,
                ToolCalls = toolCallRequests
            };
            _messages.Add(toolCallingAssistantMessage);
            requestHistory.Add(toolCallingAssistantMessage);

            // Step 6: Execute each tool call
            foreach (AiStreamToolCall toolCall in pendingToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string toolCallId = string.IsNullOrWhiteSpace(toolCall.Id)
                    ? $"tool_call_{toolCall.Index}"
                    : toolCall.Id;

                ToolCallResult result;

                if (ToolExecutionInterceptor is not null)
                {
                    // Let the orchestrator handle execution (for file-lock integration, etc.)
                    using JsonDocument? argumentsDocument = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
                        ? null
                        : AgentToolArgumentsParser.Parse(toolCall.FunctionName, toolCall.ArgumentsJson);

                    JsonElement args = argumentsDocument is null
                        ? default
                        : argumentsDocument.RootElement;

                    result = await ToolExecutionInterceptor(this, toolCall.FunctionName, args, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    result = await ExecuteToolInternal(
                        toolCall.FunctionName,
                        toolCall.ArgumentsJson,
                        toolRegistry,
                        fileLockManager,
                        orchestrator,
                        cancellationToken).ConfigureAwait(false);
                }

                totalToolCallCount++;

                string resultContent = result.Success
                    ? result.Output
                    : $"Error: {result.Error}";

                AiChatMessage toolMessage = new(AiChatRole.Tool, resultContent)
                {
                    ToolCallId = toolCallId
                };
                _messages.Add(toolMessage);
                requestHistory.Add(toolMessage);
            }

            // Loop continues to the next iteration
        }

        // Reached max iterations without a final response
        return AgentRunResult.Fail(
            $"Agent reached the maximum number of iterations ({maxIterations}) without completing the task.",
            maxIterations,
            totalToolCallCount);
    }

    /// <inheritdoc />
    public void AddMessage(AiChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
    }

    /// <inheritdoc />
    public void ReceiveChildResult(string childAgentId, AgentRunResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAgentId);
        ArgumentNullException.ThrowIfNull(result);

        string content = result.Success
            ? $"Sub-agent '{childAgentId}' completed successfully:\n{result.Summary}"
            : $"Sub-agent '{childAgentId}' failed:\n{result.Summary}";

        // Add as a tool result message (from spawn_agent tool)
        AiChatMessage toolMessage = new(AiChatRole.Tool, content)
        {
            ToolCallId = $"spawn_{childAgentId}"
        };
        _messages.Add(toolMessage);
    }

    /// <inheritdoc />
    public void RegisterChild(string childAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAgentId);
        _childIds.Add(childAgentId);
    }

    /// <inheritdoc />
    public void UnregisterChild(string childAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAgentId);
        _childIds.Remove(childAgentId);
    }

    /// <summary>
    /// Builds the initial request history including the system prompt.
    /// Merges the agent's <see cref="SystemPrompt"/> with any existing system messages.
    /// </summary>
    private List<AiChatMessage> BuildInitialRequestHistory(JsonElement toolsDef)
    {
        List<AiChatMessage> history = [];
        List<string> systemParts = [];

        // Agent-specific system prompt (from mode or custom)
        string? modePrompt = SystemPrompt ?? Mode.BuildSystemPrompt(toolsDef);
        if (!string.IsNullOrWhiteSpace(modePrompt))
        {
            systemParts.Add(modePrompt);
        }

        // Parent-context prompt for sub-agents
        if (Role == AgentRole.SubAgent)
        {
            systemParts.Add(
                "You are a sub-agent working on a delegated task. " +
                "Complete the task using available tools, then provide a clear summary of your findings. " +
                "Do not spawn additional sub-agents unless explicitly instructed by the user.");
        }

        if (systemParts.Count > 0)
        {
            history.Add(new AiChatMessage(AiChatRole.System, string.Join("\n\n", systemParts)));
        }

        // Add existing conversation messages (for continuation scenarios)
        // Skip the first message if it's a system prompt that we already merged
        bool skipFirstSystemMessage = systemParts.Count > 0;
        foreach (AiChatMessage message in _messages)
        {
            if (skipFirstSystemMessage && message.Role == AiChatRole.System)
            {
                skipFirstSystemMessage = false;
                continue;
            }

            history.Add(message);
        }

        return history;
    }

    /// <summary>
    /// Internal tool execution with file-lock awareness.
    /// </summary>
    private async Task<ToolCallResult> ExecuteToolInternal(
        string toolName,
        string argumentsJson,
        AgentToolRegistry toolRegistry,
        FileLockManager fileLockManager,
        AgentOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        IAgentTool? tool = toolRegistry.Get(toolName);
        if (tool is null)
        {
            return ToolCallResult.Fail($"Unknown tool: {toolName}");
        }

        // Check if the active mode allows this tool
        if (!Mode.IsToolAllowed(toolName))
        {
            return ToolCallResult.Fail($"Tool '{toolName}' is not allowed in {Mode.DisplayName} mode.");
        }

        // Parse arguments
        using JsonDocument? argumentsDocument = string.IsNullOrWhiteSpace(argumentsJson)
            ? null
            : AgentToolArgumentsParser.Parse(toolName, argumentsJson);

        JsonElement args = argumentsDocument is null
            ? default
            : argumentsDocument.RootElement;

        // Acquire file locks for write tools
        FileLockResult? lockResult = AcquireFileLockIfNeeded(toolName, args, fileLockManager);
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

    /// <summary>
    /// Attempts to acquire a file lock for write-type tools.
    /// Returns null if the tool doesn't need locking (read-only tools).
    /// </summary>
    private FileLockResult? AcquireFileLockIfNeeded(
        string toolName,
        JsonElement args,
        FileLockManager fileLockManager)
    {
        // Tools that write/modify files and need locking
        string? filePath = ExtractFilePath(toolName, args);
        if (filePath is null)
        {
            return null;
        }

        return fileLockManager.TryAcquireWriteLockWithResult(filePath, Id);
    }

    /// <summary>
    /// Extracts the file path from tool arguments for lock acquisition.
    /// Returns null for tools that don't operate on files or are read-only.
    /// </summary>
    private static string? ExtractFilePath(string toolName, JsonElement args)
    {
        // Write tools that modify files
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

        if (!isWriteTool)
        {
            return null;
        }

        // Extract filePath or destinationPath from args
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (args.TryGetProperty("filePath", out JsonElement filePathElement) &&
            filePathElement.ValueKind == JsonValueKind.String)
        {
            string? path = filePathElement.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        if (args.TryGetProperty("destinationPath", out JsonElement destPathElement) &&
            destPathElement.ValueKind == JsonValueKind.String)
        {
            string? path = destPathElement.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        if (args.TryGetProperty("sourcePath", out JsonElement sourcePathElement) &&
            sourcePathElement.ValueKind == JsonValueKind.String)
        {
            string? path = sourcePathElement.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    private static AiUsageStats? MergeUsageStats(AiUsageStats? existing, AiUsageStats? next)
    {
        if (next is null)
        {
            return existing;
        }

        if (existing is null)
        {
            return next;
        }

        return new AiUsageStats(
            existing.PromptTokens + next.PromptTokens,
            existing.CompletionTokens + next.CompletionTokens,
            existing.TotalTokens + next.TotalTokens);
    }
}
