# Task Detail: bare ↑/↓ scroll the pane / move the tree row (#452)

A bug fix, and the gate for the Task Checklists epic (#453 → **C** #456, whose
tab is row-selection-driven and unusable until bare `↑`/`↓` work in Task Detail).

## Problem

In Task Detail the bare arrow keys `↑`/`↓` are inert on every tab:

- **Stream / Description / Comments** (the read-only `DetailPaneView` text panes)
  — `↑`/`↓` do not scroll the body; `PgUp`/`PgDn` do.
- **Task Tree tab** — `↑`/`↓` do not move the selected row.

## Root cause (confirmed against Terminal.Gui 2.4.10 source + a PTY probe)

Key dispatch (`View.NewKeyDownEvent`) recurses to the focused **leaf** first,
raising its `KeyDown` event (→ the screen's `OnKey`), then its own key bindings,
and only then bubbles up to ancestors (the `Tabs` control). Focus is genuinely on
the pane/list (that is why `PgUp`/`PgDn` reach it). Two distinct mechanisms then
swallow the arrow:

1. **Text panes.** `TextView` binds `CursorUp/Down` → `Command.Up/Down` →
   `ProcessMoveUp/Down` → move the **caret** one line. In a read-only pane the
   caret is invisible and the viewport only scrolls once the caret leaves it, so
   the first *viewport-height* presses look completely dead. `PgUp`/`PgDn` map to
   `Command.PageUp/Down`, which scroll the viewport a page at once, so they work.

2. **Task Tree `ListView`.** Its handler is
   `AddCommand(Command.Down, ctx => RaiseActivating(ctx) == true || MoveDown())`.
   `RaiseActivating` bubbles the command to the superview when unhandled, and the
   superview is `NavSafeTabs`, whose `Command.Up/Down/Left/Right` are registered
   as inert **`() => true`** (the crash guard against 2.4.10's
   `Tabs.SelectNextTab`/`SelectPreviousTab`). So `RaiseActivating` returns `true`
   and short-circuits `MoveDown()` — the selection never moves.

`NavSafeTabs` must stay: reverting it re-introduces the tab-boundary crash that
`tab_boundary_check.py` (check 7) pins. The fix therefore claims bare `↑`/`↓`
**before** either mechanism, in the screen's own `OnKey` — which fires on the
focused pane/list ahead of its bindings and ahead of the bubble to `NavSafeTabs`.

## Approach

Mirror the codebase's pure-glue split (`DetailScrollModel`, `DetailTabNav`):
put the clamping arithmetic in the pure model (unit-tested) and keep only the
Terminal.Gui wiring in the screen.

### 1. Pure model — extend `DetailScrollModel` (`Tui/Screens/DetailScrollModel.cs`)

- `MaxTop(lineCount, viewportHeight)` → `max(0, lineCount - max(1, height))` —
  the existing `MaxTopRow` arithmetic, now shared and tested.
- `NextTop(currentTop, viewportHeight, lineCount, delta)` → the clamped next top
  row (`Clamp(currentTop + delta, 0, MaxTop(...))`).
- `NextIndex(currentIndex, count, delta)` → the clamped next list selection
  (`Clamp(currentIndex + delta, 0, count - 1)`; returns `currentIndex` when the
  list is empty).

All three are boundary no-ops (return the input) at the top/bottom edge — so the
glue can consume the key unconditionally and never fall through to `NavSafeTabs`.

### 2. Glue — `TaskDetailScreen.OnKey`

Add one block, placed after the composer/editor guard and the tree Enter/F6 +
link-focus `Tab` handlers, before the `Ctrl`-chord blocks:

- Match a **bare** `CursorUp`/`CursorDown` (no `Ctrl`/`Shift`/`Alt`), and only
  while the Dispatch prompt is closed (`!_promptBox.Visible` — its dir-browser
  owns bare `↑`/`↓`, exactly like the sibling command blocks).
- Resolve the front-most tab's scroll target (`_scrollTargets[current]`, the same
  seam `ScrollActiveTab`/`ActiveTextPane` use):
  - **`ListView`** (Task Tree) → set `SelectedItem = NextIndex(...)`; the setter
    calls `EnsureSelectedItemVisible()`, so the highlight moves and the list
    scrolls to keep it in view.
  - **`TextView`** (the three text panes *and* the Other tab's fields body) →
    set `Viewport.Y = NextTop(...)`, the one-line counterpart of the
    `SetBodyKeepingScroll` viewport write.
- `key.Handled = true` in every case, so a boundary press is a genuine no-op that
  stays on the tab (never a tab switch, never the `NavSafeTabs` crash path).

No change to `NavSafeTabs`, `CycleTab`, focus handling, or the single-pane model.

## Acceptance criteria (from #452)

- Stream/Description/Comments: `↑`/`↓` scroll one line; `PgUp`/`PgDn` unchanged.
- Task Tree: `↑`/`↓` move the selection; `Enter` navigates as before.
- At a content boundary the arrow is a no-op: no tab switch, no focus jump to a
  tab header, no crash.
- Identical in single-task launch mode (4 tabs) and dashboard detail (5 tabs).
- `dotnet test` green first, then `tui-validate`: `detail_check.py`,
  `tree_tab_check.py`, `tab_boundary_check.py` still green, plus a new
  `detail_arrow_check.py` that fails on today's code — a bare `↓`/`↑` changes the
  rendered text-pane body and moves the tree selection highlight.

## Tests

- **Unit** (`DetailScrollModelTests`): `MaxTop`, `NextTop`, `NextIndex` — mid,
  both edges, empty/degenerate (height ≥ lineCount, count 0/1), delta ±1.
- **E2E** (`detail_arrow_check.py`, new; scenario reuses `E2E_TREE=1`): open the
  detail at a short terminal so the Stream overflows; assert a bare `↑` scrolls
  the body (visible text changes) and `↓` scrolls back; cycle to the Task Tree
  tab and assert a bare `↓` moves the focus-highlighted row down one and `↑`
  moves it back. Fails on pre-fix code (the exact regression #452 reports).
