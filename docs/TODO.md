# Kane Code — Development Tracker

> This file tracks implementation progress across all phases.
> See [architecture.md](architecture.md) for full design details and issue analysis.
>
> **AI agents**: 
> pick the next unchecked `[ ]` item in the lowest-numbered phase.
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

- [X] **3.1 Build / Run integration**
  - Shell out to `dotnet build` / `dotnet run` with output capture
  - Show build output in a panel
  - Files: new `Services/BuildService.cs`, `Controls/BuildOutputPanel.xaml`, `MainWindow.xaml`

- [X] **3.2 Find References & Call Hierarchy**
  - Use Roslyn `SymbolFinder.FindReferencesAsync` and call hierarchy helpers to show where a symbol is used.
  - Files: `Services/RoslynNavigationService.cs`, `Controls/ErrorListPanel.xaml`, `ViewModels/MainViewModel.cs`

- [X] **3.3 Code Actions & Lightbulb**
  - Surface Roslyn `CodeActionService`/`CodeFixService` results and let users apply fixes/refactorings from the editor.
  - Files: new `Services/RoslynCodeActionService.cs`, `Controls/CodeActionLightBulb.xaml`, `ViewModels/MainViewModel.cs`

- [ ] **3.4 Refactoring Suite**
  - Inline rename, extract method/property, move type, and other transformations via Roslyn's `Renamer`/`CodeRefactoringService`.
  - Files: new `Services/RoslynRefactoringService.cs`, `ViewModels/MainViewModel.cs`

- [ ] **3.5 Go To Implementation / Derived Types**
  - Add commands that use Roslyn `SymbolFinder.FindImplementationsAsync`/`SymbolFinder.FindDerivedClassesAsync` to jump to related symbols.
  - Files: `Services/RoslynNavigationService.cs`, `ViewModels/MainViewModel.cs`

- [ ] **3.6 Generate Missing Members**
  - Drive Roslyn code generation (e.g., add missing methods/properties from usage) using `SyntaxGenerator` and `CodeRefactoringService` helpers.
  - Files: `Services/RoslynCodeActionService.cs`, `ViewModels/MainViewModel.cs`

## Phase 4 — Robustness

> Make the editor reliable for real-world usage.

- [ ] **4.1 File watching for external changes**
  - Use `FileSystemWatcher` to detect changes from git, other editors, etc.
  - Prompt reload or auto-reload for non-dirty files
  - Files: new `Services/FileWatcherService.cs`, `ViewModels/MainViewModel.cs`

- [ ] **4.2 Settings / Preferences system**
  - Persist font family, font size, theme, tab size, word wrap, recent files
  - JSON or user-scoped settings file
  - Files: new `Services/SettingsService.cs`

- [ ] **4.3 Configurable keybindings**
  - Keybinding map with user overrides
  - Files: new `Services/KeybindingService.cs`

- [ ] **4.4 Code formatting**
  - On-demand formatting via `Roslyn.Formatter`
  - Optional format-on-save
  - Files: `ViewModels/MainViewModel.cs`

- [ ] **4.5 Rename / Refactoring support**
  - Inline rename via Roslyn's `Renamer`
  - Extract method, etc.
  - Ref: architecture.md §5.4 #24
  - Files: new `Services/RoslynRefactoringService.cs`

- [ ] **4.6 Integrated terminal**
  - Embedded terminal panel
  - Files: new `Controls/TerminalPanel.xaml`

- [ ] **4.7 Git integration**
  - Show changed files, diff view, basic commit/push
  - Files: new `Services/GitService.cs`

- [ ] **4.8 Encoding detection and preservation**
  - Detect BOM / encoding on read
  - Preserve original encoding on save (don't silently convert to UTF-8)
  - Ref: architecture.md §5.2 #17
  - Files: `Services/EditorService.cs`

- [ ] **4.9 Incremental classification (visible lines only)**
  - `UpdateClassificationsAsync` currently classifies the entire file
  - Restrict to visible `VisualLines` range for large files
  - Ref: architecture.md §5.3 #18
  - Files: `Services/RoslynClassificationColorizer.cs`

- [ ] **4.10 Classification lookup optimization**
  - `ColorizeLine` iterates all spans linearly — O(n) per line
  - Sort spans and use binary search for O(log n)
  - Ref: architecture.md §5.3 #22
  - Files: `Services/RoslynClassificationColorizer.cs`

- [ ] **4.11 Structured logging**
  - Replace `Debug.WriteLine` and silent catches with proper logging
  - Add a log/output panel to the UI
  - Files: new `Services/LoggingService.cs`, `MainWindow.xaml`

- [ ] **4.12 Solution explorer tree (logical view)**
  - Show project nodes, virtual folders, linked files instead of raw filesystem
  - Reflect the `.csproj` / `.sln` structure
  - Ref: architecture.md §5.2 #15
  - Files: `Services/EditorService.cs`, `Models/ProjectItem.cs`, `ViewModels/MainViewModel.cs`

- [ ] **4.13 Improve NuGet reference resolution**
  - Handle Central Package Management (`Directory.Packages.props`)
  - Resolve transitive dependencies via `packages.lock.json` or `project.assets.json`
  - Ref: architecture.md §5.1 #3
  - Files: `Services/MSBuildProjectLoader.cs`

- [ ] **4.14 Tab content cache eviction**
  - `_tabContentCache` grows unbounded — evict on tab close
  - Ref: architecture.md §5.2 #16
  - Files: `ViewModels/MainViewModel.cs`

---


