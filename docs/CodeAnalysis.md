# Code Analysis Report

## Executive Summary

The current code analysis system is a custom Roslyn-based layer built on top of `AvalonEdit`.
It already provides real semantic value for C# files: compiler diagnostics, semantic classification, completion, hover info, signature help, navigation, find references, rename, and some code actions/refactorings.

The main reason it does not feel like a full C# IDE is not that Roslyn is absent. Roslyn is present, but the surrounding workspace and editor integration are much thinner than what a mature IDE uses.

In short:

- The editor intelligence is real and Roslyn-powered.
- The workspace model is custom and only partially faithful to an actual MSBuild design-time build.
- Several features are position-based or file-based approximations rather than full IDE workflows.
- Multi-file changes and project fidelity are the biggest current weaknesses.

## Files Reviewed

Core analysis and workspace files:

- `Services/RoslynWorkspaceService.cs`
- `Services/MSBuildProjectLoader.cs`
- `Services/SharedFrameworkResolver.cs`
- `Services/RoslynCompletionProvider.cs`
- `Services/RoslynQuickInfoService.cs`
- `Services/RoslynSignatureHelpService.cs`
- `Services/RoslynNavigationService.cs`
- `Services/RoslynCodeActionService.cs`
- `Services/RoslynRefactoringService.cs`
- `Services/RoslynClassificationColorizer.cs`
- `Services/RoslynDiagnosticRenderer.cs`

Editor and UI integration:

- `ViewModels/MainViewModel.cs`
- `MainWindow.xaml.cs`
- `MainWindow.xaml`
- `Controls/CodeActionLightBulb.cs`
- `Controls/CodeActionLightBulb.xaml`
- `Controls/ErrorListPanel.xaml.cs`
- `Controls/ErrorListPanel.xaml`
- `Controls/FindReferencesPanel.cs`
- `Controls/FindReferencesPanel.xaml`

Supporting models and analysis-adjacent tooling:

- `Models/DiagnosticItem.cs`
- `Models/CodeActionItem.cs`
- `Models/ReferenceItem.cs`
- `Services/Ai/Tools/GetDiagnosticsTool.cs`

## How the Current Code Analysis Works

## 1. Workspace architecture

The system centers on `RoslynWorkspaceService`, which owns a single in-memory `AdhocWorkspace`.

### What it does

- Creates a default C# project on startup.
- Tracks open documents by file path.
- Supports adding MSBuild-loaded projects into the workspace.
- Updates document text as the user edits.
- Returns Roslyn `Document` instances for downstream features.
- Computes diagnostics for a file from its semantic model.
- Computes dependent open documents so diagnostics can be refreshed across project references.

### Important design implication

This is not `MSBuildWorkspace`, and it is not a language server.
It is a hand-managed Roslyn workspace that tries to approximate enough project state to enable useful editor features.

That is the root of both its strengths and its limitations.

## 2. Project and solution loading

Project loading is handled by `MSBuildProjectLoader`.

### Load flow

When a `.csproj` or `.sln` is opened:

1. `MSBuildLocator` is registered.
2. The solution or project file is parsed.
3. Each C# project is evaluated through `Microsoft.Build.Evaluation.Project`.
4. Compilation settings are extracted manually:
   - `TargetFramework`
   - `OutputType`
   - `LangVersion`
   - `Nullable`
   - `AllowUnsafeBlocks`
   - `DefineConstants`
5. Source files are collected from `Compile` items.
6. Generated files are partially added from `obj`, including `*.g.cs`, `*.g.i.cs`, and `*.GlobalUsings.g.cs`.
7. Package references are resolved manually by scanning the NuGet cache.
8. Framework references are resolved manually by scanning installed shared frameworks.
9. Projects are added to the Roslyn workspace.
10. `ProjectReference` relationships are wired up afterward.

### What is good here

- It supports both solution and project loading.
- It loads multiple projects into one Roslyn solution.
- It includes transitive project references.
- It attempts to include generated code and implicit usings.
- It is enough to make cross-project navigation and references work in many normal cases.

### What is not IDE-grade here

This loader is doing a simplified recreation of a design-time build, not an actual design-time build.
That means fidelity gaps are expected.

Examples:

- It reads `TargetFramework`, not `TargetFrameworks`.
- It does not model full design-time MSBuild behavior.
- It does not load analyzer assemblies.
- It does not load `AdditionalFiles`.
- It does not appear to apply `.editorconfig` analyzer settings into the workspace.
- It does not model source generators as a first-class pipeline.
- It resolves package assemblies from `lib` folders heuristically instead of using Roslyn/MSBuild's actual resolved compile references.
- It falls back to `LanguageVersion.Latest` when `LangVersion` is absent, which can differ from the real project language version.
- It ignores non-C# projects in a solution.

This is the single biggest reason the analysis can drift from what a proper C# IDE would show.

## 3. Editor integration flow

`MainViewModel` is the main orchestration layer.

### On tab activation

When a file is opened or activated:

- The AvalonEdit document is swapped in.
- Basic syntax highlighting is configured through `EditorService`.
- If the file is C#:
  - the file is registered with the Roslyn workspace,
  - semantic classification is attached,
  - Roslyn analysis is scheduled.
- If the file is not C#:
  - Roslyn overlays are cleared.

### On text change

When the editor text changes:

- the tab is marked dirty,
- Git gutter markers are refreshed,
- Roslyn analysis is scheduled using a `500ms` debounce.

### Analysis pass

`RunRoslynAnalysisAsync` performs the main background pass:

1. waits for the debounce delay,
2. pushes current editor text into Roslyn,
3. recomputes semantic classifications for the full file,
4. gathers diagnostics for the active file and dependent open files,
5. updates squiggles,
6. updates the error list,
7. updates the status bar summary.

This gives the editor a basic live-analysis loop.

## Current Features

## 1. Semantic syntax coloring

Implemented in `RoslynClassificationColorizer`.

### How it works

- Uses Roslyn `Classifier.GetClassifiedSpansAsync`.
- Classifies the whole document.
- Maps Roslyn classification types to theme brushes.
- Applies colors line-by-line through an AvalonEdit transformer.

### What it supports

- classes
- structs
- records
- interfaces
- enums and enum members
- delegates
- type parameters
- methods and extension methods
- properties
- events
- fields and constants
- parameters
- locals
- namespaces
- keywords and control keywords
- string escape characters
- overloaded operators
- labels

### Assessment

This is one of the stronger parts of the current system.
It goes beyond regex coloring and gives genuine semantic coloring.

### Limitation

It reclassifies the entire file after edits.
That is simple, but it is not especially scalable for large files.

## 2. Diagnostics and squiggles

Implemented through `RoslynWorkspaceService`, `MainViewModel`, `RoslynDiagnosticRenderer`, `DiagnosticItem`, and `ErrorListPanel`.

### How it works

- Diagnostics come from `semanticModel.GetDiagnostics(...)`.
- Hidden diagnostics are ignored.
- The active file receives squiggle underlines.
- Diagnostic entries are converted into `DiagnosticItem` rows.
- The error list panel shows severity, code, message, file, line, and column.
- Double-clicking an item navigates to source.

### What is good

- Live errors and warnings exist.
- There is an error list panel.
- Diagnostics are tied to exact spans.
- Cross-project dependency reanalysis exists for open documents.

### Limitations

- Diagnostics are driven from the semantic model only, not a full analyzer pipeline.
- The system does not appear to load analyzer references from packages or SDK analyzers.
- Only open dependent documents are refreshed, not the full solution.
- The squiggle renderer only draws for the active document.
- Project-level and non-source diagnostics are not surfaced in a rich way.
- No code-fix severity icons or categorized grouping exist.
- Error list icon rendering currently uses placeholder glyphs in `DiagnosticItem.SeverityIcon` rather than polished visuals.

### Practical outcome

You get useful compiler-style feedback, but not the full analyzer-rich experience of Visual Studio or Rider.

## 3. Code completion

Implemented in `RoslynCompletionProvider` and triggered from `MainViewModel`.

### How it works

- Before requesting completions, the current editor text is pushed into Roslyn.
- Roslyn `CompletionService` is queried at the caret position.
- Items are wrapped into AvalonEdit completion entries.
- Roslyn's completion span start is used to improve filtering.
- Descriptions are prefetched asynchronously.
- Completion changes are resolved from Roslyn when the user commits an item.

### Trigger behavior

Auto-trigger occurs only when the typed character is:

- `.`
- a letter
- `_`

Manual completion is also available through the command path.

### What is good

- Completion is true Roslyn completion, not static word completion.
- Replacement text comes from Roslyn change computation.
- Filtering behavior is reasonably thought through.
- The popup is theme-aware.

### Limitations

- Trigger rules are much simpler than a full IDE.
- There is no broad commit-character policy beyond AvalonEdit behavior.
- There is no item iconography for symbol kinds.
- There is no richer completion ranking or UX tuning.
- There is no import-completion workflow comparable to a full IDE.
- There is no indication of snippet completions or advanced provider kinds.
- The system is C#-only.

### Assessment

Completion is real, but the UX layer around it is still minimal.

## 4. Quick Info / hover tooltips

Implemented in `RoslynQuickInfoService` and `MainWindow` hover handling.

### How it works

- Mouse hover computes the editor offset.
- Current text is synced into Roslyn.
- `QuickInfoService` is queried.
- Roslyn sections are flattened into plain text.
- A simple popup is shown.

### What is good

- Hover info is semantic.
- It should work for types, methods, and diagnostics that Roslyn can describe.

### Limitations

- Output is flattened to plain text, so rich Roslyn formatting is lost.
- The popup is custom and basic.
- There is no pinned tooltip, no richer navigation, and no embedded links/actions.
- It depends on mouse hover only.

## 5. Signature help

Implemented in `RoslynSignatureHelpService` and shown via AvalonEdit `OverloadInsightWindow`.

### How it works

- Triggered when typing `(` or `,`.
- Closes when typing `)`.
- Uses syntax tree + semantic model directly.
- Resolves invocation or object creation symbols.
- Collects candidate overloads.
- Builds overload text plus XML documentation summaries and parameter docs.

### What is good

- It supports both method calls and object creation.
- It tracks the active parameter by comma counting.
- It includes XML documentation content.

### Limitations

- It is a custom implementation, not Roslyn's richer signature-help pipeline.
- Active parameter updates are trigger-based, not generally caret-aware.
- Backspacing, caret moves, or other edit patterns are not handled like a full IDE.
- The content formatting is plain text oriented.
- More advanced cases may be less reliable than Roslyn's native signature help infrastructure.

## 6. Navigation

Implemented in `RoslynNavigationService` and surfaced by `MainViewModel`.

### Supported features

- Go to Definition
- Find References
- Go to Implementation
- Go to Derived Types

### How it works

- Uses `SymbolFinder.FindSymbolAtPositionAsync`.
- Handles aliases by resolving to target symbols.
- Uses Roslyn symbol search APIs across the current solution.
- Converts results into `ReferenceItem` entries.
- Reuses the Find References panel for multi-result navigation.
- Automatically navigates directly when only one implementation or derived type is found.

### What is good

- Cross-project symbol navigation exists.
- Results include definition and usage sites.
- The references panel is functional and navigable.

### Limitations

- It depends on the accuracy of the custom workspace.
- Results are only as good as the currently loaded C# projects and references.
- There is no grouped hierarchy by definition/reference kind/project.
- There is no peek definition.
- There is no call hierarchy.
- There is no symbol search or navigation search index.

## 7. Code actions and refactorings

Implemented in `RoslynCodeActionService`, `RoslynRefactoringService`, `CodeActionLightBulb`, and `MainViewModel`.

### Supported capabilities

- show code actions at caret
- generate missing members
- extract method
- rename symbol
- apply code actions across multiple files in memory

### How code actions work

- The current file text is synced into Roslyn.
- Diagnostics on the current line are collected.
- Code fix providers are discovered by reflection from Roslyn MEF assemblies.
- Refactoring providers are also discovered by reflection.
- Actions are flattened to leaf actions and shown in a popup.
- Applying a code action returns changed document text from the changed Roslyn solution.

### What is good

- This is an ambitious feature set for a custom editor.
- Code fixes and refactorings both exist.
- Rename is solution-wide in Roslyn terms.
- Extract method support exists.
- Generate-missing-members support exists.

### Major limitations

#### A. Code fixes are line-scoped

`CollectCodeFixesAsync` only checks diagnostics overlapping the current line.
That is simpler than a full IDE and can miss valid fixes that are not naturally represented by the line-only filter.

#### B. Refactorings are caret-scoped

General refactorings are requested with a zero-length span at the caret.
Many refactorings in real IDEs depend on a selected span or richer context.

`ExtractMethodAsync` works around this by using the midpoint of the current selection and then searching for an action titled like extract method. That can work, but it is still a heuristic.

#### C. Provider failures are swallowed

Several provider failures are silently ignored.
That keeps the UI resilient, but it hides why an action did not appear.

#### D. Provider discovery is broad and reflection-based

The system scans default MEF assemblies and instantiates discovered providers directly.
That is clever, but not as robust or explicit as a mature language-service composition model.

#### E. Multi-file apply behavior is incomplete

This is one of the most important current issues.

`ApplyMultiFileChanges`:

- updates the active editor text,
- updates text in already-open tabs,
- updates Roslyn workspace text for all changed files,
- but does **not** write unopened changed files back to disk.

That means a rename or code action that changes multiple files may not actually persist all changes to the filesystem.

Also:

- non-active open tabs are updated in memory,
- but they are not explicitly marked dirty,
- so the save model for those files is unclear and potentially incorrect.

This is a serious gap between "Roslyn computed the right edits" and "the IDE actually applied them correctly".

## 8. Rename support

Implemented in `RoslynRefactoringService`.

### How it works

- Gets the symbol at the caret.
- Checks if the symbol is renamable and source-backed.
- Uses `Renamer.RenameSymbolAsync` across the current solution.
- Returns a file-to-text map.

### What is good

- This is true Roslyn rename, not search/replace.
- It respects symbols rather than text.

### Limitation

The correctness of the edit application path is weaker than the correctness of the rename engine itself because of the multi-file persistence issue above.

## 9. Analysis exposure to AI tools

`Services/Ai/Tools/GetDiagnosticsTool.cs` exposes Roslyn diagnostics to the internal agent tooling.

### What it does

- Accepts a file path.
- Resolves relative paths.
- Syncs file content from disk into the Roslyn workspace.
- Returns diagnostics as structured text.

### Assessment

This is useful and sensible.
It helps keep AI-assisted edits grounded in the same analysis engine the editor uses.

## Feature Summary

The current editor already has these C# analysis features:

- semantic coloring
- compiler-style diagnostics
- squiggles
- error list
- code completion
- quick info hover
- signature help
- go to definition
- find references
- go to implementation
- go to derived types
- code actions popup
- rename symbol
- extract method
- generate missing members

That is a substantial baseline.
This is not a "no code analysis" editor.
It is a partially implemented C# smart editor.

## Main Limitations Holding It Back

## 1. Workspace fidelity is the biggest problem

The editor has Roslyn features, but it does not have a fully faithful project system.

This affects:

- completion quality
- diagnostic accuracy
- symbol resolution
- refactoring availability
- analyzer support
- source generator awareness
- cross-targeting correctness

If the project model is incomplete, every feature built on top of it becomes only partially correct.

## 2. The system is open-file centric

A proper IDE maintains a more complete, always-on model of the loaded solution.
This implementation mostly reacts to the active file and dependent open files.

That keeps things simple, but it limits:

- whole-solution diagnostics
- whole-solution freshness
- background indexing
- large feature reliability

## 3. Refactoring/application semantics are not fully safe yet

Roslyn can compute multi-file edits, but the editor does not fully persist them.
That makes advanced refactoring support less trustworthy than it appears.

## 4. The UX is thinner than a real C# IDE

Missing or limited areas include:

- richer completion presentation
- richer quick info formatting
- automatic lightbulbs near diagnostics
- inline rename UI
- grouped references view
- better signature help lifecycle
- refactoring discovery based on selection/context
- deeper command discoverability

## 5. Project change tracking is weak

The file explorer watcher refreshes the tree, but the Roslyn workspace is not being maintained as a first-class live project system when files, references, or project files change externally.

## 6. There is little evidence of automated coverage around this subsystem

For a subsystem this stateful, custom, and concurrency-heavy, tests are especially important.
I did not find a dedicated code-analysis test project in the reviewed workspace.

## Thoughts on How to Improve It

## Priority 1: Replace the custom project approximation with a higher-fidelity workspace model

If only one area is improved first, it should be this one.

### Best direction

Move toward a real design-time project loading path such as:

- `MSBuildWorkspace`, or
- a more explicit Roslyn workspace/project-system integration that uses real compile references, analyzers, additional files, and source-generated documents.

### Why this matters most

This will improve almost everything at once:

- diagnostics
- completion
- navigation
- code actions
- rename/refactorings
- source generator behavior
- analyzer support

### Specific targets

- support `TargetFrameworks`
- load analyzer references
- load `AdditionalFiles`
- respect `EditorConfig` and analyzer config documents
- use resolved compile references instead of manual NuGet `lib` scanning
- stop defaulting to `LanguageVersion.Latest` when the project did not request it

## Priority 2: Fix multi-file edit persistence immediately

This is the most important correctness bug in the current editing pipeline.

### Required changes

- when a rename or code action changes unopened files, write those files to disk,
- when open tabs are changed programmatically, mark them dirty consistently,
- handle document add/remove/rename operations, not only changed document text,
- keep Roslyn workspace, tab state, and disk state in sync.

Until this is fixed, advanced refactorings should be considered partially trustworthy.

## Priority 3: Build a real background analysis scheduler

Current analysis is simple and understandable, but it is still a coarse full-file refresh loop.

### Improve by

- separating classification, diagnostics, and navigation indexing,
- caching per-document analysis snapshots,
- doing lighter incremental updates where possible,
- distinguishing active-document latency from background solution work,
- throttling expensive full-solution operations.

This should improve responsiveness as projects grow.

## Priority 4: Upgrade diagnostics from compiler-centric to IDE-grade

### Add

- analyzer diagnostics
- project-level diagnostics
- richer severity/category presentation
- grouped error list views
- refresh on project/solution change
- diagnostic source information where available

### UI improvements

- clickable fixes from the error list
- auto lightbulb glyphs in the editor margin
- better icons and theming

## Priority 5: Improve completion and signature help UX

### Completion

- better trigger rules
- symbol kind icons
- richer item description panels
- import completion support if practical
- better commit character handling
- more aggressive sync with caret movement and filtering behavior

### Signature help

- update while caret moves inside the argument list
- handle backspace and arbitrary edits
- better overload selection
- richer formatting for active parameters and docs

## Priority 6: Improve refactoring discovery and action composition

### Recommended changes

- use spans/selections where appropriate, not only caret positions,
- make extract method selection-aware end-to-end,
- avoid title-based heuristics where possible,
- add telemetry/logging for provider failures,
- make action availability easier to diagnose during development.

## Priority 7: Make navigation results richer

### Useful enhancements

- group references by definition/project/file
- distinguish definition vs reference vs implementation visually
- add peek-style preview
- add call hierarchy or symbol search later

## Priority 8: Add automated tests around the analysis subsystem

This code is a good candidate for tests because it mixes:

- concurrency
- async cancellation
- file/workspace synchronization
- Roslyn integration
- UI-driven command flows

### High-value tests

- loading single-project and multi-project solutions
- cross-project go to definition
- find references
- rename across multiple files
- completion at key syntactic positions
- diagnostics after edits
- file add/remove/project reload behavior
- code action application persistence

## Recommended Roadmap

## Phase 1: Correctness

1. Fix multi-file apply persistence.
2. Improve dirty-state handling for programmatic edits.
3. Improve project loading fidelity for compile references and language version.

## Phase 2: Fidelity

1. Move to a more faithful MSBuild/Roslyn workspace.
2. Add analyzers, additional files, and editor config support.
3. Improve solution-wide refresh behavior.

## Phase 3: UX

1. Better completion UI.
2. Better signature help lifecycle.
3. Better quick info formatting.
4. Automatic lightbulbs and richer diagnostics UI.

## Phase 4: Scale and maintainability

1. Introduce tests for analysis workflows.
2. Add diagnostics/logging around provider failures.
3. Separate analysis scheduling from UI orchestration more cleanly.

## Final Assessment

The current code analysis system is a respectable custom Roslyn integration, especially for a non-Visual-Studio editor shell.
It already does much more than plain syntax highlighting.

However, it is not yet comparable to a proper C# IDE because the project system and analysis orchestration are still simplified.

My view is:

- the feature list is already strong,
- the core architectural bet on Roslyn is correct,
- the biggest missing piece is workspace fidelity,
- the most urgent bug is multi-file edit persistence,
- and the path to a much better experience is clear.

If those two areas are addressed first, the rest of the editor intelligence will likely improve much faster and more reliably.
