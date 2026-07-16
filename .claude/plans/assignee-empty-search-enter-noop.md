# Plan — AssigneeSelectorView: Enter in an empty search box must not remove the first assignee (#234)

## Problem

In `AssigneeSelectorView.OnSearchKey` the `Enter` branch picks the highlighted row
(`Pick(_list.SelectedItem ?? 0)`) whenever `_rowPeople.Count > 0`. In the **empty-search state** the
rows are the current assignees (each a `✓` row), and `ListView.SelectedItem` defaults to `0`. So
pressing `Enter` in an empty search box on a task that already has assignees calls `Pick(0)` →
`Toggle` → `Removed`, which in `ImmediateApply` mode (Quick Updates #158) writes an **immediate,
unconfirmed assignee removal** to ClickUp. The box's `Enter` was intended as an *add* shortcut
("add the highlighted match without leaving the box"), but in the empty state the "matches" are
actually the current-assignee removal rows.

Because the behaviour lives in the reusable component (#212), it also affects the New Task host
(#213). Worse there: New Task's Save button is `IsDefault = true`, so an `Enter` that *falls through*
unhandled would bubble to a form **Save/create**.

## Fix — make the search box's `Enter` **add-only**, keyed on the highlighted row's selection state

The naive "no-op when the query is blank" gate is **not enough**: search is debounced (~1s), so the
rendered rows (`_rowPeople`) lag behind `_search.Text`. Typing a name and pressing `Enter` before the
debounce resolves (the *most natural* add flow) leaves the box non-blank while the rows are still the
current-assignee `✓` rows — a query-based gate would `Pick(0)` and silently remove the first assignee.
So the decision must key off the **actual highlighted row**, not the query text:

- Pick the highlighted row **only** when that row is a currently **unselected** person — i.e. picking
  it can only *add*, never remove. This is correct in every state: empty-state `✓` rows and the
  debounce-window `✓` rows are selected → no-op; search-result rows and empty-state top-up rows are
  unselected candidates → add. It also *preserves* the intended "Enter adds the highlighted candidate"
  behaviour (e.g. the top-frequent row on a task with no assignees — which the issue explicitly notes
  was never the bug), which a blanket blank-query no-op would have regressed.
- Always mark the search-box `Enter` **handled** so a no-op never bubbles to a host default action
  (New Task's default Save button, or any future host). This also matches the component contract
  already documented in `QuickUpdatesScreen` ("it handles Enter … and marks them handled").

Removal stays an **explicit** action: Cursor Down into the list (`OnListKey`), then `Enter` on the
`✓` row — that path is untouched (it still routes through `Toggle` directly).

## Where the logic goes

Per the codebase split (pure decisions in `AssigneeSelectorModel`, CI-untestable Terminal.Gui glue in
the View), add a pure predicate to the model and call it from the View:

```csharp
// AssigneeSelectorModel
public static bool ShouldAddFromSearchBox(
    int highlightedRow, IReadOnlyList<TaskAssignee> rows, ISet<long> selectedIds)
    => highlightedRow >= 0
       && highlightedRow < rows.Count
       && !selectedIds.Contains(rows[highlightedRow].Id);
```

```csharp
// AssigneeSelectorView.OnSearchKey, Enter branch
case KeyCode.Enter:
    key.Handled = true; // never bubble to a host default (e.g. New Task's default Save)
    if (AssigneeSelectorModel.ShouldAddFromSearchBox(_list.SelectedItem ?? -1, _rowPeople, _selectedIds))
        Pick(_list.SelectedItem ?? -1);
    break;
```

## Tests (unit — the decision is pure)

Add to `AssigneeSelectorModelTests`:

- highlighted **unselected** candidate → `true` (adding via a searched match / top-up still works).
- highlighted **selected `✓`** row (empty-state row 0) → `false` (the bug: would have removed them).
- **debounce window** — non-blank query but rows still the selected `✓` rows → `false` (the race the
  first attempt missed; guaranteed because the predicate ignores the query text).
- out-of-range row (`-1`, past the end) → `false`.
- no rows → `false`.

The list-`✓`-row removal path (`OnListKey`) and the immediate-apply add/remove flow are unchanged, so
the existing `AssigneeSelectorModelTests` (Toggle/EmptyState/Search) all stay green.

## Invariants / hard rules

- No `Generated/` or curated-spec change (no new ClickUp surface).
- No second focusable pane (#3); no new keybindings; bare letters untouched (#12). Pure service/UI-glue
  change to one existing component.
- TUI not unit-testable in CI: verify by build + reasoning; the pure predicate carries the regression
  guard. Manual verification described in the PR.

## Acceptance criteria mapping

- *Enter in an empty search box does not remove a current assignee* → blank-query `Enter` is a
  swallowed no-op.
- *Removal via a `✓` row still works* → `OnListKey` Enter path untouched.
- *Adding via a searched match still works* → active-query `Enter` still picks.
- *Covered by a test; applies to both hosts* → pure `ShouldPickFromSearchBox` test; fix is in the
  shared component so both #158 and #213 hosts inherit it.
