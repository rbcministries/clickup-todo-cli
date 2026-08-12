# Split-pane (F): three-way launch destination for agent dispatch

Issue **#508** — part of the Split-pane epic **#502**. Depends on **B (#504)**, which is
**merged** (`main` HEAD `5429617`): the `LaunchLocation.SplitPane` enum value now exists and the
`TerminalCommandPlanner` already routes it (split → tab → window ladder). This slice only exposes
the third choice in the two places dispatch reads it — **Settings** and the per-dispatch **Dispatch
pane** — plus a defensive config-migration clamp. No planner or launcher change.

## Problem

`LaunchLocation` is three-valued on `main`, but the dispatch UI still offers only two of the three:

- **Settings** (`SettingsScreen.cs:305-311`, text at `:430-434`) — the "Launch:" button toggles
  `NewWindow ⇄ NewTab`; `SplitPane` is unreachable and `LaunchLocationText` renders it as
  `"New window"` (the `_ =>` fallthrough).
- **Dispatch pane** (`TaskDetailScreen.cs:706-712`, read at `:1604`) — the per-dispatch override is a
  two-state `CheckBox` (`_launchLocationToggle`). A checkbox can't express three values; it collapses
  `SplitPane → UnChecked` on seed and can only ever produce `NewTab`/`NewWindow` on submit
  (`DispatchPaneModel.ToLaunchLocation(bool)`).

## Design

### Pure model (`DispatchPaneModel`)

Replace the boolean `ToLaunchLocation(bool)` — which structurally can't carry a third value — with a
shared, unit-testable cycle + label the two UI surfaces both drive:

- `CycleLaunchLocation(LaunchLocation) → LaunchLocation`: `NewWindow → NewTab → SplitPane → NewWindow`.
- `LaunchLocationLabel(LaunchLocation) → string`: `"New window"` / `"New tab (where supported)"` /
  `"Split pane (where supported)"`.

`LaunchLocationApplies(AgentSessionMode)` is unchanged — a one-off `claude -p` run has no terminal, so
**pane is meaningless there exactly as tab is**; the control keeps being greyed out (and Tab-skipped) in
one-off mode rather than offering a no-op third choice (issue's "keep the one-off gate").

### Settings (`SettingsScreen`)

The "Launch:" button's `Accepting` handler cycles via `CycleLaunchLocation` instead of the two-way
ternary; `LaunchLocationText` delegates to `DispatchPaneModel.LaunchLocationLabel` so all three render.
Default stays `NewWindow`; `BuildDispatchSettings` already persists the enum unchanged.

### Dispatch pane (`TaskDetailScreen`)

Change the control's **shape** from a two-state `CheckBox` to a cycle **`Button`** (the same idiom
Settings uses), holding the actual `LaunchLocation` in a field:

- Enter is trapped by the pane for **Submit** (`OnDispatchKey`), so the button can't cycle on Enter — it
  cycles on **Space**, which the pane classifies as `Other → PassThrough` and lets reach the focused
  control (the same route by which the sibling `CheckBox`es toggle on Space today). No new pane key.
- Seeded from `defaultLaunchLocation` directly (so a `SplitPane` default shows on open, which the old
  checkbox silently dropped). `SubmitDispatch` reads the field's current value instead of
  `ToLaunchLocation(checked)`. `UpdateLaunchLocationEnabled` still greys it out in one-off mode.

### Config migration (`ConfigMigrations`)

Widening a persisted enum: an out-of-range persisted value (e.g. a future ordinal, or a hand-edited
`"launchLocation": 99`) deserializes via `JsonStringEnumConverter` to `(LaunchLocation)99` **without
throwing**, then must degrade rather than surface a bogus value downstream. Add an unconditional
normalization in `ConfigMigrations.Apply` — alongside the existing `AgentDispatch ??= new()` /
`TaskWorkingDirectories ??= []` guards (which likewise run every load, not version-gated):

```csharp
if (!Enum.IsDefined(config.AgentDispatch.LaunchLocation))
    config.AgentDispatch.LaunchLocation = LaunchLocation.NewWindow;
```

Because `SplitPane` is now a defined name, a config written by this version (`"SplitPane"`) round-trips
cleanly here; an **older** binary (pre-#504, enum `{NewWindow, NewTab}`) would still throw on the unknown
`"SplitPane"` string at parse time — an unavoidable, pre-existing limitation of `JsonStringEnumConverter`
shared by every string enum in the config, and out of scope for this slice (the issue: "must not break an
older one *worse than it has to*"). Not widening the clamp to a per-enum tolerant converter keeps this
"following existing `ConfigMigrations` conventions".

## Tests

- **`DispatchPaneModelTests`** — replace the two `ToLaunchLocation_MapsCheckedStateToLocation` cases
  with `CycleLaunchLocation` (full 3-cycle + wrap) and `LaunchLocationLabel` (all three); keep
  `LaunchLocationApplies_OnlyForInteractive`.
- **`AgentDispatchSettingsTests`** — `ToLauncherOptions` carries `SplitPane` through.
- **`ConfigMigrationsTests`** — an out-of-range persisted `LaunchLocation` degrades to `NewWindow`; a
  valid `SplitPane`/`NewTab` is preserved.
- **`SettingsFormTests`** — if it exercises the launch cycle, extend to the three-way order + text.
- **`tui-validate`** — a new `dispatch_launch_location_check.py`: open the Ctrl+A Dispatch pane,
  Shift+Tab to the launch button, and assert Space cycles the rendered label
  `New window → New tab → Split pane`; a **one-off** leg asserts the control greys out (Tab-skipped).
  `dispatch_dir_browser_seed_check.py` and `detail_check.py` A/B stay green (pane height unchanged; the
  seed check doesn't assert on the launch row's text).

## Hard-rule compliance

- No `Generated/` edits, no `clickup-openapi.json` change, no Kiota regen (no ClickUp API surface).
- ClickUp auth quirk untouched.
- No second focusable pane (#3) — the button is another control on the existing single Dispatch pane;
  Space cycles it, so **no bare-letter binding** (#12) is introduced.
- Integration tests remain `SkippableFact`/env-gated; the new units are pure/offline; the new E2E
  scenario reuses the existing dispatch-pane boot.

## Phases

1. **Pure model + Settings + migration + units** — `DispatchPaneModel`, `SettingsScreen`,
   `ConfigMigrations`, and their unit tests. Build/test/format, commit, push → draft PR.
2. **Dispatch-pane reshape + E2E** — `TaskDetailScreen` checkbox → cycle button; new
   `dispatch_launch_location_check.py`. Build/test, run `tui-validate`, commit, push, mark ready.
