# Mouse/UX (C): click an item in Quick Updates applies it (#288)

Part of the Mouse interaction epic (#283). Sibling of A (#286, double-click a
main-list row → Task Detail) and B (#287, fold-arrow click). No hard dependency
on A/B: Quick Updates panes are their own screen, so this wires clicks to the
already-built commit/apply callbacks rather than the main list's row hit-test.

## Goal

Clicking a row in any Quick Updates pane has the same effect as selecting it and
pressing Enter:

- **Status / Priority:** a single left-click selects that row **and** applies it
  (optimistic + revert), reconciling the `✓`. Re-clicking the current value
  flashes/no-ops — identical to Enter (the `CommitStatus`/`CommitPriority`
  "unchanged" guard).
- **Assignees:** a single left-click toggles that person — clicking an unselected
  candidate adds them, clicking a `✓` row removes them — through the existing
  `ImmediateApply` optimistic+revert path. Identical to the keyboard toggle.

Clicking a pane focuses it, so subsequent Tab/arrow keys operate there. Clicking
empty space beneath a short list (or above the first row) resolves to no row and
no-ops — it must never apply the nearest row. Keyboard Enter/toggle paths are
unchanged; mouse is strictly additive.

## Verified current state (repo, 2.4.10)

- `Tui/Screens/QuickUpdatesScreen.cs` stacks three focusable panes in `_panes`
  (`:128`): `_statusList` / `_priorityList` (`ListView`) and `_assignees`
  (`AssigneeSelectorView`, a composite in `SelectorMode.ImmediateApply`). **No
  mouse handlers exist.**
- Status/Priority (deferred commit, #157): `OnPaneKey` (`:161`) maps Enter →
  `CommitStatus()` (`:192`) / `CommitPriority()` (`:207`). Both read
  `SelectedItem`, apply the "unchanged → flash, no-op" guard, and raise
  `StatusCommitted`/`PriorityCommitted`; the host applies + reconciles the `✓`.
- Assignees: `SelectorView.Pick(int rowIndex)` (`:229`) maps a row to its item and
  runs the pure `SelectorModel.Toggle` decision → add/remove through the immediate
  apply path. `Pick` already bounds-checks (`rowIndex < 0 || >= _rowItems.Count`
  → no-op). `_list` and `Pick` are private to `SelectorView`.
- Terminal.Gui 2.4.10: `View.MouseEvent` raises `EventHandler<Terminal.Gui.Input.Mouse>`;
  args carry `Flags` (`MouseFlags`, incl. `LeftButtonClicked`) and a viewport-
  relative `Position` (`Point?`). `ListView.Viewport.Y` is the scroll offset.

## Design

### Phase 1 — pure row-hit mapping + unit tests

`QuickUpdatesModel.RowIndexAt(int clickY, int scrollOffset, int rowCount)` → the
absolute row a viewport-relative click lands on (`scrollOffset + clickY`), or
`-1` above the first row / below the last (empty space under a short list). Pure,
no Terminal.Gui — the risky part of the Status/Priority path, since `CommitStatus`
applies whatever `SelectedItem` points at, so the click must resolve to the real
row or explicitly no-op. Unit-tested (unscrolled, scrolled, below-last, negative,
empty).

(The selector reuses its own `Pick` bounds guard — see Phase 2 — so it needs no
new pure helper; the add/remove decision is already `SelectorModel.Toggle`,
covered by `SelectorModelTests`.)

### Phase 2 — wire the click (view glue, CI-untestable)

- **`QuickUpdatesScreen`:** `_statusList.MouseEvent` / `_priorityList.MouseEvent`
  → a shared `OnListClick(e, list, rowCount, commit)`: act only on
  `LeftButtonClicked` with a `Position`; resolve the row via
  `QuickUpdatesModel.RowIndexAt`; on `-1` leave unhandled (native select stays);
  otherwise `e.Handled = true`, focus the list, set `SelectedItem`, and invoke
  `CommitStatus`/`CommitPriority` (which keep the unchanged-guard).
- **`SelectorView` (base):** `_list.MouseEvent += OnListMouse` → on
  `LeftButtonClicked` with a `Position`, resolve `row = _list.Viewport.Y + pos.Y`,
  bounds-check against `_rowItems.Count` (mirrors `Pick`'s guard), then
  `e.Handled = true`, `_list.SetFocus()`, `Pick(row)`. Additive to every selector
  (assignees here; List selector #239 and New Task get it for free), routing the
  click through the existing `Toggle` → optimistic-apply path — no new mutation
  code.

Guards inherited unchanged: not-mine / no-list Quick Updates guards live in the
host's `ApplyStatus`/`ApplyPriority`/`ApplyAssigneeAsync`, so a click can't apply
where a keypress currently can't. `LeftButtonDoubleClicked` is left unhandled, so
a double-click degrades to two single-clicks (second → unchanged/flash).

### Phase 3 — E2E validation (`tui-validate`)

`tests/ClickUpTodo.Tui.E2E/qu_click_check.py`: boot, `Space` → Quick Updates,
inject SGR-1006 clicks (the ansi driver enables mouse reporting on boot):

- click a Status row → its `✓` moves there (apply round-trips via the fake
  backend), and re-clicking it flashes "unchanged";
- click a Priority row → its `✓` moves there;
- click an Assignees candidate → `✓` appears (add), click the `✓` row → it clears
  (remove);
- click empty space beneath the Status list → no `✓` change (no-op).

Existing `quickupdates` / `qu_assignees` / `qu_from_detail` /
`qu_assignees_empty_enter` / `foreign_quickupdates` checks must still pass. Per
the #333/#286 precedent, if SGR injection proves flaky in the PTY harness within
the session, the scenario is tracked as a follow-up rather than blocking the
feature, with manual verification described in the PR.

## Invariants preserved

- **No second focusable pane (#3/#38)** — no new views; handlers on the existing
  Status/Priority `ListView`s and the selector's own list.
- **Bare letters reserved for type-ahead (#12)** — no keyboard change at all.
- **Mouse never replaces the keyboard** — Enter/toggle paths are byte-for-byte
  unchanged; only new `MouseEvent` handlers are added.
- **Generated client / curated spec untouched** — no ClickUp API surface change;
  clicks reuse the existing commit/apply callbacks.

## Deferred

- Quick Updates *List* pane click (its own pane) rides on #242 (PR #339); once
  that lands its selector inherits `SelectorView`'s new click-to-pick automatically.
- Help-bar buttons (#289) and shortcut standardization (#290) are separate
  sub-issues.
