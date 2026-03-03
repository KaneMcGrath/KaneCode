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
- [~] **6.4** Reference addition — Add references to files, classes, or methods in the codebase
	- bar above the chat input to "Add reference" — opens a dialog to select a file, class, or method, and adds it to the current conversation context.
	- support '@' mentions in the chat input to reference code elements directly in the conversation with an autocomplete dropdown.
  - QA: Click "📎 Add" → picker dialog opens with searchable file list → select a file → tag appears in reference bar
  - QA: Click ✕ on a reference tag → tag is removed
  - QA: Send a message with references attached → AI response acknowledges the file content
  - QA: Type '@' in the input box → autocomplete popup appears with file names → Arrow keys + Enter to select → file added as reference
  - QA: Type '@partial' → popup filters to matching files → Escape dismisses popup
  - QA: Double-click a file in the @ popup → file added as reference



