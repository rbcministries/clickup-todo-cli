# Mouse/UX (A): double-click a task row opens Task Detail (#286)

Part of the Mouse interaction epic (#283). First mouse handler in the app — mouse
is greenfield (`grep Mouse src/` returns nothing). Produces the shared **row
hit-test helper** that B (fold-arrow click, #287) and F (Task Tree tab, #291)
reuse.

## Goal

Double-clicking a task row in the main list opens that task's Task Detail screen —
the mouse equivalent of Enter. Header/spacer rows and empty space no-op. Single
click still only selects (native `ListView` behaviour); Enter is unchanged.

## Current state (verified)

- Main list is one `ListView` `_list` (`Tui/TodoApp.cs`), built in `Build()`. Rows
  are header/spacer/task kinds in row-parallel arrays `_rows`/`_kinds`; the task on
  a row is `_rows[i]` (null on header/spacer).
- Keyboard opens detail: `OnListKey` maps Enter → `OpenDetail()` → `OpenTaskDetail(id)`.
  `OpenDetail()` reads the selected row via `CurrentTask()` (`_rows[SelectedItem]`)
  and guards `ActiveScreen is not null`.
- No mouse handlers exist anywhere in `src/`.
- Terminal.Gui 2.4.10: `View.MouseEvent` raises `EventHandler<Terminal.Gui.Input.Mouse>`;
  the args carry `Flags` (`MouseFlags`, incl. `LeftButtonDoubleClicked`) and a
  viewport-relative `Position` (`Point?`). `ListView.Viewport.Y` is the scroll
  offset (index of the topmost displayed row).

## Design

### Phase 1 — pure hit-test helper + unit tests

`Services/RowHitTester.cs` (pure, no Terminal.Gui, matching `SubtaskArranger`):

- `RowIndexAt(int clickY, int scrollOffset, int rowCount)` → absolute row index,
  or `-1` when the click is above the first row or below the last (short list).
- `TaskAt(int clickY, int scrollOffset, IReadOnlyList<TaskItem?> rows)` → the
  `TaskItem` on that row, or `null` for a header/spacer/out-of-range row — mirroring
  `CurrentTask()` so a double-click on a non-task row no-ops like Enter.

Unit tests: task row (with scroll offset), header/spacer rows (null), negative Y,
Y below the last row, empty list.

### Phase 2 — wire the double-click in TodoApp + E2E validation

- `Build()`: `_list.MouseEvent += OnListMouse`.
- `OnListMouse`: act only on `MouseFlags.LeftButtonDoubleClicked` with a `Position`;
  guard `ActiveScreen is null`; resolve `RowHitTester.TaskAt(pos.Y, _list.Viewport.Y,
  _rows)`; if a task, mark `e.Handled = true` and `OpenTaskDetail(task.Id)`. Every
  other mouse event is left unhandled, so single-click select and drag-scroll are
  untouched.
- Extend the E2E harness (`tests/ClickUpTodo.Tui.E2E/`) with SGR-1006 mouse injection
  and a `double_click_check.py` scenario: double-click a task row, assert the detail
  screen opens; double-click a header row, assert it does not. The TG ansi driver
  enables mouse reporting on boot, so the harness only has to emit the click bytes.

## Invariants

- **No second focusable pane (#3/#38)** — no new views; a handler on the existing list.
- **Mouse is additive** — Enter and single-click keep working exactly as today.
- **Bare letters reserved for type-ahead (#12)** — no keyboard change.
- Helper stays action-agnostic (row → task) so B/F layer on it.

## Deferred

Nothing functional. If SGR mouse injection proves flaky in the PTY harness within
this session, the `double_click_check.py` scenario is tracked as a follow-up rather
than blocking the feature (precedent: #333), and manual verification is described in
the PR.
