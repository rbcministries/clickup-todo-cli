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

## Fix (issue option 1 — add-only Enter, made timing-proof)

Keep the decision pure (repo convention: all decisions live in `AssigneeSelectorModel`).

1. **`AssigneeSelectorModel.ShouldPickFromSearchBox(string? query, long highlightedId, ISet<long> selectedIds)`**
   — returns `true` only when the query is non-blank **and** the highlighted row is an *addable*
   candidate: a usable id (`> 0`) that is **not already selected**. This makes the search box strictly
   add-only.
2. **`AssigneeSelectorView.OnSearchKey`** `Enter` branch: always set `key.Handled = true` (so
   `Enter` never falls through to a host default — New Task's `Save` button is `IsDefault = true`;
   QuickUpdatesScreen's `OnPaneKey` already expects the selector to mark `Enter` handled), and call
   `Pick(row)` only when the highlighted row is in range and `ShouldPickFromSearchBox` is true.

Removal stays an explicit action: cursor `Down` into the list, then `Enter` on a `✓` row
(`OnListKey`, unchanged).

### Why not gate on query text alone

A first cut gated the pick on the query being non-blank. That's a false proxy: `OnSearchChanged`
arms the debounce timer **without re-rendering** (`AssigneeSelectorView.cs:305-318`), so for the
~1s debounce window the displayed rows are still the empty-state `✓` current-assignee rows even
though the box is already non-blank. An `Enter` in that window would pick a `✓` row → remove the
first assignee — reintroducing #234 through a timing window. Refusing to pick an **already-selected**
row closes the window regardless of render/debounce state (verified in first-pass review of PR #269).

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
