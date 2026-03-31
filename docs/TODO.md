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
- `[-]` — Skip
- `[~]` — In progress
- `[O]` — Awaiting review
- `[x]` — Complete

---

## Task List

### Phase 10 — Code Analysis MSBuild Parity

- [ ] Replace manual compile reference discovery with resolved MSBuild compile references
  - QA: Open a package-heavy C# project and confirm common framework and NuGet types resolve without false errors.
  - QA: Compare build output vs editor diagnostics for a few files and confirm missing-reference errors are reduced.
  - QA: Confirm completion, hover, and go-to-definition work for types coming from package references.

- [ ] Support `TargetFrameworks` and select the active target framework consistently during workspace load
  - QA: Open a multi-targeted project and confirm the workspace loads without dropping analysis.
  - QA: Verify diagnostics and completion change appropriately when the selected target framework changes.
  - QA: Confirm platform-specific APIs are only flagged when invalid for the active target.

- [ ] Load analyzer references, analyzer config documents, and additional files into the Roslyn workspace
  - QA: Open a project with analyzer packages and confirm analyzer diagnostics appear in the editor.
  - QA: Add or modify an `.editorconfig` rule and confirm diagnostic severity changes in the IDE.
  - QA: Verify projects using `AdditionalFiles` no longer lose related diagnostics or code actions.

- [ ] Improve generated file and source generator parity during project loading
  - QA: Open a project that relies on generated code and confirm generated symbols resolve in completion and navigation.
  - QA: Verify implicit usings, generated partial types, and generated members no longer produce false diagnostics.
  - QA: Confirm hover and find references work for symbols declared in generated outputs when source is available.

- [ ] Track project file changes and refresh the Roslyn workspace when project structure changes on disk
  - QA: Add a new `.cs` file to a loaded project from outside the editor and confirm it appears in analysis without reopening the solution.
  - QA: Remove or rename a source file on disk and confirm stale diagnostics and navigation entries disappear.
  - QA: Change a package or project reference and confirm analysis refreshes after reload.

- [ ] Fix multi-file code action and rename persistence so unopened changed files are written to disk
  - QA: Run rename on a symbol used across multiple files and confirm all changed files are updated on disk.
  - QA: Apply a code action that edits more than one file and confirm reopening the solution preserves all edits.
  - QA: Verify no changes are lost when only one of the affected files was open in the editor.

- [ ] Correct tab dirty-state and workspace synchronization for programmatic edits
  - QA: Apply rename or a code fix to an open tab and confirm the tab dirty state matches the actual file state.
  - QA: Apply a multi-file change and confirm open tabs update visually without requiring manual reopen.
  - QA: Save after a programmatic edit and confirm the on-disk file matches the editor and workspace text.

- [ ] Expand diagnostics from active/open-file refresh to a more complete solution-aware refresh model
  - QA: Make a change in one project that affects another project and confirm dependent diagnostics refresh without reopening files.
  - QA: Open a multi-project solution and confirm the error list includes cross-project issues more reliably.
  - QA: Measure that diagnostics remain responsive while editing large files or larger solutions.

- [ ] Improve code action discovery by using richer spans and better context instead of line-only heuristics
  - QA: Place the caret on a diagnostic and confirm expected quick fixes appear.
  - QA: Select code for extract method and confirm the action appears consistently without title-based guessing failures.
  - QA: Verify refactorings still appear when the caret is near, but not exactly on, the triggering token.

- [ ] Improve completion and signature help behavior to better match a full C# IDE
  - QA: Confirm completion triggers on more realistic typing scenarios, including member access and common identifier edits.
  - QA: Verify signature help updates when moving the caret, typing commas, backspacing, and closing parentheses.
  - QA: Confirm completion descriptions, insertion text, and filtering stay correct during fast typing.

- [ ] Improve diagnostics and navigation UI presentation for analysis results
  - QA: Confirm the error list clearly distinguishes errors, warnings, and info with stable icons or visuals.
  - QA: Verify find references results remain navigable and understandable across multiple projects and files.
  - QA: Confirm code action presentation is discoverable and usable near the caret.

- [ ] Add automated tests for workspace loading, diagnostics, navigation, rename, and multi-file code actions
  - QA: Run the test suite and confirm new analysis tests pass consistently.
  - QA: Verify there are tests covering single-project and multi-project loads, cross-project navigation, and rename persistence.
  - QA: Confirm regressions in code analysis behavior are caught by tests before manual validation.







