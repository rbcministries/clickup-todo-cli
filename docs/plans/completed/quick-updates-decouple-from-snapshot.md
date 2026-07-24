# Quick Updates: decouple the detail write path from the main-list snapshot `_all` (#297)

Part of the Multi-tab epic (#292). Sub-issue **(5)**. **No hard dependency**; this is the
enabling refactor that lets single-task launch mode (sub-issue 4, #296) get the full Quick
Updates experience with no main list present. Adjacent to #290 (Quick Updates → Ctrl+U
everywhere).

## Goal / acceptance (from the issue)

1. Quick Updates status/priority/assignee work from a single-task context with **no `_all`**
   present; list-mode behaviour is **unchanged** (row still updates in place, reconcile/revert
   intact, commit-generation supersession intact).
2. The shared write/reconcile is exercised by **unit tests from both entry modes**.
3. `dotnet test` green; `tui-validate` covers Quick Updates in list mode (single-task mode is
   wired by #296, so validate list-mode parity here).

## Verified current state (repo)

- `ShowQuickUpdates` (`Tui/TodoApp.cs:1830`) wires `screen.StatusCommitted`/`PriorityCommitted`
  to `ApplyStatus`/`ApplyPriority`, and the Assignees pane to `ApplyAssigneeAsync`.
- Both apply methods resolve the task via `QuickUpdatesTaskById(taskId)` =
  `TaskService.FindById(_all, _rows, taskId)` (`:1902`) and mutate the visible row + canonical
  snapshot via `UpdateTaskRow(updated, sending)` (`:2082`). `UpdateTaskRow` folds the three field
  changes onto `_all` through `TaskService.Apply{Status,Priority,Assignees}Change`, then repaints
  the one `ListView` row.
- `OpenQuickUpdatesForDetail` (`:1784`) already tolerates a task **absent from the snapshot**
  (feed-opened, #115): it seeds the screen from `TaskItemProjection.FromDetail(detail)`. But the
  **commit** path re-resolves via `QuickUpdatesTaskById`, which returns `null` for that task, so
  the commit dead-ends at *"This task is no longer in the list — status unchanged."* — a latent
  bug this refactor also fixes.
- The commit-generation guards (`_statusCommitGen`/`_priorityCommitGen`, `:1908`) and the
  screen/detail reflection (`ReconcileScreen*`/`ReflectDetail*`) are UI-thread concerns and stay
  in `TodoApp` untouched.

## Design

Introduce a small **`IQuickUpdateTarget`** seam — the mutable "unit of truth" a commit resolves
against and writes back to. The two touch points in the apply methods (`QuickUpdatesTaskById` and
`UpdateTaskRow`) become `target.Resolve(id)` and `target.Apply(updated, sending)`. Everything else
(commit-gen supersession, screen ✓ reconcile, detail reflection, Flash messaging) is unchanged.

```csharp
public interface IQuickUpdateTarget
{
    TaskItem? Resolve(string taskId);
    void Apply(TaskItem updated, bool sending);
}
```

- **List mode → `ListUpdateTarget`** (private adapter in `TodoApp`): `Resolve` =
  `QuickUpdatesTaskById`, `Apply` = `UpdateTaskRow`. Byte-for-byte the current behaviour — the
  snapshot stays authoritative and the on-screen row still repaints.
- **Single-task mode → `SingleTaskUpdateTarget`** (pure, in `Services/`): holds one mutable
  `TaskItem`, no list. `Resolve` returns it for a matching id; `Apply` composes the edit so
  consecutive commits build on each other. No row to repaint.

**Shared reconcile.** Extract the three-field fold `UpdateTaskRow` already performs into a pure
`TaskService.ApplyFieldChanges(snapshot, updated)`. `UpdateTaskRow` (list) and
`SingleTaskUpdateTarget.Apply` (single) both call it, so a commit settles a field **identically**
in both modes — this is the "shared write/reconcile exercised from both entry modes" the
acceptance asks for, and it is pure/unit-testable without a terminal.

**Target selection.**

- `OpenQuickUpdates` (list origin): always a `CurrentTask()` from the list → `ListUpdateTarget`.
- `OpenQuickUpdatesForDetail` (detail origin): if the detail's task resolves in the snapshot/rows
  (`QuickUpdatesTaskById(detail.Id) is not null`) → `ListUpdateTarget` (the row still updates);
  otherwise (feed-opened / single-task) → `SingleTaskUpdateTarget` seeded from the projection, so
  the commit succeeds against the loaded task with the list untouched.

The chosen target threads through `ShowQuickUpdates(..., IQuickUpdateTarget target)` into the three
commit handlers.

### Why this is low-risk

- The common list path calls the exact same two methods it does today (via the thin
  `ListUpdateTarget` delegate) — no change to the input-latency-sensitive redraw path, no second
  focusable pane.
- The new `SingleTaskUpdateTarget` branch only activates for a detail-launched task that isn't in
  the snapshot — previously a dead-ended commit, now a working one.
- No Kiota/generated changes; no API surface change; no new keybindings.

## Phases

1. **Shared reconcile + target seam + tests (services):** add
   `TaskService.ApplyFieldChanges`, `IQuickUpdateTarget`, `SingleTaskUpdateTarget`; unit-test the
   pure reconcile and the single-task target, including a parity test asserting a snapshot edit and
   a single-task edit settle to the identical record. `dotnet test` green.
2. **Wire the seam into `TodoApp`:** route `ApplyStatus`/`ApplyPriority`/`ApplyAssigneeAsync`
   through `IQuickUpdateTarget`; add `ListUpdateTarget`; pick the target in the open paths; make
   `UpdateTaskRow` call the shared reconcile. Build 0/0, `dotnet test` green, `dotnet format`.
3. **Validate + PR:** `tui-validate` list-mode Quick Updates parity (single-task mode arrives with
   #296); finalize the PR body with manual-verification notes.

## Deferred

- Wiring `SingleTaskUpdateTarget` to a real no-list UI is **#296** (single-task launch mode) and
  **#345** (agent dispatch in single-task mode); this issue delivers the seam + the tested target
  they consume.
