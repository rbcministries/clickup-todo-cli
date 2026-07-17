# Plan — #75: nest a pinned parent's subtasks under it in the Current Focus section

## Problem

When a **parent task is pinned** to "Current Focus" and the F4 subtasks view (`ShowSubtasks`)
is on, that parent's subtasks are **not** nested under the pinned parent. `TodoApp.Render`
builds the Focus section with a plain header + `AddTask` loop and never runs `SubtaskArranger`.
Meanwhile `nonPinned = _all.Where(!IsPinned)` only excludes *pinned* ids, so the parent's
(non-pinned) subtasks stay in the to-do set and render **flat / un-indented in their List group**.

## Goal (acceptance criteria from the issue)

- With F4 on and a parent pinned, its in-snapshot subtasks render **indented directly beneath it**
  in the Current Focus section (grandchildren included, same depth the arranger already supports).
- Those subtasks no longer appear un-indented in their List group (**no duplication**).
- F4-off behaviour unchanged (subtasks hidden; pins shown flat).
- Status-badge / indent alignment stays correct on nested pinned rows.
- Unit coverage for pinned-parent nesting, grouped and ungrouped.

## Design decisions (from the issue, settled)

1. **Gate on F4 (`ShowSubtasks`)** — only nest under a pinned parent when the subtasks view is on.
   With F4 off, `nonPinned` already drops all subtasks, so nothing changes.
2. **De-dup** — exclude "subtask of a pinned parent" from `nonPinned` when nesting.
3. **Pins ignore F3 filters/grouping** — the Focus section is arranged directly via the pure
   `SubtaskArranger`, *not* routed through `TaskView.Apply`. (Sort still applies, as today.)
4. **A pinned subtask whose parent is not pinned** — leave flat in Focus (arranger's orphan
   fallback with no context parents). Out of scope to drag its parent in.
5. **Non-assigned subtasks (#70)** — out of scope; we only nest subtasks already in the snapshot.

## Implementation

### New pure service — `Services/FocusSectionLayout.cs`

Mirrors `SectionLayout`/`SubtaskArranger` (no Terminal.Gui, fully unit-testable).

```
FocusSection Build(
    IReadOnlyList<TaskItem> allTasks,
    IReadOnlySet<string> pinnedIds,
    bool nest,
    TaskField? sortField,
    SortDirection sortDirection)

readonly record struct FocusSection(
    IReadOnlyList<ArrangedRow> Rows,           // pinned anchors + nested descendants
    IReadOnlySet<string> NestedSubtaskIds)     // non-pinned subtasks pulled into Focus
```

- **nest = false** → `Rows` = sorted pinned tasks as flat depth-0 `ArrangedRow`s (parity with
  today); `NestedSubtaskIds` empty. (No arranger, so two pinned parent/child pins stay flat as now.)
- **nest = true**:
  1. Build `childrenByParent` over `allTasks`.
  2. Walk down from every pinned id (DFS, cycle-guarded) collecting **all in-snapshot descendants**;
     those not themselves pinned become `NestedSubtaskIds` / pulled tasks.
  3. `focusInput = TaskView.Sort(pinned ∪ pulled, sortField, sortDirection)`.
  4. `Rows = SubtaskArranger.Arrange(focusInput, emptyContext)` — nests each descendant under its
     ancestor; a pinned subtask whose parent isn't in the set falls back flat (decision 4).

### `Tui/TodoApp.Render`

- Compute `pinnedIds` once; call `FocusSectionLayout.Build(...)`.
- `nonPinned = _all.Where(t => !pinnedIds.Contains(t.Id) && !focus.NestedSubtaskIds.Contains(t.Id))`;
  keep the existing `if (!nest) drop-subtasks` filter.
- Emit the Focus header (count = `pinnedIds.Count`, unchanged semantics — pulled children are
  nested rows, not pins), then `foreach (row in focus.Rows) AddTask(row.Task, row.Depth, false, null)`
  (Focus rows keep every segment — no group header above them, #67).
- Stash `focus.NestedSubtaskIds` in a new field `_focusNestedIds`.

### `Tui/TodoApp.UpdateTaskRow` (in-place status update, #67 grouped-field omission)

The row omits the grouped field only when it's a **to-do** row. A pulled-in Focus subtask isn't
pinned, so the old `_focus.IsPinned(id)` test would wrongly omit its group segment. Fix:
`var inFocus = _focus.IsPinned(id) || _focusNestedIds.Contains(id);` and gate `groupedBy` on that.

## Tests — `tests/ClickUpTodo.Tests/FocusSectionLayoutTests.cs`

Mirror `SectionLayoutTests`/`SubtaskArrangerTests` (pure, no UI):

- Pinned parent + its subtask (nest on): subtask nested at depth 1, subtask id in `NestedSubtaskIds`.
- Grandchildren nest (depth 2).
- nest off: pinned shown flat, `NestedSubtaskIds` empty.
- Pinned subtask whose parent isn't pinned → flat in Focus, not pulled, parent not dragged in.
- Pinned parent whose subtask is *also* pinned → child appears once (no dup), nested.
- No pins → empty section.
- Sort order respected among top-level pins.

TUI (`TodoApp`) itself isn't unit-testable in CI — verify by build + reasoning; the arrangement
logic is covered by the pure `FocusSectionLayoutTests`. No new focusable pane; no keybinding change.

## Out of scope / deferred

- Pulling in a pinned parent's subtasks that aren't assigned to me — tracked by #70.
- Per-parent interactive fold (#76) will share this Focus arrangement.
