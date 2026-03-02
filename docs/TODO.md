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

### Phase 5 — Git Integration

- [X] **5.1** Add `LibGit2Sharp` NuGet package; create `Services/GitService.cs` skeleton (detect & open repo)
- [X] **5.2** Implement repo status query — per-file status + `StatusChanged` event
- [X] **5.3** Explorer file-status indicators (M/A/?/D badges on tree nodes)
- [X] **5.4** Git Changes panel — unstaged / staged file lists (AvalonDock anchorable)
- [X] **5.5** Stage / Unstage / Discard operations in `GitService` + panel context menu
- [X] **5.6** Commit workflow — message box, validation, `GitService.CommitAsync`
- [X] **5.7** Branch listing & switching — drop-down + `GitService.CheckoutAsync`
- [O] **5.8** Create & delete branches — dialog + confirmation
  - QA: Use `Git > Create Branch...`, enter a new valid name, and verify branch appears in the branch drop-down.
  - QA: Try creating a branch with an existing name and verify a warning is shown.
  - QA: Use `Git > Delete Branch...`, confirm deletion, and verify the branch is removed from the drop-down.
  - QA: Try deleting the currently checked-out branch and verify a warning prevents deletion.
- [O] **5.9** Diff viewer — side-by-side AvalonEdit diff with colour-coded lines
  - QA: Right-click a changed file in Git Changes and choose `View Diff`; verify `Git Diff` panel opens with side-by-side text.
  - QA: Verify modified lines are highlighted on both sides, added-only lines are highlighted on the working-tree side, and deleted-only lines on the HEAD side.
  - QA: Open diff for a newly added file and verify left (HEAD) pane is empty while right pane shows file content.
  - QA: Open diff for a deleted file and verify left pane shows previous content while right pane is empty.
- [ ] **5.10** Git log panel — commit history list (hash, message, author, date)
- [ ] **5.11** Remote operations — Push / Pull / Fetch with progress reporting
- [ ] **5.12** Gutter change markers in the editor (green/blue/red margin vs HEAD)
- [ ] **5.13** Merge-conflict helpers — detect conflicts, offer Accept Current/Incoming/Both

### Phase 6 — AI Agents

- [ ] **6.1** `IAiProvider` abstraction + provider/API-key settings in Options (encrypted storage)
- [ ] **6.2** OpenAI / Azure OpenAI provider implementation (GPT-4o / 4.1 / o3-mini)
- [ ] **6.3** Llama.cpp (local) provider implementation, talks to a local Llama.cpp HTTP endpoint.
- [ ] **6.4** AI Chat panel — AvalonDock anchorable, markdown rendering, streaming tokens
- [ ] **6.5** Context injection — "Ask about selection" with file, selection, and diagnostics
- [ ] **6.6** Project-wide context builder — file tree, packages, TFM for system prompt
- [ ] **6.7** Inline code generation / edit — insert at caret or replace selection with diff preview
- [ ] **6.8** AI-powered Explain & Fix Diagnostic — right-click diagnostic integration
- [ ] **6.9** Conversation history & context management — persist per-project, token budget
- [ ] **6.10** Agent mode — plan/act/observe loop, surface steps in chat, require user confirmation
- [ ] **6.11** TODO.md–driven autonomous agent — pick `[ ]` items, execute, mark `[~]`/`[O]`


