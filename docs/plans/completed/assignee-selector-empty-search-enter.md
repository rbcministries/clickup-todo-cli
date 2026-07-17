# Plan: AssigneeSelectorView — Enter in an empty search box must not remove an assignee (#234)

> **Post-#243 location note.** #243 (merged as #258) hoisted the search box / list / `Enter` / `Pick`
> machinery out of `AssigneeSelectorView` into the shared **`SelectorView`** base (over string-id
> `SelectorItem`), with the pure decisions in **`SelectorModel`**. This branch was rebased on top of
> that (via a merge of `main`), so the fix now lives in the **shared base** — `SelectorModel` +
> `SelectorView` — and is inherited by *every* specialization: assignees (#158/#213) **and** the List
> selector (#239). File/line references below are updated accordingly.

## Problem

`SelectorView.OnSearchKey` handles `Enter` in the search box by calling `Pick(_list.SelectedItem ?? 0)`
whenever `_rowItems.Count > 0`. In the **empty-search state** the rows are the current-selection `✓`
rows (topped up from the most-frequent pool), and `ListView.SelectedItem` defaults to `0`, so a
stray `Enter` in the (empty) box picks row 0 → `Toggle` → `Removed` → in `ImmediateApply` mode an
**immediate, unconfirmed server-side removal** of the task's first assignee (via the assignee
specialization used by Quick Updates #158).

The `Enter` shortcut is intended as "add the highlighted **search match** without leaving the
box" — which only makes sense when there is an active query (then every row is an addable,
unselected match, per `SelectorModel.SearchResultRows`). On a blank query it is misapplied to the
`✓` rows.

## Fix (issue option 1 — add-only Enter, made timing-proof)

Keep the decision pure (repo convention: all decisions live in `SelectorModel`).

1. **`SelectorModel.ShouldPickFromSearchBox(string? query, string highlightedId, ISet<string> selectedIds)`**
   — returns `true` only when the query is non-blank **and** the highlighted row is an *addable*
   candidate: a usable (non-blank) id that is **not already selected**. This makes the search box
   strictly add-only.
2. **`SelectorView.OnSearchKey`** `Enter` branch: always set `key.Handled = true` (so `Enter` never
   falls through to a host default — New Task's `Save` button is `IsDefault = true`;
   `QuickUpdatesScreen.OnPaneKey` already expects the selector to mark `Enter` handled), and call
   `Pick(row)` only when the highlighted row is in range and `ShouldPickFromSearchBox` is true.

Removal stays an explicit action: cursor `Down` into the list, then `Enter` on a `✓` row
(`OnListKey`, unchanged).

### Why not gate on query text alone

A first cut gated the pick on the query being non-blank. That's a false proxy: `OnSearchChanged`
arms the debounce timer **without re-rendering**, so for the ~1s debounce window the displayed rows
are still the empty-state `✓` current-selection rows even though the box is already non-blank. An
`Enter` in that window would pick a `✓` row → remove the first assignee — reintroducing #234 through
a timing window. Refusing to pick an **already-selected** row closes the window regardless of
render/debounce state (caught in first-pass review across the overlapping PRs #262/#268/#269).

## Acceptance criteria → coverage

- Enter in an empty search box does not remove a current assignee → `ShouldPickFromSearchBox`
  returns `false` for blank/whitespace query (unit-tested); the View then does not `Pick`.
- Removal via a `✓` row still works → `OnListKey` unchanged; `ShouldPickFromSearchBox` is not on
  that path.
- Adding via a searched match with Enter still works → `ShouldPickFromSearchBox` returns `true`
  for a non-blank query with an unselected highlighted id (unit-tested).
- Covered by a test so it can't regress → new `SelectorModelTests` cases; applies to both hosts (and
  the List selector) because the fix is in the shared base.

## E2E (tui-validate)

Adopted from the overlapping PR #262: `qu_assignees_empty_enter_check.py`, gated on a new
`E2E_QU_SEED_ASSIGNEE=1` seam in the fake backend (`tests/ClickUpTodo.Tui.E2E/Program.cs`) that seeds
every task with a current assignee so the Assignees empty state has a removable `✓` row 0. It asserts:
the empty-box `Enter` retains the `✓`; a **type-then-quick-`Enter` inside the debounce window** retains
the `✓` (the timing regression); and `Down`+`Enter` on the `✓` row still removes — with the check
verified to fail on both pre-fix behaviours (the original empty-box remove and the query-text-gate
debounce remove).

## Out of scope

No Kiota regen, no spec change, no new focusable pane. The empty-state "start with no selection"
alternative (issue option 2) is not taken — option 1 is the minimal change matching the documented
"add without leaving the box" intent.
