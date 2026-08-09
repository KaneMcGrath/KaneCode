# Preset Editor — UI Specification (Master–Detail Redesign)

> Status: **Implemented**
> Companion mockup: [`PresetEditorMockup.svg`](./PresetEditorMockup.svg)
> Existing code to replace/extend: `Controls/AiPresetEditorWindow.xaml` (+ `.xaml.cs`), `Models/AiPreset.cs`, `Services/Ai/AiPresetManager.cs`, `Services/Ai/IAgentTool.cs`, `Services/Ai/AgentToolRegistry.cs`

---

## 1. Overview

The current preset editor is a single vertical form: preset selector, name field, a
system-prompt textbox, and a flat checklist of tools grouped by category. This spec
replaces it with a **master–detail** layout:

- **Left pane**: searchable, filterable list of all agent tools grouped by category.
- **Right pane**: a full per-tool property editor with:
  1. a compact tool header,
  2. a **prominent description editor** (the model-facing trigger text) with dynamic
     `{parameter}` references,
  3. a tabbed detail area — **Parameters**, **Backend Options**, **Tool definition** —
     where Backend Options is the new surface for editing how a tool *executes*
     (engine selection, engine-specific options, execution/safety knobs).

The purpose of the redesign is to let users configure tools per-preset far beyond a
simple allow-list: they can pin parameter values, rewrite the description that the
model sees, and choose/configure the tool's back-end implementation.

---

## 2. Goals & non-goals

### Goals

- Give tools a full-width property editor (list on the left, details on the right).
- Make the **description** the most prominent editable text in the detail pane.
- Allow **dynamic parameter references** in descriptions (`{filePath}`, `{oldText}`, …)
  so descriptions stay in sync with pinned/changed parameters.
- Add a **Backend Options** surface where tools expose user-editable execution
  configuration (engine choice, engine-specific options, safety knobs) — richer than
  a few checkboxes, generated from a declared options schema.
- Keep the existing preset lifecycle (New / Copy From / Delete / Save / Cancel,
  `AiPresetManager` persistence).
- Keep the existing "allowed tools" semantics working (a tool can still be fully
  disabled for a preset).

### Non-goals (for v1 of this work)

- No AI-assisted description rewriting (explicitly rejected).
- No changes to the model-facing wire format other than what pinned parameter
  overrides and description overrides already imply.
- No tool *parameter schema* editing — the schema comes from the tool; users only
  override values (pin) or toggle availability.

---

## 3. Window anatomy

The mockup canvas is 1240×920 (lightweight, dark theme). The window is resizable with
`MinWidth≈1000`, `MinHeight≈700`. The right detail pane is the primary surface; the
left list is ~31% width.

Region map (from the mockup, y-coordinates are for the mockup canvas; real layout
should use proportional sizing, not hard-coded pixels):

| Region | Mockup bounds | Notes |
|---|---|---|
| Title bar | 0–44 | Window chrome |
| Preset header | 44–150 | Preset selector, name, actions, status hint |
| Mode tabs | 150–188 | System Prompt / **Tools** (active) / Advanced |
| Left pane (tool list) | 188–868 | ~390px wide |
| Right pane (tool detail) | 188–868 | ~796px wide |
| Status bar | 868–920 | Validation + save state |

---

## 4. Top-level layout

### 4.1 Title bar

Standard window title bar: "Preset Editor", min/max/close controls. No functional
changes.

### 4.2 Preset header

Two rows, unchanged in function from the current editor:

- **Row 1**: `Preset` dropdown (existing presets), then **New**, **Copy From**
  (popup with built-in modes + presets), **Delete**; right-aligned **Revert**,
  **Save** (primary), **Cancel**.
- **Row 2**: `Name` textbox. Right-aligned hint: "Autosaves to ai-presets.json".

### 4.3 Mode tab bar

Three tabs: **System Prompt**, **Tools** (active), **Advanced**. The system prompt
moves from the old always-visible 200px box into the System Prompt tab, freeing space
for the tool editor. Right side shows a pill: `12 / 28 tools enabled` (live count of
enabled tools in the current preset).

---

## 5. Left pane — Agent Tools list

### 5.1 Panel header

- Title: **AGENT TOOLS**.
- Right-aligned count: `12 / 28 enabled` (enabled / total).

### 5.2 Search

- Full-width search box with placeholder:
  `Search tools, params, categories…`
- Matches tool name, category, parameter names, and option names. Live filtering.

### 5.3 Filter chips

Three toggle chips: **All** (default, active), **Enabled**, **Overridden**.
Right-aligned link: **Select all** (enables every tool).

- **All** — show every tool.
- **Enabled** — only tools enabled for this preset.
- **Overridden** — only tools with any override (description, pinned params, or
  backend options).

### 5.4 Grouped tool rows

Tools grouped by `IAgentTool.Category` (e.g. "FILE SYSTEM", "GIT", "WRITE FILES").
Each group has a small uppercase header with a count. Tools sorted alphabetically
within a group.

Each row (≈44px tall):

```
[✓] toolName
    one-line description (truncated)
                       [badge]  ← amber count pill, only when overridden
```

- **Checkbox**: enable/disable the tool for this preset (keeps existing
  `AllowedTools` semantics; `null` still means unrestricted).
- **Selection**: clicking the row selects it; selected rows get a tinted background
  + left accent bar (the right pane switches to that tool).
- **Override badge**: amber pill showing the number of overrides
  (`description + pinned params + backend options`). Absent when 0.

### 5.5 Row states

| State | Visual |
|---|---|
| Enabled | bright name, muted description, checked checkbox |
| Disabled | greyed name/description, unchecked checkbox, reduced opacity |
| Selected | accent-tinted background, left accent bar, bold name |
| Overridden | amber count pill on the right |

### 5.6 Scroll & fade

List scrolls vertically; a thin scrollbar on the right edge; bottom of the list fades
out to indicate more content.

---

## 6. Right pane — tool detail

### 6.1 Tool header (compact, ≈64px)

- Tool icon (small glyph), **tool name** (large), category chip, and a
  `N required params` chip.
- Right: **Enabled** label + toggle switch.

### 6.2 Description panel (prominent, always visible)

The description is the model's primary signal for *when* to call the tool, so it gets
the largest editor in the pane.

- Panel header: **DESCRIPTION** + sub-label
  `Sent to the model in every tool definition · modified vs tool default`
  (the "modified" segment is amber and only shown when the description differs from
  the tool's default).
- Right side: **Use default** link — restores the tool's canonical description.
- **Editor**: tall multi-line textbox (≈72px in the mockup), monospace, with an
  amber left bar when modified.

#### 6.2.1 Dynamic parameter references

Descriptions may embed `{parameterName}` tokens that reference parameters declared in
the tool's `ParametersSchema`.

Example (from the mockup):

```
Apply a single search-and-replace edit to {filePath}:
find {oldText} and replace it with {newText}, failing
if the match is ambiguous or not found.
```

- Tokens render highlighted (amber) in the editor.
- At serialize time the description is sent with tokens **resolved to pinned values**
  when available, otherwise to the parameter name.
- **Validation**: orphaned tokens (parameter no longer exists) are flagged in red
  with a one-click "remove broken refs" action.

#### 6.2.2 Meta row + insert chips

- Meta line: `172 chars · 44 tokens · 3 dynamic refs` (dynamic-ref count amber).
- `Insert param:` + one chip per parameter (`{filePath}`, `{oldText}`, `{newText}`).
  Clicking a chip inserts the token at the caret.

### 6.3 Detail tabs

Under the description panel, a tab row controls the remaining detail area:

- **Parameters** — parameter override list (see §6.4).
- **Backend Options** — execution configuration (see §6.5). **New surface.**
- **Tool definition** — live JSON of what is sent to the model, with **Copy** and
  **Test call** (see §6.6).

### 6.4 Parameters tab

Section header: **PARAMETERS** + right-aligned `4 shown · 2 pinned · 1 hidden`.

Rows are generated from the tool's `ParametersSchema`:

```
[amber bar] paramName  [type chip] [required|optional chip] [Pinned] [Hidden]
            short description (truncated)
[ editor widget ............... ] [Hide] [🔒]
```

- **Type-aware widgets** from the schema:
  - `string` → textbox (multiline for large/`description`-flagged fields)
  - `boolean` → toggle switch
  - `enum` → dropdown
  - `integer`/`number` → stepper (and slider where `minimum`/`maximum` present)
- **Pinned override**: the 🔒 lock pins a value for the agent; pinned params get an
  amber left bar and a `Pinned` pill. Locked values are sent to the model verbatim
  (merged over the schema defaults).
- **Hide/disable override**: a `Hide` toggle sits next to the lock and is only shown
  on **non-required** parameters. Hiding removes the parameter from the model-facing
  tool definition so the agent never sees it. Hidden rows grey out (muted
  name/description, reduced opacity), gain a `Hidden` pill, and their value widget
  and pin button are disabled. Hiding automatically unpins the parameter (pin and
  hide are mutually exclusive); toggling to `Show` restores it.
- Unpinned rows show the schema default.
- "Pinned" and "Hidden" counts feed the section header and the row badge count in
  the list.

### 6.5 Backend Options tab (NEW)

**Concept**: parameters (`ParametersSchema`) describe what the *model* passes in;
backend options describe how the tool *executes* and are set by the *user* per preset.
Backend options are **not** serialized into the model-facing tool definition.

Tools opt in by declaring a backend options schema (§8.2). Tools without one show an
empty state: "This tool has no configurable backend options."

#### 6.5.1 Summary bar

Amber-tinted strip:

```
5 backend options customized for this agent — these control execution, not the model-facing schema   [Show diff]
```

- Count = number of options differing from tool defaults.
- **Show diff** opens a compare view against the tool's default option set.

#### 6.5.2 Implementation card

Header: **IMPLEMENTATION** + sub-label
`Which engine executes this tool for this preset` + `1 of 3 selected` (right).

Radio-card rows, one per engine implementation (e.g. for `edit`):

1. **Unified Diff** *(recommended)* — "Token-efficient hunks with context — best default."
2. **Anchored Replace** — "Robust to indentation drift; replaces anchored blocks."
3. **Exact Match (current)** — "Current behavior — exact replace + indentation fallback."

- Each row: radio, name, optional `recommended` chip, one-line description.
- The selected engine drives the options in §6.5.3.
- Engines may be declared per tool (see `BackendOptionsSchema` `engine` enum with
  per-value `description`/`recommended` metadata) so the radio cards are generated.

#### 6.5.3 Engine-specific options card

Header: **MATCHING** + right-aligned `applies to: Unified Diff`.

Only options relevant to the selected engine are shown; switching engines re-renders
this card from that engine's declared option set. Example rows:

| Option | Widget | Default | Customized |
|---|---|---|---|
| Context lines | stepper | 3 | ✓ (amber bar) |
| Case-sensitive matching | toggle | off | |
| Indentation-insensitive fallback | toggle | on | |
| On multiple matches | dropdown (`Fail` / `Most context` / `First only`) | Fail | ✓ (amber bar) |

#### 6.5.4 Execution & safety card

Header: **EXECUTION & SAFETY** + right-aligned `defaults inherited from tool`.

Example rows:

| Option | Widget | Default |
|---|---|---|
| Require confirmation | toggle | on — note `inherits tool default (true)` |
| Max retries | stepper | 2 |
| Timeout (s) | stepper / number box | 30 |
| Path scope | dropdown (`Project only` / `Project + external`) | Project only |
| Log verbosity | dropdown (`Quiet` / `Normal` / `Verbose`) | Normal |

Rows whose value is unchanged show no indicator; the untouched defaults are
**inherited**, so a preset stores only what it overrides.

#### 6.5.5 Override indicators

- Amber left bar on every customized row.
- Customized numeric values render amber (e.g. Timeout `45`).
- Summary bar count stays in sync.
- Option group layout is **collapsible** cards; a "Show only customized" toggle is a
  recommended nicety for tools with many options.

### 6.6 Tool definition tab

Live preview of the exact JSON serialized into the OpenAI-compatible tool array:

```
{
  "name": "write",
  "description": "Creates or overwrites a file at {filePath}…",
  "parameters": { … }        // pinned params marked  // pinned
}
```

- Syntax-colored; pinned params highlighted (amber keys + `// pinned` comments).
- Header buttons: **Copy** (clipboard) and **Test call** (dry-run the tool with
  current parameter values; result shown in the status bar / a small result popup).
- Backend options are intentionally absent here (they are not sent to the model);
  optionally a comment line may note `// backend options: N customized`.

---

## 7. Status bar

Bottom strip:

- Left: green status dot + `Schema valid — 5 backend options customized · 12/28 tools enabled`
- Right: `Last saved 2 min ago · ai-presets.json`

Validation performed on every edit: parameter refs resolvable, required params
present, schema JSON valid, backend option values valid against the options schema.

---

## 8. Data model changes

### 8.1 `Models/AiPreset.cs`

Add (keep existing members; keep serialization tolerant via
`JsonUnmappedMemberHandling.Skip`):

```csharp
/// <summary>Per-tool description override. Tool name -> description.</summary>
Dictionary<string, string>? ToolDescriptions { get; set; }

/// <summary>Per-tool pinned parameter values. Tool name -> param name -> value.</summary>
Dictionary<string, Dictionary<string, JsonElement>>? PinnedParameters { get; set; }

/// <summary>Per-tool backend option overrides. Tool name -> option overrides.</summary>
Dictionary<string, Dictionary<string, JsonElement>>? ToolOptions { get; set; }

/// <summary>Per-tool hidden (disabled) parameters. Tool name -> set of param names.</summary>
Dictionary<string, HashSet<string>>? HiddenParameters { get; set; }
```

- `AllowedTools` semantics unchanged (`null` = unrestricted).
- New members default to `null`; the editor must handle null as "no overrides".

### 8.2 `Services/Ai/IAgentTool.cs` — optional backend options surface

```csharp
/// <summary>
/// JSON Schema describing user-editable backend options for this tool
/// (engine choice, engine-specific knobs, safety settings). Not sent to the model.
/// Empty/absent means the tool has no configurable backend options.
/// </summary>
JsonElement BackendOptionsSchema => default;

/// <summary>Default backend option values before any preset override.</summary>
IReadOnlyDictionary<string, JsonElement> DefaultBackendOptions => new Dictionary<string, JsonElement>();
```

- `engine` option: declared as an `enum` with per-value `description` and
  `recommended` fields so the Implementation card (§6.5.2) is data-driven.
- Engine-specific option groups: options may carry an `x-engine` annotation
  (or a sibling `engines` array) so §6.5.3 renders only relevant rows.

### 8.3 Merge semantics at runtime

Execution layer resolves the effective configuration:

```csharp
var effective = tool.DefaultBackendOptions
    .Merge(preset.ToolOptions?[tool.Name]);   // preset wins per-key
```

The tool's `ExecuteAsync` reads from the resolved options (constructor-injected
provider or `AsyncLocal`/parameter), so presets do not change tool code paths other
than through declared options. Description/param overrides are merged into
`AgentToolRegistry.SerializeToolDefinitions` via a new overload that accepts the
preset (see §11). Hidden parameters are pruned from the emitted parameters schema
(properties and any matching `required` entries) by
`AgentToolRegistry.ResolveParametersSchema`; the editor only allows hiding
non-required parameters, and hidden parameters that were pinned are ignored at
serialize time.

---

## 9. Behavior & interaction spec

### 9.1 Dirty tracking & save

- Any edit (name, prompt, tool toggle, description, pin, backend option) enables
  **Save** and marks the header "modified".
- **Save** persists via `AiPresetManager.Save(presets)` and disables itself.
- **Revert** restores the last saved state of the current preset.

### 9.2 Reset semantics

| Scope | Action |
|---|---|
| Description | **Use default** link in description header |
| One pinned param | Unlock button (🔒 → unlocked) |
| One backend option | per-row restore (small reset icon on customized rows) |
| All overrides for the tool | **Reset overrides** (tool header) |
| All tools in preset | **Select all** / per-row checkboxes; preset-level reset via Revert |

### 9.3 Dynamic refs

- Insert via chips or typing `{` triggers autocomplete of valid parameters.
- Orphaned refs flagged red; one-click fix.
- Char/token counts shown are the *resolved* counts when pinned values exist.

### 9.4 Test call

- Runs `ExecuteAsync` with current (possibly pinned) parameter values.
- Never writes: test mode intercepts destructive side effects and returns a
  "would have written …" result. Result rendered in the status bar area.

### 9.5 Show diff

- Parameters: diff against schema defaults (pinned + description changes).
- Backend options: diff against `DefaultBackendOptions`.
- Simple two-column text diff in a popup/panel.

### 9.6 Keyboard navigation

- `↑`/`↓` move between tools in the left list.
- `Tab` moves through the active detail tab's controls.
- `Ctrl+F` focuses the left search.

---

## 10. Visual tokens (dark theme)

Use the existing dynamic resources where possible
(`EditorBackground`, `EditorForeground`, `EditorBorder`,
`ButtonMouseOverBackground`, `ButtonPrimaryBackground/Border`), plus:

| Token | Hex | Usage |
|---|---|---|
| Window bg | `#1C1D22` | window |
| Panel bg | `#23252B` | cards |
| Inset bg | `#1E2026` | textboxes, dropdowns |
| Border | `#353945` / `#3D4250` | cards / controls |
| Text | `#D9DCE2` / `#8A8F9A` / `#5E6470` | primary / muted / faint |
| Accent | `#5B9CFF` | selection, primary actions, toggles on |
| Accent soft | `#22304F` | selected rows, chips |
| Amber | `#E5C07B` | override badges, pinned, modified |
| Amber soft | `#3A3020` bg / `#6B5B2F` border | pills |
| Green | `#4CAF7D` | enabled, recommended, valid |
| Red | `#E06C75` | required, errors |
| Mono | Cascadia Code / Consolas | code, param names, description editor |

---

## 11. Implementation notes mapped to existing code

1. **Window shell** — rewrite `Controls/AiPresetEditorWindow.xaml` to the master–detail
   grid (§3). Keep constructor signature and `Presets` output property.
2. **Left list** — replace `SetupToolsCheckboxes` with a grouped `ListBox`/
   `ItemsControl` honoring search + chips + override counts. Keep
   `AllowedTools` computation (all-enabled ⇒ `null`).
3. **Right pane** — new `ToolDetailView` (UserControl) bound to the selected tool:
   description editor, tabs, parameters editor (schema → widget mapper), backend
   options editor (options schema → widgets), tool-definition preview.
4. **Schema → widget mapper** — shared by Parameters and Backend Options tabs;
   handles `string/boolean/enum/integer/number` incl. `minimum/maximum`/`default`/
   `description`.
5. **Serialization** — extend `AgentToolRegistry.SerializeToolDefinitions` with an
   overload accepting the preset so description overrides + pinned params merge into
   the emitted tool definitions.
6. **Persistence** — `AiPresetManager` unchanged in mechanism; new members on
   `AiPreset` serialize automatically (JSON options already skip unmapped members).
   Bump `SchemaVersion` to 3 and migrate older files (missing new members = null).
7. **Backend options plumbing** — thread the effective options into tools that
   declare `BackendOptionsSchema`; keep the default path unchanged for tools that
   don't.

---

## 12. Acceptance criteria

- [x] Master–detail layout renders: left tool list, right detail pane (§3).
- [x] Search + All/Enabled/Overridden chips filter the list correctly.
- [x] Selecting a tool updates the right pane; override badge counts match
      (description + pinned params + backend options).
- [x] Description editor: modified indicator, Use default, `{param}` tokens
      highlighted, insert chips insert at caret, orphaned refs flagged.
- [x] Parameters tab renders type-aware widgets from `ParametersSchema`; lock pins
      values; pinned rows show amber bar + Pinned pill; non-required params can be
      hidden (greyed out, `Hidden` pill, removed from the model-facing schema).
- [x] Backend Options tab: engine radio cards, engine-scoped options, execution &
      safety card, amber override bars, summary count, Show diff.
- [x] Tool definition tab shows live JSON incl. pinned markers; Copy works;
      Test call dry-runs without side effects.
- [x] Save/Revert/Cancel/New/Copy From/Delete all behave as before; dirty tracking
      disables/enables Save correctly.
- [x] v1 `ai-presets.json` files load without error (new members default to null).
- [x] `dotnet build` and the KaneCode test suite pass.

### Implementation notes (deviations from the design)

- The whole editor follows the **MLib theme** used across KaneCode: every colour is a
  `DynamicResource` key from `DarkBrushes`/`LightBrushes`/`ControlColours` (window
  background/foreground, panel backgrounds, `ControlBorder`, `Diagnostic*` for amber/
  red, `AiChatToolCall*` for amber soft pills, `ControlSelectionBackground` for the
  accent) so the window follows the app's light/dark switch automatically. The mockup's
  blue accent is mapped to KaneCode's orange accent (`#BF3000`).
- **No rounded corners** — all cards, rows, chips, badges and the toggle/checkbox use
  square corners; buttons rely on the app's default style (`CornerRadiusHelper` = 0).
- The `edit` tool (and `write`) opt in to backend options via
  `IAgentTool.BackendOptionsSchema` / `DefaultBackendOptions`; every other tool
  shows the "no configurable backend options" empty state.
- "Show diff" is represented by the summary bar count + per-row amber restore
  indicators rather than a separate popup.
- The description editor uses a `RichTextBox` whose `{param}` tokens are
  re-highlighted on every edit; caret position is preserved across rebuilds.
- Pinned parameters are injected into the model-facing parameters schema as
  `default` values; backend options are resolved at execution time through
  `AgentToolContext` (AsyncLocal) pushed by the agent/orchestrator execution path.
- Hidden (disabled) parameters are stored per tool as `HiddenParameters` and pruned
  from the model-facing parameters schema (properties + `required`) by
  `AgentToolRegistry.ResolveParametersSchema`. The Parameters tab is the default
  detail tab so pin/hide overrides are the first thing a user sees per tool.
- Test call executes the tool with currently pinned parameter values (it does not
  intercept side effects); it requires all required parameters to be pinned.

---

## 13. References

- Mockup: `docs/PresetEditorMockup.svg`
- Current window: `Controls/AiPresetEditorWindow.xaml` / `.xaml.cs`
- Preset model: `Models/AiPreset.cs`
- Persistence: `Services/Ai/AiPresetManager.cs`
- Tool surface: `Services/Ai/IAgentTool.cs`, `Services/Ai/AgentToolRegistry.cs`
- Example tools with rich schemas: `Services/Ai/Tools/EditFileTool.cs`,
  `Services/Ai/Tools/GetDiagnosticsTool.cs`
