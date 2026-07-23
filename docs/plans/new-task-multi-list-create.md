# New Task — create in multiple lists (#241)

Sub-issue **N** of the Writing New Content epic (#208). Depends on the New Task
List selector (#239/#240, merged) and the task↔list membership write
`AddTaskToListAsync` (#237, merged). Completes multi-list task creation: when the
user selects more than one list on the New Task screen, create the task in the
primary/home list and add it to the rest.

> **Shipped DISABLED pending the list-change migration (#365).** Exactly as the
> Quick Updates List pane (#242/#339) is: the implementation lands complete and
> unit-tested, but the New Task screen wires it to file into the **single home
> list only** until the field/status stranding migration is designed (#365).
> Focusing the List selector flashes `NewTaskScreen.MultiListDisabledNote` so a
> user who adds a second list knows it won't be applied.
>
> **To re-enable when #365 lands:** in `NewTaskScreen.TrySave`, pass
> `_lists.Selection` to `NewTaskCreator.CreateAsync` instead of `[primary!]`, and
> drop the `MultiListDisabledNote` focus flash wired in the constructor. The
> orchestrator, its facade delegate, and the partial-failure result/flash stay in
> the tree, so nothing else changes.

## Verified current state

- `NewTaskScreen` (`Tui/Screens/NewTaskScreen.cs`) embeds a `ListSelectorView` in
  collect-selection mode. It exposes an ordered `Selection` (`IReadOnlyList<NamedEntity>`)
  and a distinguished `Primary` (`NamedEntity?`, the create target — the marked
  `" (home)"` list if still selected, else the first selected, else null).
- Save (`TrySave`) validates via the pure `NewTaskForm.TryBuild` (name/list/due),
  then calls the injected `createAsync(primaryListId, request, ct)` — **primary
  list only**; the additional selections are ignored today.
- `ClickUpClient.CreateTaskAsync(listId, req)` POSTs to the home list and returns
  the mapped `TaskItem`. `ClickUpClient.AddTaskToListAsync(taskId, listId)` performs
  the "Tasks in Multiple Lists" add and surfaces a disabled-ClickApp failure as a
  caught `ClickUpApiException` (ClickUp `OV_016`). Both are exposed on `TaskService`.
- The screen raises `Created` (`EventHandler<TaskItem>`); `TodoApp` sets the
  pending-select id, flashes `Created "…" · refreshing…`, and requests a refresh.

## Design

Keep the pure/testable-service pattern the screen already uses (`NewTaskForm`,
injected async callbacks). Introduce a small **TUI-free orchestrator** so the
create-then-add sequence is unit-tested against faked facade delegates.

### New: `Services/NewTaskCreator.cs`

- `record NewTaskCreateResult(TaskItem Created, IReadOnlyList<NamedEntity> FailedAdditionalLists)`
  with `AllListsSucceeded => FailedAdditionalLists.Count == 0`.
- `static Task<NewTaskCreateResult> CreateAsync(primary, selection, request, createAsync, addToListAsync, ct)`:
  1. Compute the **additional** lists = `selection` minus `primary` (by id),
     de-duped, blank ids dropped, order preserved.
  2. `await createAsync(primary.Id, request, ct)` → `created` (a create failure
     propagates out — the task doesn't exist, form stays open; existing behaviour).
  3. For each additional list, `await addToListAsync(created.Id, list.Id, ct)`;
     catch any per-list failure and record the list in `FailedAdditionalLists`
     (never roll back the created task). `OperationCanceledException` is rethrown,
     not recorded as a failure.
  4. Return the result (empty `FailedAdditionalLists` on the single-list path — no
     add calls issued).

### `NewTaskScreen`

- Inject a second delegate `addToListAsync` (`Func<string,string,CancellationToken,Task>`).
- `TrySave` reads `_lists.Selection` + `_lists.Primary`, validates as today, then
  in the existing off-UI-thread `Task.Run` calls `NewTaskCreator.CreateAsync(...)`.
- Change `Created` to `EventHandler<NewTaskCreateResult>` so the host can report the
  partial-failure outcome. On success (any result, since the task exists), raise
  `Created` and close; a create failure keeps the form open and flashes the error
  (unchanged).

### `TodoApp`

- Wire `addToListAsync: (taskId, listId, ct) => _tasks.AddTaskToListAsync(taskId, listId, ct)`.
- In the `Created` handler, compose the flash from the result: all-succeeded →
  `Created "…" · refreshing…`; partial → `Created "…", but couldn't add to <names> · refreshing…`
  (falling back to the list ids when a name is blank), so the outcome is unambiguous.

## Tests (`tests/ClickUpTodo.Tests/NewTaskCreatorTests.cs`)

Fake the two delegates (capturing calls; one that throws for chosen list ids):

- Single selected list ⇒ creates in primary, **zero** add calls, `AllListsSucceeded`.
- N selected lists ⇒ creates in primary, adds the other N−1 in order, no add for
  the primary; a subsequent membership would show all (asserted via captured calls).
- The primary is excluded from adds even when it isn't `Selection[0]` (home removed
  then re-picked / distinguished falls through).
- Duplicate / blank ids in the selection are de-duped / dropped.
- A failing additional add is recorded in `FailedAdditionalLists` while the created
  task is still returned and the remaining adds still run (no early abort, no rollback).
- A create failure propagates (no add calls attempted).

`NewTaskForm` behaviour is unchanged (existing `NewTaskFormTests` stay green).

## Manual / TUI verification

`dotnet test` green, then `tui-validate` drives a two-list New Task create against
the fake backend and asserts the confirmation. Single-focusable-pane model is
unchanged (no new pane); no generated code touched; no spec change (both facade
methods already exist).
