# Kane Code — Development Tracker

> This file tracks implementation progress across all phases.
>
> **AI agents**: 
> pick the next unchecked `[ ]` item.
> Mark `[~]` when starting work, `[O]` when complete and awaiting review, 
> Under the item give a few brief runtime testing items, for a QA team to verify.
> The QA team will mark `[x]` when it is tested.


## Status Key

- `[ ]` — Not started
- `[~]` — In progress
- `[O]` — Awaiting review
- `[x]` — Complete

---

## Task List

### Phase 6 — AI Agents

- [x] **6.1** `IAiProvider` abstraction + provider/API-key settings in Options (encrypted storage)
- [x] **6.2** Llama.cpp (local) provider implementation, talks to a local Llama.cpp HTTP endpoint.
- [x] **6.3** AI Chat panel — AvalonDock anchorable, markdown rendering, streaming tokens
- [x] **6.4** Reference addition — Add references to files, classes, or methods in the codebase
- [x] **6.5** Context injection — "Ask about selection" with file, selection, and diagnostics
- [x] **6.6** Project-wide context builder — file tree, packages, TFM for system prompt
- [x] **6.7** Conversation history & context management — persist per-project, token budget

### Phase 7 — Tool Calling Infrastructure

- [O] **7.1** Tool definition framework — `IAgentTool` interface, tool registry, JSON schema generation
    - `IAgentTool` with `Name`, `Description`, `ParametersSchema` (JSON Schema), `ExecuteAsync(JsonElement args)`
    - `AgentToolRegistry` to register/discover tools and serialize the tool list for the API
    - Shared `ToolCallResult` record with `Success`, `Output`, `Error` fields
  - QA: Verify `IAgentTool` interface compiles and exposes Name, Description, ParametersSchema, ExecuteAsync
  - QA: Verify `ToolCallResult.Ok("output")` and `ToolCallResult.Fail("error")` factory methods work
  - QA: Register tools into `AgentToolRegistry`, call `Get(name)` → returns correct tool
  - QA: Call `SerializeToolDefinitions()` → returns valid JSON array with type/function/name/description/parameters
- [O] **7.2** Tool-call message flow — extend `IAiProvider` and `AiChatMessage` for tool calls
    - Add `AiChatRole.Tool` and `ToolCallId` to `AiChatMessage`
    - Extend `AiStreamToken` with `AiStreamTokenType.ToolCall` carrying function name + arguments JSON
    - Update `LlamaCppProvider` to parse `tool_calls` from SSE deltas and send `tools` in the request body
  - QA: Verify `AiChatRole.Tool` exists and `AiChatMessage` has `ToolCallId` and `ToolCalls` properties
  - QA: Verify `AiStreamTokenType.ToolCall` and `AiStreamToolCall` record compile
  - QA: Send a chat with tools defined → request body includes `tools` array
  - QA: Model responds with tool_calls → `AiStreamToolCall` tokens are emitted after stream ends
  - QA: Sending tool result messages back includes `tool_call_id` and `role: "tool"` in the request
- [O] **7.3** Tool-call loop in `AiChatPanel` — detect tool calls, execute, feed results back
    - After streaming completes, check if the response contains tool calls instead of content
    - Execute each tool call via the registry, collect results
    - Append tool results as `AiChatRole.Tool` messages and re-send to the model
    - Loop until the model returns a content response (with a max-iteration safety limit)
  - QA: Chat with no tools registered → normal assistant flow, no tool-call loop
  - QA: Chat with tools registered and model invokes a tool → tool executes, result appended, model re-called
  - QA: Model chains multiple tool calls → each is executed and result shown before next model call
  - QA: Tool-call loop reaches 20 iterations → stops and shows safety-limit warning
  - QA: User cancels during tool execution → streaming stops cleanly
- [O] **7.4** Tool-call UI rendering — show tool invocations and results in chat
    - Render each tool call as a collapsible block (like thinking) showing tool name, arguments, and result
    - Show a spinner/status indicator while a tool is executing
    - Color-code success (green) vs error (red) results
  - QA: Tool invocation shows collapsible block with ⏳ spinner and tool name during execution
  - QA: On success → header changes to ✅, result text is green-tinted
  - QA: On error → header changes to ❌, error text is red-tinted
  - QA: Expanding a tool-call block shows formatted arguments and result text
  - QA: Dark and light themes both render tool-call blocks with appropriate colors

### Phase 8 — Built-in Agent Tools

- [X] **8.1** `ReadFileTool` — read a file's contents by path
    - Parameters: `filePath` (string)
    - Returns the file text, or error if not found / too large
    - Respects a max file size limit (e.g. 100 KB) to protect context budget
- [X] **8.2** `WriteFileTool` — create or overwrite a file by path
    - Parameters: `filePath` (string), `content` (string)
    - Writes the file to disk and opens it in the editor
    - Reports success or IO error
- [O] **8.3** `EditFileTool` — search-and-replace edit within a file
    - Parameters: `filePath` (string), `oldText` (string), `newText` (string)
    - Applies a single find-and-replace, fails if `oldText` is not found or matches multiple locations
    - Returns the updated region or error
  - QA: Agent calls edit_file with valid oldText matching exactly once → file is updated, success returned with line number
  - QA: oldText not present in file → fails with "not found" error and hint about whitespace
  - QA: oldText appears more than once → fails with "N locations" error and hint to add more context
  - QA: Empty oldText → fails with explicit validation error
  - QA: File not found → returns "File not found" error
- [O] **8.4** `ListFilesTool` — list files in a directory or the project tree
    - Parameters: `directory` (string, optional — defaults to project root)
    - Returns a flat list of relative file paths
  - QA: Call `list_files` with no `directory` argument → returns flat list of relative paths from project root, excluding bin/obj/.git
  - QA: Call `list_files` with a valid relative subdirectory (e.g. `"Services"`) → returns only files under that subdirectory
  - QA: Call `list_files` with an absolute path to an existing directory → returns files relative to that directory
  - QA: Call `list_files` with a non-existent directory → returns "Directory not found" error
  - QA: Open a project with more than 2000 files → result is truncated at 2000 entries with a trailing `... (truncated at 2000 files)` note
  - QA: Directory contains `bin`, `obj`, `.git`, `node_modules` subdirectories → those are excluded from the output
- [ ] **8.5** `SearchFilesTool` — search file contents with text or regex
    - Parameters: `query` (string), `directory` (string, optional), `isRegex` (bool, optional)
    - Returns matching file paths with line numbers and snippets
- [ ] **8.6** `RunBuildTool` — trigger a build and return diagnostics
    - Invokes `BuildService.BuildAsync`, returns build output and error list
    - Streams build output lines back as partial results if possible
- [ ] **8.7** `GetDiagnosticsTool` — get Roslyn diagnostics for a file
    - Parameters: `filePath` (string)
    - Returns the list of errors/warnings with line numbers and messages
    - Uses existing `RoslynWorkspaceService.GetDiagnosticsAsync`
- [ ] **8.8** `RunCommandTool` — execute a shell command (with user confirmation)
    - Parameters: `command` (string), `workingDirectory` (string, optional)
    - **Requires explicit user approval** before execution (security gate)
    - Returns stdout/stderr with a max output size limit
    - Timeout after configurable duration (default 30s)
- [ ] **8.9** `GoToDefinitionTool` — find the definition of a symbol
    - Parameters: `filePath` (string), `symbolName` (string) or `line`/`column`
    - Uses `RoslynNavigationService.FindDefinitionAsync`
    - Returns the file path, line, and surrounding code context
- [ ] **8.10** `FindReferencesTool` — find all references to a symbol
    - Parameters: `filePath` (string), `symbolName` (string) or `line`/`column`
    - Uses `RoslynNavigationService.FindReferencesAsync`
    - Returns list of locations with file, line, and snippet
- [ ] **8.11** `GitStatusTool` — get current git status, branch, and changed files
    - Uses existing `GitService` to return branch, staged/unstaged changes
- [ ] **8.12** `GitDiffTool` — get the diff for a file or the entire working tree
    - Parameters: `filePath` (string, optional)
    - Returns unified diff output

### Phase 9 — Agent Mode

- [ ] **9.1** Agent mode toggle — UI switch in chat header to enable agent/tool mode
    - Toggle button in the AI Chat header bar
    - When enabled, the system prompt includes tool definitions and agent instructions
    - When disabled, tools are not sent and the model behaves as a plain chat assistant
- [ ] **9.2** System prompt engineering — agent persona with tool-use instructions
    - Structured system prompt: role, capabilities, available tools, coding conventions
    - Include project context (from 6.6): file tree, TFM, packages, language version
    - Instructions for plan-then-act workflow, when to use which tools
- [ ] **9.3** Plan/act/observe loop — structured agent reasoning
    - Agent outputs a plan (numbered steps) before acting
    - Each step is surfaced in the chat as a collapsible plan block
    - After each tool call, the agent observes the result and decides the next action
    - Plan can be revised mid-execution based on observations
- [ ] **9.4** User confirmation gates — require approval for destructive actions
    - File writes, command execution, and git operations prompt the user with a diff/preview
    - Inline approve/reject buttons in the chat for each pending action
    - Configurable trust levels: "ask always", "auto-approve reads", "auto-approve all"
- [ ] **9.5** Multi-step task execution — end-to-end coding workflows
    - Agent can chain: read file → understand → edit → build → check diagnostics → fix → done
    - Max iteration limit (configurable, default 20) to prevent runaway loops
    - Automatic cancellation if the agent appears stuck (repeated identical tool calls)
- [ ] **9.6** Inline diff preview — show proposed edits as diffs before applying
    - When the agent proposes a file edit, render a unified diff in the chat
    - Accept/Reject buttons apply or discard the change
    - Side-by-side or inline diff view using existing `GitDiffPanel` patterns
- [ ] **9.7** Agent output panel — structured progress view
    - Separate collapsible section showing: current plan, completed steps, pending steps
    - Real-time updates as the agent works through its plan
    - Elapsed time and tool call count displayed




