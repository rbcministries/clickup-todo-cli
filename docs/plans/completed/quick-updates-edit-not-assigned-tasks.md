# Quick Updates on tasks not assigned to the current user (#160)

Part of the Quick Updates epic (#153). Depends on the Quick Updates screen shell (#156, **merged**).

## Goal

Let Quick Updates (Space / detail launch) open and apply **Status** and **Priority**
edits on a task that isn't the current user's own work — a **foreign subtask** pulled in
under an assigned parent (`_foreignSubtasks`, #70/#179) or a **context parent**
(`_contextParents`, #46). Today both paths hard-block with a "not assigned to you —
status unchanged" flash. Lift the *edit* restriction only; the informational trailing row
markers stay.

## Verified current state (in code)

- **Two open paths, both guarded:** `TodoApp.OpenQuickUpdates()` (`Tui/TodoApp.cs:1485`)
  and `OpenQuickUpdatesForDetail()` (`:1541`) each `Flash(...)`+`return` when the task id
  is in `_contextParents` or `_foreignSubtasks`, before the no-list guard.
- **The reconcile primitives already cope with a not-in-`_all` task:**
  - `UpdateTaskRow` (`:1806`) keys the visible update off `_rows.FindIndex(...)` — and
    foreign/context rows **are** in `_rows` — while `TaskService.ApplyStatusChange` /
    `ApplyPriorityChange` (`Services/TaskService.cs:379/388`) are pure and a **no-op** when
    the id isn't in `_all` (already unit-tested for status:
    `StatusUpdateTests.ApplyStatusChange_NoMatch_ReturnsEquivalentSnapshot`). So the row
    updates in place and `_all`/`_signature` are left clean.
- **The actual blocker is the edit-target lookup.** `ApplyStatus` (`:1647`) and
  `ApplyPriority` (`:1704`) resolve the task with `TaskById` (`:1626`,
  `_all.FirstOrDefault`). For a foreign subtask / context parent that isn't in `_all` this
  returns null, so:
  1. the initial guard flashes "This task is no longer in the list — status unchanged" and
     bails, and
  2. even if opened, the post-write success/revert reconcile is gated on
     `TaskById(taskId) is { } t`, so the optimistic `"(sending…)"` row would never settle.
- **Assignees apply is not in `main` yet** (the Assignees pane is stubbed; immediate-apply
  lands with #158 / PR #229). This slice therefore wires Status + Priority — the two apply
  paths that exist — and the shared resolver it introduces is exactly what a future
  assignee-apply path will reuse. Noted as follow-up in the PR.

## Design

Introduce one pure resolver and route the Quick Updates apply paths through it; delete the
two ownership guards.

1. **`TaskService.FindById(primary, rows, taskId)`** — pure static. Resolve first from the
   canonical snapshot (`primary` = `_all`), then from the visible `rows` (`_rows`, which
   include the context rows that live outside the snapshot; null header entries skipped).
   Returns null when the id is in neither. Preferring `primary` is safe because
   `UpdateTaskRow` keeps `_all` **and** `_rows` in sync for any edited task, so whichever
   side holds it carries the current optimistic value — consecutive edits (status then
   priority) compose without clobbering.
2. **`TodoApp.QuickUpdatesTaskById(taskId)`** — `=> TaskService.FindById(_all, _rows, taskId)`.
   Swap the three `TaskById` calls in `ApplyStatus` and the three in `ApplyPriority` to it.
   `TaskById` stays for its other callers.
3. **Remove** the `_contextParents` / `_foreignSubtasks` guards from `OpenQuickUpdates` and
   `OpenQuickUpdatesForDetail`. **Keep** the no-list guard (a data constraint, not an
   ownership one) and the context/foreign markers in `TaskRowFormatter`.

## Tests

- `TaskServiceFindByIdTests` (new): found in primary; found only in rows (foreign/context
  case); primary preferred over a same-id row; null header rows skipped; not-found → null;
  input not mutated.
- Extend `StatusUpdateTests` with `ApplyPriorityChange_NoMatch_ReturnsEquivalentSnapshot`
  to lock the priority side of the "editing a not-in-`_all` task leaves the snapshot clean"
  invariant (status already has the sibling test).
- TUI wiring (guard removal, resolver routing) is not CI-unit-testable (Terminal.Gui) —
  validated by build + `tui-validate`.

## Validation

- `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format`.
- `tui-validate`: select a foreign-subtask row → Space opens Quick Updates (no block) →
  commit a status → the row shows the new status in place and stays put (not dropped).

## Out of scope / deferred

- Assignees immediate-apply on these rows — arrives with the Assignees pane (#158 / PR
  #229); the resolver introduced here is what it will reuse.
- No curated-spec / Kiota / `Generated/` change; no new ClickUp API surface. Personal-token
  raw `Authorization` header untouched.
