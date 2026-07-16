# AssigneeSelectorView: Enter in an empty search box must not remove the first assignee (#234)

## Problem

`AssigneeSelectorView.OnSearchKey`'s `Enter` branch picks the highlighted row
whenever any rows are shown:

```csharp
case KeyCode.Enter:
    if (_rowPeople.Count > 0)
    {
        key.Handled = true;
        Pick(_list.SelectedItem ?? 0);   // row 0 in the empty state = first current assignee (a ✓ row)
    }
```

In the **empty-search** state the list's first rows are the current assignees
(`✓` rows), and `ListView.SelectedItem` defaults to `0`. So pressing `Enter`
while focused in an *empty* search box of a task that already has assignees runs
`Pick(0)` → `Toggle` → `Removed` → an immediate server remove of that assignee
(in `ImmediateApply` mode). The comment's intent ("add without leaving the box")
only holds when the rows are *addable search matches*, not the current-assignee
`✓` rows.

This affects both hosts of the reusable component: Quick Updates (#158) and New
Task (#213), so the fix belongs in the component, once.

## Approach

Make the search-box `Enter` shortcut fire **only when the shown rows are settled
search results**. Those rows are always *unselected, addable* matches
(`AssigneeSelectorModel.SearchResultRows` excludes `selectedIds`), so `Enter`
only ever **adds** — never removes. In the empty-search state `Enter` becomes a
no-op; removal stays an explicit action reached by arrowing `Down` into the list
and pressing `Enter` there (`OnListKey`, unchanged).

**Gate on render state, not the query text.** A first cut gated on a non-blank
`_search.Text`, but the rows lag the query text during the ~1s type-ahead
debounce: `OnSearchChanged` only arms the debounce timer — it does **not**
re-render — so between a keystroke and the debounce firing, the query is
non-blank while the rows are still the empty-state `✓` rows. A query-text gate
would still remove the first assignee if the user types a name and presses
`Enter` before the debounce resolves. Tracking whether the shown rows are search
results closes that window.

Why a no-op rather than swallowing everything: `Enter` is left to fall through
exactly as it already did when `_rowPeople` was empty. Both hosts treat a bubbled
`Enter` from the selector as a no-op today: Quick Updates' commit branch is keyed
by `sender` identity (`_statusList` / `_priorityList`), and New Task's key
handler only acts on `Esc`/`F1`.

## Design

- New `bool _showingSearchResults` field on the View: `true` only after a settled
  search renders results (`RunSearch` / `RenderCurrent` non-blank branch),
  `false` in `RenderEmptyState` (i.e. also during the debounce window, since no
  render happens then). Touched on the UI thread only.
- New pure predicate `AssigneeSelectorModel.ShouldPickFromSearchBox(bool
  showingSearchResults, int rowCount)` = `showingSearchResults && rowCount > 0`.
  Keeps the decision unit-testable, mirroring `ShouldRunSearch`.
- `OnSearchKey` calls it to gate the `Pick`. `OnListKey`'s `Enter` (removal via a
  `✓` row) is untouched.
- Update the Quick Updates `OnPaneKey` doc comment: a stray empty-box `Enter` may
  now bubble, but the identity-keyed commit gate makes it a no-op there.

## Tests

`AssigneeSelectorModelTests` — cover `ShouldPickFromSearchBox`:
- settled search + rows → picks (adds).
- not showing search results (empty state *or* unsettled debounce) → no-op (the
  #234 removal path).
- settled search but zero rows → no-op.

The View glue itself is CI-untestable (Terminal.Gui). E2E via `tui-validate`:
- `qu_assignees_check.py` (unchanged) exercises the add path *from a settled
  search* (types "grac", waits the debounce, `Enter`) and removal via
  `Down`+`Enter` in the list — keeps passing.
- new `qu_assignees_empty_enter_check.py` (env-gated seed of a current assignee)
  asserts: empty-box `Enter` retains the `✓`; a type-then-quick-`Enter` **inside
  the debounce window** retains the `✓`; and `Down`+`Enter` on the `✓` row still
  removes. Verified it fails on the pre-fix behaviour.

## Acceptance criteria (from #234)

- [x] `Enter` in an empty search box does not remove a current assignee.
- [x] Removal via a `✓` row (arrow into the list, then `Enter`) still works.
- [x] Adding via a searched match with `Enter` still works.
- [x] Covered by a test so it can't regress; applies to both hosts (fixed in the
      shared component).
