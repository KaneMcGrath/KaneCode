## Phase 1 — Stabilize the Foundation

> Fix correctness issues and safety problems before adding new features.

- [x] **1.1 Fix thread safety in RoslynWorkspaceService**
- [x] **1.2 Fix sync-over-async in completion provider**
- [X] **1.3 Add ProjectReference support**
- [X] **1.4 Guard concurrent project loads**
- [X] **1.5 Per-tab undo stacks**
- [X] **1.6 Unsubscribe ThemeChanged on dispose**
- [X] **1.7 Code quality fixes**

---

## Phase 2 — Core IDE Features

> The features most expected in a code editor.

- [x] **2.1 Error list panel**
- [X] **2.2 Go to Definition (F12 / Ctrl+Click)**
- [X] **2.3 Find / Replace**
- [X] **2.4 Quick Info / Hover tooltips**
- [X] **2.5 Signature help**
- [X] **2.6 Multi-file re-analysis**


---

## Phase 3 — Real Features

> Features that round out the experience.

- [X] **3.1 Build / Run integration**
- [X] **3.2 Find References & Call Hierarchy**
- [X] **3.3 Code Actions & Lightbulb**
- [X] **3.4 Refactoring Suite**
- [X] **3.5 Go To Implementation / Derived Types**
- [X] **3.6 Generate Missing Members**
- [ ] 
## Phase 4 — File Creation, Templates & Explorer

> Improve the new-file workflow, add C# templates, and overhaul the explorer panel.

- [X] **4.1 Project-aware New File dialog**
- [X] **4.2 C# file template service**
- [X] **4.3 Explorer context menu — New File from template**
- [X] **4.4 Explorer panel overhaul — project/solution root node**
- [X] **4.5 Explorer — placeholder icons**
- [X] **4.6 New project dialogue**
- [X] **4.7 Rebuild New Project System with Microsoft.TemplateEngine**

## Phase 5 — Git Integration

> Bring source-control awareness into the IDE using LibGit2Sharp.

- [ ] **5.1 Add LibGit2Sharp & GitService skeleton**
  - Add the `LibGit2Sharp` NuGet package.
  - Create `Services/GitService.cs` that wraps `Repository` — detect/open repo from loaded project path.
- [ ] **5.2 Repo status query**
  - Expose a method that returns per-file status (added, modified, deleted, untracked, ignored).
  - Raise a `StatusChanged` event after any mutating operation.
- [ ] **5.3 Explorer file-status indicators**
  - Overlay or badge icons in the Explorer tree to reflect git status (M, A, ?, etc.).
  - Refresh badges when `StatusChanged` fires or when the user switches branches.
- [ ] **5.4 Git Changes panel — unstaged / staged file lists**
  - New `Controls/GitChangesPanel` (AvalonDock anchorable).
  - Show two groups: *Staged* and *Unstaged* with file paths and status glyphs.
  - Context menu: Stage / Unstage / Discard changes per file.
- [ ] **5.5 Stage / Unstage / Discard operations**
  - `GitService.StageAsync`, `UnstageAsync`, `DiscardAsync` wrapping LibGit2Sharp index commands.
  - Hook into the panel's context-menu and toolbar buttons.
- [ ] **5.6 Commit workflow**
  - Commit message text box + "Commit" button in the Changes panel.
  - Validate: non-empty message, at least one staged file.
  - Create the commit via `GitService.CommitAsync` using the configured author/e-mail.
- [ ] **5.7 Branch listing & switching**
  - Drop-down / list in the Changes panel (or status bar) showing local branches.
  - Switch branch via `GitService.CheckoutAsync`; reload workspace after switch.
- [ ] **5.8 Create & delete branches**
  - "New Branch" dialog (name + optional start point).
  - Delete branch via context menu with confirmation.
- [ ] **5.9 Diff viewer**
  - Side-by-side or inline diff view using AvalonEdit for a selected changed file.
  - Highlight added/removed/modified lines with gutter colours.
- [ ] **5.10 Git log (commit history)**
  - New `Controls/GitLogPanel` showing commit graph / flat list (hash, message, author, date).
  - Click a commit to view its diff summary.
- [ ] **5.11 Remote operations — Push / Pull / Fetch**
  - `GitService.PushAsync`, `PullAsync`, `FetchAsync` with progress reporting.
  - Toolbar buttons in the Changes panel; show progress in a status bar.
- [ ] **5.12 Gutter change markers in the editor**
  - Show coloured margin marks (green = added, blue = modified, red = deleted) in the active editor by diffing against HEAD.
- [ ] **5.13 Merge-conflict helpers**
  - Detect conflicted files after a pull/merge.
  - Mark conflict regions in the editor and offer "Accept Current / Incoming / Both" code-action–style buttons.

---

## Phase 6 — AI Agents

> Add LLM-powered coding assistance via a pluggable provider model.

- [ ] **6.1 AI provider abstraction & settings**
  - Define `IAiProvider` interface (`SendPromptAsync`, `StreamResponseAsync`, model list, display name).
  - Add provider selection + API-key storage in the Options window (encrypt keys with `ProtectedData`).
- [ ] **6.2 OpenAI / Azure OpenAI provider**
  - Implement `IAiProvider` using the `Azure.AI.OpenAI` (or `OpenAI`) SDK.
  - Support GPT-4o, GPT-4.1, and o3/o4-mini model families; allow the user to pick a model.
- [ ] **6.3 Llama.cpp (local) provider**
  - Implement `IAiProvider` that talks to a local Llama.cpp HTTP endpoint.
- [ ] **6.4 AI Chat panel**
  - New `Controls/AiChatPanel` (AvalonDock anchorable) with a message list and input box.
  - Render user / assistant messages with markdown support; stream tokens in real-time.
- [ ] **6.5 Context injection — current file & selection**
  - "Ask about selection" command that pre-fills the chat with the current file path, language, and selected text.
  - Include surrounding context (±50 lines) and diagnostics for the selection range.
- [ ] **6.6 Project-wide context builder**
  - Build a concise project summary (file tree, referenced packages, target framework) to include in system prompts.
  - Allow the user to attach additional files to the conversation.
- [ ] **6.7 Inline code generation / edit**
  - "Generate" command that inserts AI-suggested code at the caret position.
  - "Edit" command that replaces the selection with an AI-rewritten version; show a diff preview before applying.
- [ ] **6.8 AI-powered Explain & Fix Diagnostic**
  - Right-click a diagnostic → "Explain with AI" sends the error + surrounding code to the provider.
  - "Fix with AI" suggests a patch; show diff preview → Apply / Discard.
- [ ] **6.9 Conversation history & context management**
  - Persist chat sessions per project (JSON on disk).
  - Token-budget management: summarise older messages when approaching the model's context window.
- [ ] **6.10 Agent mode — multi-step task runner**
  - Agent loop: plan → act (edit files, run build) → observe (read diagnostics) → iterate.
  - Surface each step in the chat panel with expandable details.
  - Safety: require user confirmation before applying file edits or running commands.