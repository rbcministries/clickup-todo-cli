# In-place row update preserves not-mine / context markers (#264)

## Problem

After a Quick Updates Status/Priority/Assignee commit on a **not-mine** row (a pulled-in
foreign subtask or a context parent), the row's trailing context marker transiently
disappears until the next full re-render (a background refresh or manual `F5`).

Cause (verified in the issue): `TodoApp.UpdateTaskRow` re-formats the single edited row in
place via `BuildRow(updated, …)` **without** the `isForeignSubtask` / `isContextParent` /
`isUnassignedSubtask` flags. The full render path (`Render` → `AddTask` → `BuildRow`) sets
those flags per row:

- `isContextParent` — from `row.IsContextParent` (SectionLayout, driven by `_contextParents`).
- `isForeignSubtask` — `IsForeignOthers(task, VisibleForeignSubtasks())`.
- `isUnassignedSubtask` — `IsForeignUnassigned(task, VisibleForeignSubtasks())`.

`TaskRowFormatter.Format` appends the marker only when the matching flag is set (precedence:
context-parent > unassigned > foreign-others), so the in-place update dropped it.

It's a transient cosmetic inconsistency — the row stays put and shows the confirmed
status/priority correctly; the marker reappears on the next full `Render`. No data problem,
no API surface.

## Fix

1. Extract the three-flag classification into a pure `internal static` helper on `TodoApp`:

   ```csharp
   internal static (bool IsContextParent, bool IsForeignSubtask, bool IsUnassignedSubtask)
       ClassifyRowMarker(
           TaskItem task,
           IReadOnlyDictionary<string, TaskItem> contextParents,
           IReadOnlyDictionary<string, TaskItem> visibleForeign)
       => (contextParents.ContainsKey(task.Id),
           IsForeignOthers(task, visibleForeign),
           IsForeignUnassigned(task, visibleForeign));
   ```

   `_contextParents` keys are disjoint from `_all` (context parents are never in the
   snapshot), so `ContainsKey(id)` faithfully reproduces `row.IsContextParent` for the single
   row an in-place update touches.

2. Have `UpdateTaskRow` compute the flags via `ClassifyRowMarker(updated, _contextParents,
   VisibleForeignSubtasks())` and pass them into `BuildRow`, mirroring the full render path so
   the in-place row keeps the same marker it had before the edit.

## Tests

- Unit-test `ClassifyRowMarker` over the four cases (plain snapshot task → all false; context
  parent → context-parent only; foreign-others subtask → foreign only; unassigned foreign
  subtask → unassigned only) — the logic `UpdateTaskRow` was missing. `InternalsVisibleTo
  ClickUpTodo.Tests` is already set.
- `TaskRowFormatter.Format`'s flag→marker mapping and precedence are already covered by
  `TaskRowFormatterTests`, so together the classifier test + existing formatter tests guard
  the full in-place-update chain.

## Deferred

- Tightening `tests/ClickUpTodo.Tui.E2E/foreign_quickupdates_check.py` to assert the marker
  survives the edit depends on PR #263 (which introduces that file) landing on `main`. Tracked
  as follow-up, not blocking this fix.

## Scope

Small, self-contained TUI-render fix. Single focusable `ListView` unchanged; no new pane, no
API/spec change, no generated-code touch.
