# D1 — Dispatch pane shell + `A` → `Ctrl+A` (#93)

Part of the #90 agent-dispatch epic. Establishes the **container + focus/scroll
navigation model** for the detail view's dispatch UI that #94 (D2), #95 (D3) and
#97 (D5) slot their controls into. Ships stub/placeholder controls so the layout
and focus order are real, but wires **only the prompt** into dispatch — behaviour
is byte-for-byte identical to the pre-#93 single-line prompt.

## Acceptance criteria (from the issue)

1. **Trigger `A` → `Ctrl+A`.** Bare-letter trigger replaced with the `Ctrl+A`
   chord (mirrors the `Ctrl+B` pattern), pre-empted before the read-only
   `TextView` sees it. Update the three help strings: detail footer,
   `HelpScreen`, any main-list hint that references dispatch (there is none — the
   main list has no dispatch hint; dispatch is detail-view only).
2. **Expand `_promptBox` into a Dispatch pane** — a taller bottom-anchored
   `FrameView` hosting, top-to-bottom: prompt field, then stub controls for the
   one-off/interactive toggle (#94), working-dir field (#95), and post-to-Comments
   toggle (#97).
3. **Tab / Shift+Tab** cycle the dispatch controls (with wraparound) **while the
   pane is open**; when closed, `Tab` reverts to cycling the detail tabs.
4. **PgUp / PgDn** keep scrolling the active tab *above* the pane while it's open
   (routed to the front-most `_scrollTargets` entry, not trapped in the pane).
5. **Enter** submits (raises `AgentDispatchRequested` carrying a new
   `DispatchRequest` record); **Esc** cancels and returns focus to the current
   pane.
6. Pane grows from `Height = 3`, anchored bottom via `Pos.AnchorEnd(n)`, sized to
   fit the controls, and **degrades gracefully** on short terminals — the prompt
   field stays visible; the bottom stub controls clip first.

## Constraints

- Stay within the single already-open screen — no nested run-loop / second
  toplevel (the #26 design note). The dashboard's single sectioned `ListView`
  (#3) is untouched; this pane lives inside the detail screen, which already has
  focusable panes.
- Keep pure/composable logic (focus order, key-routing decisions, pane sizing)
  unit-testable; verify the Terminal.Gui surface by build + reasoning (+ a
  `tui-validate` regression pass).
- `Generated/` is off-limits; no spec/regen needed (no new API surface).

## Design

### Pure model — `Tui/Screens/DispatchPaneModel.cs`

Mirrors the `StatusPickerModel` pure-glue split. No Terminal.Gui dependency, so
it's fully unit-tested:

- `enum PaneKey { Enter, Escape, Tab, BackTab, PageUp, PageDown, Other }` — the
  glue classifies a Terminal.Gui `Key` into this.
- `enum PaneAction { Submit, Cancel, FocusNext, FocusPrevious,
  ScrollUnderlyingPageUp, ScrollUnderlyingPageDown, PassThrough }`.
- `PaneAction Route(PaneKey)` — the routing table (independent of which control
  has focus: Enter always submits, Esc cancels, Tab/BackTab move focus, PgUp/PgDn
  scroll the tab above, everything else passes through to the focused control so
  typing / Space-toggle still work).
- `int NextFocus(int current, int count, bool forward)` — wraparound focus cycle
  (same math as the detail-tab cycle).
- `int PreferredHeight(int controlCount)` — one row per control + 2 border rows.
- `int ClampHeight(int preferred, int availableHeight, int minTabRows)` — caps
  the height so at least `minTabRows` of the tab above stay visible, but never
  below a 3-row minimum (top border + prompt row + bottom border) so the prompt
  is never clipped.

### Event seam — `Tui/Screens/DispatchRequest.cs`

`public sealed record DispatchRequest(string Prompt);` — today only the prompt;
#94/#95/#97 extend this record so the `AgentDispatchRequested` event signature
stays stable. `TaskDetailScreen.AgentDispatchRequested` becomes
`EventHandler<DispatchRequest>`; `TodoApp` reads `req.Prompt`.

### TUI glue — `Tui/Screens/TaskDetailScreen.cs`

- Build the pane `FrameView` (title "Dispatch to Claude") with the prompt
  `TextField` plus the three stub controls, each on its own row with a describing
  `Label`; stubs are labelled "(coming soon)" and their values are **not** read
  into `DispatchRequest` yet (wired in #94/#95/#97), so zero-config dispatch is
  unchanged.
- `_dispatchControls` = ordered `[prompt, oneOff, workingDir, postToComments]`.
- One `OnDispatchKey` handler on every dispatch control: classify → `Route` →
  act (submit / cancel / `NextFocus`+`SetFocus` / `InvokeCommand(Command.PageUp
  /PageDown)` on the front-most scroll target). `PassThrough` leaves the key for
  the focused control.
- `Ctrl+A` shows the pane and focuses the prompt; `Esc`/submit hide it and return
  focus to the current tab's scroll target.
- On show, compute the height from `Viewport.Height` via `ClampHeight` and set
  `Height` + `Y = Pos.AnchorEnd(height)`.

## Tests — `tests/ClickUpTodo.Tests/DispatchPaneModelTests.cs`

- `Route` maps each `PaneKey` to the expected `PaneAction` (incl. `Other` →
  `PassThrough`).
- `NextFocus` wraps both directions, is a no-op-safe for count ≤ 0, single
  element stays put.
- `PreferredHeight` = count + 2.
- `ClampHeight`: fits when room; caps to keep `minTabRows`; never below 3;
  handles tiny/again-degenerate terminals.

## Manual / TUI verification (per the repo's TUI rule)

Build + reasoning + a `tui-validate` regression pass (latency + A/B render of the
dashboard, which this doesn't touch). To exercise manually: open a task (Enter),
press `Ctrl+A` — the pane opens with the prompt focused; Tab/Shift+Tab cycle the
four controls; PgUp/PgDn scroll the tab above; type a prompt + Enter dispatches
(interactive `claude`, unchanged); Esc cancels. `A` alone no longer triggers.

## Deferred / follow-up (tracked by existing issues)

- One-off vs interactive toggle behaviour → **#94**
- Working-directory control + file-tree browser → **#95**
- Post-results-to-Comments toggle → **#97**
