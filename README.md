![alt text](https://github.com/KaneMcGrath/KaneCode/blob/master/Icons/Title.png?raw=true "KaneCode")

# KaneCode

KaneCode is a small, personal IDE project for experimenting with new ideas for developing C# software and using local LLMs.

![alt text](https://github.com/KaneMcGrath/KaneCode/blob/master/Icons/Capture.png?raw=true "Screenshot")

KaneCode is still in development and has many bugs and missing features.
But it is capable of developing c# programs, and currently almost all KaneCode development happens in KaneCode

---

# Features

## Code editing
- **AvalonEdit** editor with syntax highlighting and line numbers
- **Roslyn-powered completions**, signature help, quick info (hover tooltips), and diagnostics
- **Inline rename** UI for renaming symbols across files
- **Refactoring** (Extract Method, Rename, Generate Missing Members, etc.)
- **Go to Definition / Go to Implementation / Find References** navigation
- **Markdown preview** panel for markdown files
- **Spellchecking** in the AI prompt input field

## Solution & project management
- Solution and project loading (`.sln`, `.slnx`, `.csproj`)
- High-fidelity MSBuild/Roslyn workspace loading
- **New project** from built-in or custom templates (powered by `Microsoft.TemplateEngine`)
- **NuGet package manager** window — browse, search, install, and uninstall packages
- **Recent projects** tracking for quick re-opening

## Build & run
- Integrated **build** with output in a dedicated panel
- **Run** the built application with a **stop** button
- **Error list** panel with clickable diagnostics

## AI Agent

KaneCode's AI agent uses any OpenAI-compatible API provider, and uses a variety of tools to complete tasks.

### Available tools

| Category | Tools |
|---|---|
| **File I/O** | `read_file`, `write_file`, `edit_file`, `delete_file`, `rename_path`, `create_directory`, `delete_directory` |
| **Search** | `list_files`, `search_files` |
| **Build & test** | `build`, `run`, `run_test`, `get_diagnostics` |
| **Git** | `git_status`, `git_diff`, `git_log`, `git_commit`, `git_stage`, `git_unstage`, `git_discard`, `git_init`, `git_branches`, `git_create_branch`, `git_delete_branch`, `git_checkout`, `git_fetch`, `git_pull`, `git_push`, `git_conflicts`, `git_resolve_conflict` |
| **NuGet** | `nuget_search`, `nuget_info`, `nuget_install`, `nuget_uninstall`, `nuget_list_installed` |
| **Project** | `load_project`, `new_project` |
| **Presentation** | `presentation_new`, `presentation_add_slide` |
| **Utility** | `run_dotnet` |

### AI modes
- **Agent mode** (default) — full tool-call loop for autonomous code work
- **Chat mode** — plain conversation without tool access
- **Teacher mode** — tools for navigating and presenting the codebase
- **Application mode** — can load and create projects via tools
- **Custom mode** — user-configurable tool selection

### AI chat features
- **Thinking block** rendering (for models that output reasoning tokens)
- **Tool call** UI with expandable headers showing filename and argument details
- **Streaming** content preview with configurable token batching
- **Conversation** save/load with conversation store
- **Context window** builder — add files, folders, diagnostics, build output, and Git status as context
- **Model selection** and per-parameter configuration (temperature, max tokens, etc.)
- **Multiple OpenAI-compatible providers** (configure endpoint, API key, model name)
- **Malformed tool call** recovery and debugging panel for tool failures
- **Raw mode** to disable response formatting

## Integrated Git tools
- Full Git UI panels: **Changes**, **Log**, **Diff**
- **Status badges** showing branch name and pending changes
- **Stage / Unstage / Discard** from the Changes panel
- **Commit** without pre-staging
- **Branch** creation, deletion, and checkout
- **Push / Pull / Fetch**
- **Merge conflict** detection and resolution (accept ours, theirs, or both)
- **Git init** with automatic `.gitignore` creation

## Presentation system
- Create slide-based presentations directly from the AI agent
- Full-screen overlay for presenting

## UI & theming
- Fully themed with **AvalonDock** (dockable tool windows)
- Three built-in themes: **Dark**, **Light**, **Blue**
- Dockable panels: Project Explorer, Error List, Build Output, Git panels, AI Chat, AI Debug, Find References, Markdown Preview
- Panels are recoverable from the **View** menu


---

# Requirements

- **.NET 8 SDK** (or later)
