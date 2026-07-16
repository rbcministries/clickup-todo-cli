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

## Approach (recommended option from the issue)

Make the search-box `Enter` shortcut fire **only when a search is active** (a
non-blank query). In that state the rows are always *unselected, addable* search
matches (`AssigneeSelectorModel.SearchResultRows` excludes `selectedIds`), so
`Enter` only ever **adds** — never removes. In the empty-search state `Enter`
becomes a no-op; removal stays an explicit action reached by arrowing `Down` into
the list and pressing `Enter` there (`OnListKey`, unchanged).

Why a no-op rather than swallowing everything: this is the minimal-diff change —
`Enter` is left to fall through exactly as it already did when `_rowPeople` was
empty. Both hosts treat a bubbled `Enter` from the selector as a no-op today:
Quick Updates' commit branch is keyed by `sender` identity (`_statusList` /
`_priorityList`), and New Task's key handler only acts on `Esc`/`F1`.

## Design

- New pure predicate `AssigneeSelectorModel.ShouldPickFromSearchBox(string? query,
  int rowCount)` = `rowCount > 0 && !string.IsNullOrWhiteSpace(query)`. Keeps the
  decision unit-testable, mirroring `ShouldRunSearch`.
- `OnSearchKey` calls it to gate the `Pick`. `OnListKey`'s `Enter` (removal via a
  `✓` row) is untouched.
- Update the Quick Updates `OnPaneKey` doc comment: a stray empty-box `Enter` may
  now bubble, but the identity-keyed commit gate makes it a no-op there.

## Tests

`AssigneeSelectorModelTests` — cover `ShouldPickFromSearchBox`:
- non-blank query + rows → picks (adds).
- blank / whitespace-only query → no-op (the #234 removal path).
- non-blank query but zero rows → no-op.

The View glue itself is CI-untestable (Terminal.Gui). The E2E
`qu_assignees_check.py` already exercises the add path *from an active search*
(types "grac", then `Enter`) and removal via `Down`+`Enter` in the list, so it
keeps passing unchanged and continues to guard the fixed behaviour.

## Acceptance criteria (from #234)

- [x] `Enter` in an empty search box does not remove a current assignee.
- [x] Removal via a `✓` row (arrow into the list, then `Enter`) still works.
- [x] Adding via a searched match with `Enter` still works.
- [x] Covered by a test so it can't regress; applies to both hosts (fixed in the
      shared component).
