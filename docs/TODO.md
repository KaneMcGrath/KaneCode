# Kane Code — Development Tracker

> This file tracks implementation progress across all phases.
>
> **AI agents**: 
> pick the next unchecked `[ ]` item.
> Mark `[~]` when starting work, `[x]` when complete
>
> write tests when necessary in `KaneCode.Tests` to verify correctness of your implementation, 
> and to build out a robust set of tests for the future


## Status Key

- `[ ]` — Not started
- `[-]` — Skip
- `[~]` — In progress
- `[x]` — Complete

---

## Task List

### Phase 1: Correctness

#### 1.1 Fix Multi-File Edit Persistence

- [x] Write changed but unopened files to disk when a rename or code action modifies them (`ApplyMultiFileChanges` in `MainViewModel`)
- [x] Mark open tabs dirty when their text is changed programmatically by a code action or rename
- [x] Handle document add, remove, and rename operations from Roslyn solution changes (not only changed document text)
- [x] Keep Roslyn workspace state, tab state, and disk state in sync after every multi-file operation
- [x] Add guard/logging when a code action produces changes to files that cannot be persisted

#### 1.2 Improve Dirty-State Handling

- [x] Ensure programmatic edits to non-active open tabs set the tab dirty flag so the user is prompted to save
- [x] Verify that undo/redo stacks are correct after programmatic multi-file edits

#### 1.3 Improve Project Loading Fidelity — Compile References & Language Version

- [x] Use resolved compile references from MSBuild evaluation (`ReferencePath` items) instead of manual NuGet `lib` folder scanning
- [x] Derive `LanguageVersion` from the TFM default when `LangVersion` is not explicitly set, instead of falling back to `LanguageVersion.Latest`
- [x] Support `TargetFrameworks` (plural / multi-targeting) — pick the first or let the user choose
- [x] Include generated source files from the `obj` folder more reliably (cover all `*.g.cs` and `*.g.i.cs` patterns)

---

### Phase 2: Workspace Fidelity

#### 2.1 Move to Higher-Fidelity MSBuild/Roslyn Workspace

- [ ] Evaluate replacing `AdhocWorkspace` + manual loading with `MSBuildWorkspace` for project/solution open
- [ ] If `MSBuildWorkspace` is adopted, wire it into `RoslynWorkspaceService` as the backing workspace
- [ ] If `MSBuildWorkspace` is not viable, enhance `MSBuildProjectLoader` to use design-time build outputs for references and source files

#### 2.2 Load Analyzer References

- [ ] Discover analyzer assemblies from NuGet packages and SDK during project load
- [ ] Register `AnalyzerReference` entries on the Roslyn project so analyzer diagnostics appear
- [ ] Verify that common analyzers (e.g. `Microsoft.CodeAnalysis.NetAnalyzers`) produce diagnostics in the error list

#### 2.3 Load AdditionalFiles and EditorConfig

- [ ] Add `AdditionalFiles` (e.g. `.razor`, `.cshtml`, analyzer data files) to the Roslyn project during loading
- [ ] Add `.editorconfig` and `AnalyzerConfig` documents to the Roslyn project so style/analyzer settings are respected
- [ ] Verify that `.editorconfig` severity overrides affect reported diagnostics

#### 2.4 Improve Solution-Wide Refresh

- [ ] Refresh diagnostics for all open documents (not just dependents of the active file) after edits
- [ ] Re-evaluate the Roslyn workspace when project files (`.csproj`) change on disk (file watcher integration)
- [ ] Re-evaluate the Roslyn workspace when `Directory.Build.props` / `Directory.Packages.props` change

---

### Phase 3: UX Improvements

#### 3.1 Completion UI Enhancements

- [ ] Add symbol-kind icons (method, property, class, etc.) to completion list items
- [ ] Broaden auto-trigger rules (e.g. after `<`, `[`, `::`, spaces in certain contexts)
- [ ] Improve commit-character handling to match common IDE behavior (`;`, `(`, `[`, etc.)
- [ ] Add import-completion support (suggest types from unreferenced namespaces with automatic `using`)
- [ ] Add a detail/description side panel for the selected completion item

#### 3.2 Signature Help Lifecycle

- [ ] Update active parameter highlight as the caret moves within the argument list (not just on `(` and `,`)
- [ ] Handle backspace and arbitrary edits inside argument lists without dismissing the window
- [ ] Improve overload selection to prefer the overload matching the current argument count/types
- [ ] Use richer formatting for the active parameter (bold/highlight) and XML doc content

#### 3.3 Quick Info / Hover Formatting

- [ ] Render Roslyn QuickInfo sections as rich formatted content (syntax-colored signatures, XML doc, etc.) instead of plain text
- [ ] Support pinning or copying tooltip content
- [ ] Show diagnostic messages inline within the hover tooltip when hovering over a squiggle

#### 3.4 Diagnostics & Error List UX

- [ ] Add proper severity icons (error, warning, info) to error list rows replacing placeholder glyphs
- [ ] Add grouped/filterable error list views (by severity, project, file)
- [ ] Show diagnostic source information (analyzer ID, category) in the error list
- [ ] Add clickable "fix" links from error list rows that trigger associated code fixes

#### 3.5 Automatic Lightbulb Improvements

- [ ] Show the lightbulb glyph in the editor margin automatically when code actions are available for the current line
- [ ] Position the lightbulb near diagnostics (not just at caret) for discoverability
- [ ] Dismiss the lightbulb when moving to a line with no available actions

#### 3.6 Inline Rename UI

- [ ] Add an inline rename experience (highlight all occurrences, type-to-rename) instead of a dialog/prompt
- [ ] Show a preview of affected files/locations before committing the rename

#### 3.7 Navigation Enhancements

- [ ] Group Find References results by project → file → definition vs. reference vs. implementation
- [ ] Add visual distinction (icons/colors) for definition, reference, and implementation results
- [ ] Add peek definition (inline preview without navigating away)
- [ ] Add symbol search / Go To Symbol command (search by name across the solution)

---

### Phase 4: Scale & Maintainability

#### 4.1 Background Analysis Scheduler

- [ ] Separate classification, diagnostics, and navigation indexing into independent background tasks
- [ ] Cache per-document analysis snapshots to avoid full recomputation on every edit
- [ ] Implement incremental / partial-file classification updates for large files
- [ ] Distinguish active-document latency-sensitive work from background solution-wide analysis
- [ ] Throttle expensive full-solution operations to avoid UI stalls

#### 4.2 Refactoring Discovery & Action Composition

- [ ] Use the editor selection span (not just caret position) when requesting refactoring actions
- [ ] Make Extract Method fully selection-aware without title-matching heuristics
- [ ] Log provider failures at a diagnostic level instead of silently swallowing them
- [ ] Add telemetry/counters for code-action provider success/failure rates

#### 4.3 Automated Tests for Code Analysis

- [ ] Create a `KaneCode.Tests` (or similar) test project for analysis subsystem tests
- [ ] Add tests for loading single-project and multi-project solutions into the Roslyn workspace
- [ ] Add tests for cross-project Go To Definition
- [ ] Add tests for Find References returning expected results
- [ ] Add tests for Rename across multiple files and verifying all file contents
- [ ] Add tests for completion at key syntactic positions (after `.`, inside generics, etc.)
- [ ] Add tests for diagnostics appearing and updating after edits
- [ ] Add tests for file add/remove/project reload behavior in the workspace
- [ ] Add tests for code action application and multi-file persistence

#### 4.4 Logging & Diagnostics for Development

- [ ] Add structured logging around code-fix and refactoring provider discovery and invocation
- [ ] Add logging for workspace/project load failures and partial load warnings
- [ ] Separate analysis scheduling logic from UI orchestration in `MainViewModel` into a dedicated service
