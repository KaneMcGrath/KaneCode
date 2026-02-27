# Kane Code — Development Tracker

> This file tracks implementation progress across all phases.
> See [architecture.md](architecture.md) for full design details and issue analysis.
>
> **AI agents**: pick the next unchecked `[ ]` item in the lowest-numbered phase.
> Mark `[~]` when starting work, `[O]` when complete and awaiting review, 
> Under the item give a few brief runtime testing items, for a QA team to verify.
> The QA team will mark `[x]` when it is tested.


## Status Key

- `[ ]` — Not started
- `[~]` — In progress
- `[O]` — Awaiting review
- `[x]` — Complete

---

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

- [~] **3.1 Build / Run integration**
  - Shell out to `dotnet build` / `dotnet run` with output capture
  - Show build output in a panel
  - Files: new `Services/BuildService.cs`, `Controls/BuildOutputPanel.xaml`, `MainWindow.xaml`
  - **QA verification items:**
    - Load a .csproj or .sln, press Ctrl+Shift+B — verify `dotnet build` output appears in the Build Output panel
    - Press F5 — verify `dotnet run` output streams in real-time
    - Click Cancel while a build/run is in progress — verify the process stops
    - Verify Build menu items are enabled only when a project/solution is loaded
    - Verify Build Output panel is visible as a tab alongside Error List
    - Switch themes — verify Build Output panel colors update correctly

- [ ] **3.2 Settings / Preferences system**
  - Persist font family, font size, theme, tab size, word wrap, recent files
  - JSON or user-scoped settings file
  - Files: new `Services/SettingsService.cs`

- [ ] **3.3 Configurable keybindings**
  - Keybinding map with user overrides
  - Files: new `Services/KeybindingService.cs`

- [ ] **3.4 Code formatting**
  - On-demand formatting via `Roslyn.Formatter`
  - Optional format-on-save
  - Files: `ViewModels/MainViewModel.cs`

- [ ] **3.5 Rename / Refactoring support**
  - Inline rename via Roslyn's `Renamer`
  - Extract method, etc.
  - Ref: architecture.md §5.4 #24
  - Files: new `Services/RoslynRefactoringService.cs`

- [ ] **3.6 Integrated terminal**
  - Embedded terminal panel
  - Files: new `Controls/TerminalPanel.xaml`

- [ ] **3.7 Git integration**
  - Show changed files, diff view, basic commit/push
  - Files: new `Services/GitService.cs`

## Phase 4 — Robustness

> Make the editor reliable for real-world usage.

- [ ] **4.1 File watching for external changes**
  - Use `FileSystemWatcher` to detect changes from git, other editors, etc.
  - Prompt reload or auto-reload for non-dirty files
  - Files: new `Services/FileWatcherService.cs`, `ViewModels/MainViewModel.cs`

- [ ] **4.2 Encoding detection and preservation**
  - Detect BOM / encoding on read
  - Preserve original encoding on save (don't silently convert to UTF-8)
  - Ref: architecture.md §5.2 #17
  - Files: `Services/EditorService.cs`

- [ ] **4.3 Incremental classification (visible lines only)**
  - `UpdateClassificationsAsync` currently classifies the entire file
  - Restrict to visible `VisualLines` range for large files
  - Ref: architecture.md §5.3 #18
  - Files: `Services/RoslynClassificationColorizer.cs`

- [ ] **4.4 Classification lookup optimization**
  - `ColorizeLine` iterates all spans linearly — O(n) per line
  - Sort spans and use binary search for O(log n)
  - Ref: architecture.md §5.3 #22
  - Files: `Services/RoslynClassificationColorizer.cs`

- [ ] **4.5 Structured logging**
  - Replace `Debug.WriteLine` and silent catches with proper logging
  - Add a log/output panel to the UI
  - Files: new `Services/LoggingService.cs`, `MainWindow.xaml`

- [ ] **4.6 Solution explorer tree (logical view)**
  - Show project nodes, virtual folders, linked files instead of raw filesystem
  - Reflect the `.csproj` / `.sln` structure
  - Ref: architecture.md §5.2 #15
  - Files: `Services/EditorService.cs`, `Models/ProjectItem.cs`, `ViewModels/MainViewModel.cs`

- [ ] **4.7 Improve NuGet reference resolution**
  - Handle Central Package Management (`Directory.Packages.props`)
  - Resolve transitive dependencies via `packages.lock.json` or `project.assets.json`
  - Ref: architecture.md §5.1 #3
  - Files: `Services/MSBuildProjectLoader.cs`

- [ ] **4.8 Tab content cache eviction**
  - `_tabContentCache` grows unbounded — evict on tab close
  - Ref: architecture.md §5.2 #16
  - Files: `ViewModels/MainViewModel.cs`

---


