# Plan — Per-dispatch launch-location override in the Dispatch pane (#275)

Follow-up to #255/#274, which added the **settings-level** launch-location default
(`AgentDispatchSettings.LaunchLocation`: new window vs. new tab of the current terminal,
threaded to the launcher via `ToLauncherOptions()`). This issue adds an **optional per-dispatch
override** in the Dispatch pane so a single dispatch can pick window-vs-tab without changing the
saved default — mirroring how the #94 session-mode and #97 post-to-Comments toggles seed from
their settings defaults and override per dispatch.

## Acceptance criteria (from the issue)

- A per-dispatch **launch location** toggle in the Dispatch pane, initialized from
  `AgentDispatchSettings.LaunchLocation`.
- The chosen `LaunchLocation` threaded into `AgentDispatcher.DispatchAsync` → the
  `TerminalLauncherOptions` handed to `ITerminalLauncher.LaunchAsync` for **that one launch**
  (today the options come solely from settings via `ToLauncherOptions()`).
- Only relevant for **interactive** dispatches — one-off `-p` runs go through the background runner
  with no terminal, so the toggle is hidden/disabled in one-off mode.
- Unit-test the pane-model default/override plumbing; the Terminal.Gui pane itself isn't CI-testable.

## Design

No launch logic changes — `TerminalCommandPlanner` already supports both locations per host as of
#274. This is a pane-model + options-threading change only.

### Phase 1 — model + wiring seam (pure, unit-tested) + request/dispatcher threading

1. **`DispatchPaneModel`** (pure): two small helpers, reused by the pane and unit-tested.
   - `LaunchLocationApplies(AgentSessionMode)` → `sessionMode == Interactive`. The rule the pane
     uses to enable/disable the toggle (one-off has no terminal), and documents why a one-off's
     carried value is ignored downstream.
   - `ToLaunchLocation(bool newTabChecked)` → `NewTab`/`NewWindow`. The toggle's read-on-submit
     mapping, kept pure so it's tested rather than inlined in CI-untestable glue.
2. **`DispatchRequest`**: add `LaunchLocation LaunchLocation = LaunchLocation.NewWindow` (last param,
   backward-compatible default).
3. **`AgentDispatcher.DispatchAsync`**: add `LaunchLocation? launchLocation = null`. When supplied,
   pass `_options with { LaunchLocation = loc }` to `LaunchAsync`; otherwise `_options` unchanged
   (so the background/one-off path and existing callers/tests are untouched). `DispatchBackgroundAsync`
   is the one-off path (no terminal) and is left alone.

### Phase 2 — TUI glue (build-verified) + host threading

4. **`TaskDetailScreen`**: add a `_launchLocationToggle` CheckBox seeded from a new
   `defaultLaunchLocation` ctor param; place it as a new bottom row (bump `DispatchRowsBelowBrowser`
   1→2), add it to `_dispatchControls` (Tab order) and `_promptBox`. Wire `_oneOffToggle.ValueChanged`
   to grey the toggle out in one-off mode (`LaunchLocationApplies`). `MoveDispatchFocus` skips
   disabled controls so Tab still cycles cleanly. `SubmitDispatch` reads the toggle via
   `ToLaunchLocation` into the `DispatchRequest`.
5. **`TodoApp`**: seed the screen with `defaultLaunchLocation: _config.AgentDispatch.LaunchLocation`;
   thread `request.LaunchLocation` into the interactive `agent.DispatchAsync(...)` call (the one-off
   branch returns early and never uses it).

## Tests

- `DispatchPaneModelTests`: `LaunchLocationApplies` (Interactive→true, OneOff→false) and
  `ToLaunchLocation` (both booleans).
- `AgentDispatcherTests`: a fake launcher captures the `TerminalLauncherOptions` it receives; assert
  an explicit `launchLocation` override reaches the launcher, and that omitting it preserves the
  constructor `_options` value (both directions), leaving one-off/background untouched.

## Out of scope / deferred

None — this is the whole optional-stretch slice #255 flagged. No spec/regen (no ClickUp API surface),
no second focusable pane (the toggle is another control on the existing single Dispatch pane).
