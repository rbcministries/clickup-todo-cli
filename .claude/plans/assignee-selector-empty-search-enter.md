# Plan: AssigneeSelectorView — Enter in an empty search box must not remove an assignee (#234)

## Problem

`AssigneeSelectorView.OnSearchKey` (`src/ClickUpTodo/Tui/AssigneeSelectorView.cs:164-172`)
handles `Enter` in the search box by calling `Pick(_list.SelectedItem ?? 0)` whenever
`_rowPeople.Count > 0`. In the **empty-search state** the rows are the current-assignee `✓`
rows (topped up from the most-frequent pool), and `ListView.SelectedItem` defaults to `0`, so a
stray `Enter` in the (empty) box picks row 0 → `Toggle` → `Removed` → in `ImmediateApply` mode an
**immediate, unconfirmed server-side removal** of the task's first assignee.

The `Enter` shortcut is intended as "add the highlighted **search match** without leaving the
box" — which only makes sense when there is an active query (then every row is an addable,
unselected match, per `SearchResultRows`). On a blank query it is misapplied to the `✓` rows.

## Fix (issue option 1 — add-only Enter)

Keep the decision pure (repo convention: all decisions live in `AssigneeSelectorModel`).

1. **`AssigneeSelectorModel.ShouldPickFromSearchBox(string? query, int rowCount)`** — returns
   `true` only when `rowCount > 0` **and** the query is non-blank. This is the sole new logic.
2. **`AssigneeSelectorView.OnSearchKey`** `Enter` branch: always set `key.Handled = true` (so
   `Enter` never falls through to a host default — New Task's `Save` button is `IsDefault = true`;
   QuickUpdatesScreen's `OnPaneKey` already expects the selector to mark `Enter` handled), and only
   call `Pick(_list.SelectedItem ?? 0)` when `ShouldPickFromSearchBox` is true. On a blank box it is
   a swallowed no-op.

Removal stays an explicit action: cursor `Down` into the list, then `Enter` on a `✓` row
(`OnListKey`, unchanged).

## Acceptance criteria → coverage

- Enter in an empty search box does not remove a current assignee → `ShouldPickFromSearchBox`
  returns `false` for blank/whitespace query (unit-tested); the View then does not `Pick`.
- Removal via a `✓` row still works → `OnListKey` unchanged; `ShouldPickFromSearchBox` is not on
  that path.
- Adding via a searched match with Enter still works → `ShouldPickFromSearchBox` returns `true`
  for a non-blank query with rows (unit-tested).
- Covered by a test so it can't regress → new `AssigneeSelectorModelTests` cases; applies to both
  hosts because the fix is in the shared component.

## Out of scope

No Kiota regen, no spec change, no new focusable pane, no TUI-validate change required (the pure
decision is unit-tested; the View glue is the one-line guard). The empty-state "start with no
selection" alternative (issue option 2) is not taken — option 1 is the minimal change matching the
documented "add without leaving the box" intent.
