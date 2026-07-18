# Task Detail (A): rebind tab navigation to Ctrl+→ / Ctrl+← (#315)

Part of the Task Detail & Comments UX epic (#313). Foundation for **E** (#319,
in-text link focus traversal), which needs bare `Tab` / `Shift+Tab` free.

## Problem

Task Detail switches tabs on bare `Tab` / `Shift+Tab`
(`TaskDetailScreen.OnKey` → `CycleTab`). The in-text link work (#319 / **E**)
wants bare `Tab` to traverse links inside a pane, so tab-switching has to move
onto a chord. `Ctrl+→` / `Ctrl+←` are unused on this screen (`Ctrl+PgUp/PgDn`
already drive Stream activity order), so they are the natural home — matching the
main list's `Ctrl+→/←` (Expand-All / Collapse-All) shape, on a different screen,
so there is no runtime collision.

## Approach

Mirror the existing pure-glue split (`DispatchPaneModel`): factor the key→action
decision into a small, unit-testable pure model and keep only the Terminal.Gui
wiring in the screen.

### 1. Pure model — `DetailTabNav` (`Tui/Screens/DetailTabNav.cs`)

- `NavKey { CtrlRight, CtrlLeft, Other }` — the chord vocabulary the glue
  classifies a Terminal.Gui `Key` into.
- `NavAction { CycleForward, CycleBackward, None }`.
- `Route(NavKey, bool promptOpen)` → `NavAction`. Inert (`None`) while the
  Dispatch prompt is open: its dir-browser owns bare `←`/`→` and its text fields
  own cursor movement, and — crucially — this preserves the pre-#315 behaviour
  where the Dispatch pane consumed `Tab` before it could reach the tab cycle.
- `NextTab(current, count, forward)` — wraparound tab index, delegating to the
  existing `DispatchPaneModel.NextFocus` ("the same wraparound the detail-tab
  cycle uses", per that model's doc comment) so both share one implementation.

### 2. Glue — `TaskDetailScreen.OnKey`

- Add a `ClassifyTabNav(Key)` helper: `Ctrl`+`CursorRight` → `CtrlRight`,
  `Ctrl`+`CursorLeft` → `CtrlLeft`, else `Other` (same `key.IsCtrl` +
  `KeyCode & ~KeyCode.CtrlMask` shape as the sibling Ctrl chords).
- Route it; on `CycleForward`/`CycleBackward` set `key.Handled = true` and call
  `CycleTab(forward: …)`.
- Remove `case KeyCode.Tab:` from the trailing `switch` so the screen stops
  consuming bare `Tab` / `Shift+Tab` (they fall through to Terminal.Gui default
  focus until **E** claims them — the accepted interim per the issue's open
  question).
- Refactor `CycleTab` to compute its next index via `DetailTabNav.NextTab`, so
  the unit-tested math is the real code path.
- The composer / description-editor overlays already early-return at the top of
  `OnKey` (they own `Tab` for their own control cycling); the Dispatch pane's
  `Tab` cycling is via `OnDispatchKey` and unaffected. No second focusable pane
  is introduced.

### 3. Help footer — `HelpLine.Detail`

`new("Tab", "switch tab")` → `new("Ctrl+←/→", "switch tab")`.

## Tests

- **Unit** (`DetailTabNavTests`): `Route` maps each chord to its action;
  `promptOpen` forces `None`; `Other` → `None`; `NextTab` wraps both directions
  and tolerates a non-positive count. CI-verifiable, no terminal.
- **tui-validate** (`detail_check.py`): the tab-cycle driver switches from `\t`
  to `Ctrl+→` (`\x1b[1;5C`) / `Ctrl+←` (`\x1b[1;5D`), keeping the A/B diffed-vs-
  stock render parity assertion. The Terminal.Gui glue itself is verified by
  build + reasoning per the repo's TUI rule.

## Acceptance criteria (from #315)

- `Ctrl+→` / `Ctrl+←` cycle tabs with wraparound and move focus into the new
  tab's scroll target, exactly as `CycleTab` does today; bare `Tab` /
  `Shift+Tab` no longer switch tabs.
- Help footer reflects the new keys.
- `dotnet test` green; `tui-validate` confirms tab cycling on the new chord and
  that composer/editor Tab-focus still works.

## Out of scope

- Bare `Tab` / `Shift+Tab` in-pane link traversal — that is **E** (#319). Until
  it lands, bare `Tab` falls through to default focus behaviour (accepted
  interim).
