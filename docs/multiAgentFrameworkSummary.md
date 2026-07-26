# Multi-Agent Framework — Backend Summary

**Branch:** `feature/multi-agent-framework`  
**Date:** 2026-07-19  
**Commit:** `9b2d707`

---

## Overview

This branch introduces a multi-agent / sub-agent framework backend for KaneCode. Each AI chat session is modeled as an **Agent** — a self-contained entity with its own provider, model, mode, conversation history, and tool-calling loop. Agents are organized in a tree: a **root agent** (the main AI chat) can spawn **sub-agents** to handle delegated tasks with their own context windows, providers, or models.

The framework is wired into the existing `AiChatPanel` and `MainWindow` so that `spawn_agent` tool calls are intercepted and delegated to the orchestrator without breaking the existing chat flow.

**The root agent is now created eagerly** when the AI provider is configured, and **all tool execution from the main chat panel is routed through the `AgentOrchestrator`**. This ensures file-locking and tool interception apply consistently to the main chat and all sub-agents.

---

## Files Created

### `Services/Ai/Agents/IAgent.cs`
Core interface for an agent:
- `Id`, `Role`, `DisplayName` — identity and role in the tree
- `Provider`, `Model`, `Mode`, `SystemPrompt` — independent AI configuration
- `ParentId`, `ChildIds` — tree navigation
- `Messages` — the agent's own conversation history
- `RunAsync()` — runs the full tool-calling loop on a given task
- `AddMessage()`, `ReceiveChildResult()`, `RegisterChild()`, `UnregisterChild()` — message routing

### `Services/Ai/Agents/Agent.cs`
Concrete implementation of `IAgent`. Contains the extracted tool-calling loop:

1. **Build request history** — merges the agent's system prompt with its message history
2. **Stream completion** from the provider, collecting response text + tool calls
3. **Check for tool calls** — if none, the agent is done; return the final response
4. **Execute each tool call** — each tool runs with file-lock awareness via `FileLockManager`
5. **Feed results back** — tool results are added to the conversation; loop continues

Key design points:
- **File-lock integration**: before executing write tools (`write`, `edit`, `delete`, `rename_path`), the agent extracts the target file path and acquires a lock. Conflicts return `ToolCallResult.Conflict()`.
- **UI callbacks**: `ToolExecutionInterceptor`, `TokenCallback`, and `IterationCallback` allow the UI layer to hook into the agent loop for streaming display.
- **Sub-agent guard**: sub-agents get a system prompt addendum telling them not to spawn further sub-agents unless instructed.
- **Max iterations**: configurable per run; returns a failure result if exceeded.

### `Services/Ai/Agents/AgentOrchestrator.cs`
Central manager for the multi-agent system:

- **Agent lifecycle**: `CreateRootAgent()`, `SpawnSubAgent()`, `RemoveAgent()` — creates/destroys agents, maintains the tree, cascades removal to descendants.
- **Tool execution interceptor**: `OnToolExecution()` intercepts all tool calls. For `spawn_agent`, it creates a sub-agent, runs it to completion, and returns the result. For other tools, it looks up the tool in the registry and applies file-lock checks.
- **Shared `FileLockManager`**: single instance used by all agents.
- **Agent lookup**: `GetAgent()`, `GetAllAgents()`, `GetAgentsAtDepth()`, `GetAncestorChain()`.
- **Events**: `AgentChanged` (created/removed), `SubAgentCompleted`.
- **`IDisposable`**: removes all agents on disposal.

### `Services/Ai/Agents/AgentRole.cs`
Simple enum: `Root` (main chat) and `SubAgent` (delegated task).

### `Services/Ai/Agents/AgentRunResult.cs`
Encapsulates the outcome of an agent's `RunAsync()`:
- `Success` — whether the agent completed its task
- `Summary` — the final text (conclusion or error description)
- `Iterations` — number of tool-call loop iterations executed
- `ToolCallCount` — total tool calls made
- `UsageStats` — aggregated token usage across all provider calls

Static factories: `Ok()` and `Fail()`.

### `Services/Ai/Agents/FileLockManager.cs`
Thread-safe file-lock manager using `ConcurrentDictionary`:

- **Write locks**: `TryAcquireWriteLock(filePath, agentId)` — if the file is locked by another agent, returns false.
- **Lock result**: `TryAcquireWriteLockWithResult()` returns a `FileLockResult` with conflict details (conflicting agent ID, acquisition time).
- **Release**: `ReleaseAll(agentId)` — called when an agent completes or is removed. `Release(filePath, agentId)` for individual unlocks.
- **Snapshot**: `GetSnapshot()` for debugging/monitoring.
- **Path normalization**: uses `Path.GetFullPath()` for consistent keys.

Supporting types: `FileLockType` (Write/Read), `FileLockInfo`, `FileLockResult`.

### `Services/Ai/Agents/SpawnAgentTool.cs`
Tool definition registered in the `AgentToolRegistry` so it appears in the tools UI:

- **Name**: `spawn_agent`
- **Category**: `"Agent"` (new category for the tools dropdown)
- **Parameters**: `task` (required), `displayName`, `provider`, `model`, `mode`, `systemPrompt`, `maxIterations` (all optional)
- **`ExecuteAsync()`**: returns a failure directing the caller to use the orchestrator. The actual execution is intercepted by `AgentOrchestrator.OnToolExecution()` or `AiChatPanel.ExecuteSpawnAgentViaOrchestratorAsync()`.

---

## Files Modified

### `Services/Ai/ToolCallResult.cs`
Added `Conflict()` static factory:
```csharp
public static ToolCallResult Conflict(string message) => new()
{
    Success = false,
    Error = $"[CONFLICT] {message}"
};
```
This allows write tools to report file-lock conflicts back to the model with a distinctive prefix so the model can recognize and retry.

### `Services/Ai/Agents/IAgent.cs`
Added `RunWithHistoryAsync()` method — runs the tool-calling loop on a pre-built conversation history without adding a user message or building initial request history. This is the single backend used by both `RunAsync()` and the main AI chat panel.

### `Services/Ai/Agents/Agent.cs`
- **Refactored `RunAsync()`** to delegate to `RunWithHistoryAsync()`, eliminating duplicated tool-loop logic.
- **Implemented `RunWithHistoryAsync()`** — the canonical tool-calling loop that accepts a pre-built `List<AiChatMessage>`. Messages are appended to both the internal `_messages` list and the provided request history.

### `Services/Ai/Agents/AgentOrchestrator.cs`
- **Added `ExecuteToolAsync()`** — public entry point for tool execution used by `AiChatPanel.SendMessageAsync()`. Delegates to `OnToolExecution()` for file-locking and spawn_agent interception.
- **Fixed `AcquireFileLockIfNeeded()`** — now accepts the agent ID instead of a hardcoded `"orchestrator"` string, so file locks are correctly attributed to the calling agent.

### `MainWindow.xaml.cs`
- **Added `EnsureRootAgent()`** — creates the root agent eagerly in `ConfigureAiChatPanel()` so it exists before any chat messages are sent. Uses the default mode and active provider/model.
- **Removed lazy root agent creation** from the chat flow (was previously in `ExecuteSpawnAgentViaOrchestratorAsync`).

### `Controls/AiChatPanel.xaml.cs`
- **Added `GetRootAgentId()`** helper — returns the orchestrator's root agent ID for tool execution routing.
- **Added `SyncRootAgentConfig()`** — recreates the root agent when provider, model, or mode changes. Called from `Configure()`, `ModeSelector_SelectionChanged`, `SwitchToMode()`, and `ModelListBox_SelectionChanged`.
- **Updated `SendMessageAsync()` tool execution** — all non-spawn_agent tool calls now route through `_agentOrchestrator.ExecuteToolAsync()` instead of direct `tool.ExecuteAsync()`. This gives the main chat file-lock awareness consistent with sub-agents.
- **Simplified `ExecuteSpawnAgentViaOrchestratorAsync()`** — removed the lazy root agent creation block since the root now exists eagerly.

---

## Architecture Diagram

```
MainWindow
  ├── AgentOrchestrator
  │     ├── FileLockManager (shared)
  │     ├── RootAgent (eager, mirrors AiChatPanel config)
  │     │     └── SubAgent (spawned on demand)
  │     │           └── SubAgent (nested, if needed)
  │     ├── AgentToolRegistry (shared)
  │     └── ExecuteToolAsync() — public entry for main chat tool execution
  │
  └── AiChatPanel
        ├── AgentOrchestrator reference
        ├── Existing chat loop (preserved for UI streaming)
        ├── Tool execution → delegates to AgentOrchestrator.ExecuteToolAsync()
        └── spawn_agent interception → delegates to orchestrator
```

---

## Tool Call Flow for `spawn_agent`

```
1. Model emits: spawn_agent(task="analyze this code"), spawn_agent(task="list xaml files"), write(file="notes.txt")
2. All three tools execute simultaneously via Task.WhenAll:
   a. spawn_agent("analyze this code") → sub-agent runs in parallel
   b. spawn_agent("list xaml files") → sub-agent runs in parallel
   c. write("notes.txt") → writes file in parallel
3. Results are collected and added to conversation in the original order
4. Model receives all three tool results in the next iteration
```

---

## File-Lock Conflict Flow

```
1. Agent A calls: write(filePath: "foo.cs", content: "...")
2. Agent.RunAsync() → AcquireFileLockIfNeeded("write", args)
3. FileLockManager.TryAcquireWriteLockWithResult("foo.cs", agentA.Id)
   → SUCCESS: lock acquired
4. Agent B calls: edit(filePath: "foo.cs", oldText: "...", newText: "...")
5. Agent.RunAsync() → AcquireFileLockIfNeeded("edit", args)
6. FileLockManager.TryAcquireWriteLockWithResult("foo.cs", agentB.Id)
   → CONFLICT: file locked by agentA
7. Returns ToolCallResult.Conflict("File is locked by agent 'agentA' ...")
8. Model receives: "Error: [CONFLICT] File is locked by agent 'agentA' ..."
9. Model can retry after agentA completes and releases the lock
```

---

## What's Not Done Yet (Future UI Work)

1. **Agent tree visualization** — a panel showing the agent hierarchy, their status (idle/running/done), and live output from sub-agents.
2. **Agent lifecycle panel** — a debug/log panel showing agent creation, completion, file-lock activity.
3. **Replacing the inline tool loop** — the `AiChatPanel.SendMessageAsync()` still uses its own inline loop for UI streaming. The extracted `Agent.RunWithHistoryAsync()` is available as the single backend loop; the UI would need to be reworked to use `TokenCallback` + `IterationCallback` for streaming display.
4. **Cross-agent messaging beyond parent↔child** — the current pattern limits communication to the tree structure. A message bus could allow arbitrary agent-to-agent communication.
5. **Agent state persistence** — sub-agents are ephemeral (created and destroyed during a single `spawn_agent` call). Long-lived sub-agents that persist across sessions could be added.

## Completed Since Original Implementation

1. ✅ **Root agent created eagerly** — the root agent now exists from startup, not just when `spawn_agent` is first called.
2. ✅ **Tool execution unified** — all tool calls from the main chat go through `AgentOrchestrator.ExecuteToolAsync()`, giving the main chat file-locking and consistent tool interception.
3. ✅ **`RunWithHistoryAsync()` added** — the tool-calling loop is extracted into a single method used by both `RunAsync()` and (in the future) the main chat's streaming loop.
4. ✅ **File-lock attribution fixed** — locks are attributed to the correct agent ID instead of hardcoded `"orchestrator"`.
5. ✅ **Parallel tool execution** — all tool calls within a single iteration now execute concurrently via `Task.WhenAll()` in both `Agent.RunWithHistoryAsync()` and `AiChatPanel.SendMessageAsync()`. Results are collected and added to the conversation in the original order.
   - **Backend** (`Agent.cs`): `ExecuteSingleToolCallAsync()` helper captures each tool's result independently; results are ordered by index before adding to history.
   - **Frontend** (`AiChatPanel.cs`): Phase 1 creates all UI blocks upfront, Phase 2 runs all tools in parallel, Phase 3 finalizes blocks in order, Phase 4 adds results to conversation history.
   - **Thread safety**: `FileLockManager` (`ConcurrentDictionary`) and `AgentOrchestrator._spawnLock` already guard concurrent access; per-tool `CancellationTokenSource` instances allow individual cancellation within the parallel batch.

---

## How to Test

1. Launch KaneCode with an AI provider configured
2. Switch to Agent mode
3. Ensure "spawn_agent" appears in the tools list (under the "Agent" category)
4. Send a message like: "Use spawn_agent to create a sub-agent that lists all files in the project"
5. The sub-agent should run, list files, and report back
6. To test parallel execution: "Use spawn_agent to create two sub-agents — one that lists .cs files and another that lists .xaml files"
7. Both sub-agents should run simultaneously, and results should appear in order

---

## Design Decisions

- **Each agent is independent**: different provider, model, mode, and context window. This lets the root agent use a fast/cheap model while sub-agents use more capable models for complex tasks.
- **Parent-child communication only by default**: keeps the architecture simple and prevents chaotic message routing. Can be relaxed later.
- **File-lock manager is shared**: all agents in an orchestrator share one `FileLockManager` to prevent conflicting edits regardless of tree position.
- **Eager root agent creation**: the root agent is created at startup so it's always available for tool execution routing and session display.
- **Parallel tool execution**: all tool calls from a single model response execute concurrently via `Task.WhenAll`. Results are ordered by their original tool-call index before being added to the conversation. This means multiple `spawn_agent` calls (or a mix of `spawn_agent`, `read`, `write`, etc.) run simultaneously.
- **Tool loop extracted but not yet used for root**: the `Agent.RunAsync()` method contains the full tool-calling loop, but `AiChatPanel` still uses its own inline loop. This was intentional to minimize risk — the extracted loop is battle-tested by sub-agents first.
