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

## Fix (issue option 1 — the recommended one)

Make the search box's `Enter` an **add-only** shortcut:

- Pick the highlighted row **only** when there is an active (non-blank) query — in that state the
  rows are addable search matches (`SearchResultRows`, which excludes already-selected ids, so a
  pick can only ever *add*).
- On a **blank** (empty / whitespace-only) query the rows are the current-assignee `✓` rows, so
  `Enter` is a **no-op**.
- Always mark the search-box `Enter` **handled** so a no-op never bubbles to a host default action
  (New Task's default Save button, or any future host). This also matches the component contract
  already documented in `QuickUpdatesScreen` ("it handles Enter … and marks them handled").

Removal stays an **explicit** action: Cursor Down into the list (`OnListKey`), then `Enter` on the
`✓` row — that path is untouched.

The "active query" test uses `string.IsNullOrWhiteSpace`, matching the View's `Trim()` in
`OnSearchChanged`/`RenderCurrent` (a whitespace-only box renders the empty `✓`-row state), so the
decision and the rendered rows agree.

## Where the logic goes

Per the codebase split (pure decisions in `AssigneeSelectorModel`, CI-untestable Terminal.Gui glue in
the View), add a pure predicate to the model and call it from the View:

```csharp
// AssigneeSelectorModel
public static bool ShouldPickFromSearchBox(string? query, int rowCount)
    => rowCount > 0 && !string.IsNullOrWhiteSpace(query);
```

```csharp
// AssigneeSelectorView.OnSearchKey, Enter branch
case KeyCode.Enter:
    key.Handled = true; // never bubble to a host default (e.g. New Task's default Save)
    if (AssigneeSelectorModel.ShouldPickFromSearchBox(_search.Text, _rowPeople.Count))
        Pick(_list.SelectedItem ?? 0);
    break;
```

## Tests (unit — the decision is pure)

Add to `AssigneeSelectorModelTests`:

- blank query (`""`) with rows present → `false` (the bug: would have removed the first assignee).
- whitespace-only query (`"   "`) with rows → `false` (matches the View's trim → empty state).
- `null` query → `false`.
- active query with matches → `true` (adding via a searched match still works).
- active query but zero rows → `false` (nothing to pick; `Enter` is a swallowed no-op).

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
