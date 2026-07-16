# Plan — Writing New Content (M): task↔list membership writes (#237)

Part of Epic #208. **Foundation — no UI dependency.** Unblocks multi-list New Task creation
(#241, N) and the Quick Updates List pane (#242 / #153 follow-up). ClickUp calls this feature
**"Tasks in Multiple Lists."**

## Goal

Add the ability to **add / remove a task's membership of an additional list** to the API facade:

- `POST /v2/list/{list_id}/task/{task_id}` — add the task to the list.
- `DELETE /v2/list/{list_id}/task/{task_id}` — remove the task from the list.

A task has one **home** list (set at creation, unchanged here) plus optional additional
**locations**. The read side already exists — `TaskDetail.Lists` (`Models.cs`, from the task
response's `locations`, added in #36). This adds the write side, which does not exist at any
layer today.

## ClickUp v2 request shape (verified against the v2 reference)

Both endpoints take the task id and list id purely in the path; **no request body**. Each
returns HTTP 200 with an empty JSON object `{}`. When the "Tasks in Multiple Lists" ClickApp is
disabled for the workspace, the add call fails with an HTTP 4xx (ClickUp error `OV_016`).

## Hard rules honored

- **No hand edits under `Generated/`.** Edit the curated spec
  `src/ClickUpTodo/ClickUp/clickup-openapi.json`, then regenerate with Kiota.
- Generated types never escape the facade.
- Personal-token raw `Authorization` header untouched. Integration test is a `SkippableFact`,
  env-gated on `CLICKUP_TOKEN`.
- No TUI surface in this slice (single sectioned `ListView` model untouched).

## Design

- **Spec** (`clickup-openapi.json`): add a new path item `/v2/list/{list_id}/task/{task_id}`
  with two operations:
  - `post` — `operationId: AddTaskToList`, `list_id` + `task_id` path params, **no requestBody**,
    a `200` response described only (no content schema) so Kiota emits a **void-returning**
    `PostAsync` (nothing useful in `{}`).
  - `delete` — `operationId: RemoveTaskFromList`, same path params, `200` described only →
    void-returning `DeleteAsync`.
  This is the spec's first `delete` verb and first schema-less response; both are valid OpenAPI
  and Kiota-supported.
- **Regen**: `dotnet tool restore` then the `regen-client.ps1` body invoked directly
  (`dotnet kiota generate … --clean-output --exclude-backward-compatible`; `pwsh` is absent).
  Kiota will add a `this[string position]` indexer on the list `TaskRequestBuilder` and emit
  `V2/List/Item/TaskNamespace/Item/WithTask_ItemRequestBuilder.cs` with `PostAsync`/`DeleteAsync`.
- **Facade** (`IClickUpClient` + `ClickUpClient`):
  - `Task AddTaskToListAsync(string taskId, string listId, CancellationToken ct = default)`
  - `Task RemoveTaskFromListAsync(string taskId, string listId, CancellationToken ct = default)`
  Both wrap `_client.V2.List[listId].Task[taskId].PostAsync/DeleteAsync` in the existing `Guard`
  choke point. Add a **non-generic `Guard(string, Func<Task>)` overload** for these void calls
  (mirrors the generic one), so any Kiota `ApiException` is translated to the domain
  `ClickUpApiException` — i.e. the feature-disabled 4xx is a **caught, non-fatal** error, not a
  crash. XML docs on both methods document the "Tasks in Multiple Lists" ClickApp prerequisite
  and the throwing default on the interface (mirroring the other write methods) so read-only
  fakes needn't implement it.
- **`taskId`-first argument order** matches the domain reading ("add *this task* to *that list*")
  and the issue's suggested signature, even though the generated URL nests list-then-task.

## Phases

### Phase 1 — spec + regen

Edit the spec, regenerate, confirm the generated client builds (0/0). Commit the spec + the
regenerated `Generated/` delta together (generated code follows the spec change).

### Phase 2 — facade + interface + tests

1. Add the two methods to `IClickUpClient` (throwing defaults) and `ClickUpClient` (real impl),
   plus the non-generic `Guard` overload.
2. Tests:
   - `ClickUpClientListMembershipTests` (offline, `CapturingHandler` mirroring
     `ClickUpClientCreateTaskTests`): add sends `POST` to `/v2/list/{listId}/task/{taskId}` with
     no body; remove sends `DELETE` to the same URL; a non-2xx response (feature disabled)
     surfaces as a `ClickUpApiException` carrying the status code rather than throwing raw.
   - Integration `SkippableFact` in `ClickUpClientIntegrationTests` gated on
     `CLICKUP_TOKEN` + `CLICKUP_LIST_ID` + `CLICKUP_TASK_ID` + a second list id
     (`CLICKUP_SECONDARY_LIST_ID`): add the task to the second list, assert it appears in
     `GetTaskDetailAsync(...).Lists`, then remove it and assert it's gone — try/finally so the
     task's membership is restored, mirroring the assignee add/remove round-trip.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green, integration self-skips),
  `dotnet format`. No TUI surface touched → no `tui-validate` needed.

## Deferred (out of scope, tracked)

- **UI wiring** — multi-list selection on the New Task screen (#241, N) and the Quick Updates
  List pane (#242); the "feature disabled" error is *flashed* at those call sites there, not here.
- Setting/changing a task's **home** list (creation-time only in ClickUp v2).
- Capturing ClickUp's `ECODE` from the error body for a message more specific than the HTTP
  status — `Guard` records only the status code today; a richer error surface is a separate
  cross-cutting change, not this foundation slice.
