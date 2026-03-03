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
- [~] **6.3** AI Chat panel — AvalonDock anchorable, markdown rendering, streaming tokens
  - QA: View → AI Chat → panel appears in the bottom dock group
  - QA: With no provider configured → type a message → system message says "No AI provider configured"
  - QA: Configure a llama.cpp provider → relaunch → AI Chat shows provider name in header
  - QA: Send a message → tokens stream in progressively with markdown formatting (headings, code blocks, bold, bullet lists)
  - QA: Click "⏹ Stop" mid-stream → streaming halts, partial response preserved
  - QA: Click "Clear" → conversation history and messages cleared
  - QA: Switch between Dark and Light themes → chat colors update correctly
  - QA: Enter sends message, Shift+Enter inserts newline
- [ ] **6.4** Context injection — "Ask about selection" with file, selection, and diagnostics
- [ ] **6.5** Project-wide context builder — file tree, packages, TFM for system prompt
- [ ] **6.6** Inline code generation / edit — insert at caret or replace selection with diff preview
- [ ] **6.7** AI-powered Explain & Fix Diagnostic — right-click diagnostic integration
- [ ] **6.8** Conversation history & context management — persist per-project, token budget
- [ ] **6.9** Agent mode — plan/act/observe loop, surface steps in chat, require user confirmation
- [ ] **6.10** OpenAI / Azure OpenAI provider implementation (GPT-4o / 4.1 / o3-mini)


