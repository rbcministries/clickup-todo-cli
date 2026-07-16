# Task ↔ list membership writes (add/remove a task to/from a list) — issue #237

Part of the **Writing New Content** epic (#208). **Foundation — no UI in this slice.**
Enables multi-list task creation (#241) and the Quick Updates List pane (#242) downstream.
ClickUp calls this feature **"Tasks in Multiple Lists."**

A task has one **home** list (set at creation, `POST /list/{id}/task`, #209) plus optional
additional **locations**. The read side already landed (#36): `TaskDetail.Lists`
(`ClickUp/Models.cs`) surfaces the `locations` array. The **write** side — adding/removing
those additional locations — does not exist at any layer today. This adds it.

## Verified current state

- **Read side exists:** `TaskDetail.Lists` surfaces `locations` (empty for the single-list case).
- **No write endpoint generated:** `V2.List[listId].Task`
  (`Generated/V2/List/Item/TaskNamespace/TaskRequestBuilder.cs`) exposes only `GetAsync` +
  `PostAsync` (create); it is **not indexable by `task_id`**, so there is no
  `/list/{list_id}/task/{task_id}` builder. The facade has no add/remove-to-list method.
- The curated spec's `/v2/list/{list_id}/task` path defines only `get` (GetTasks) + `post`
  (CreateTask). No global `security` block; auth is applied by the request adapter's auth
  provider, not the spec.
- No `pwsh` in this environment — regen invokes the underlying `dotnet kiota generate`
  directly (same body as `scripts/regen-client.ps1`), matching prior regen work (#36).

## ClickUp v2 endpoints (to confirm live via the SkippableFact)

- **Add task to list:** `POST /v2/list/{list_id}/task/{task_id}` — no request body; success
  returns an empty object `{}`.
- **Remove task from list:** `DELETE /v2/list/{list_id}/task/{task_id}` — no request body;
  success returns `{}`.
- When the **"Tasks in Multiple Lists" ClickApp is disabled**, the add call fails with an API
  error (non-2xx). Kiota surfaces this as an `ApiException`, which the facade's `Guard`
  wrapper already translates into a typed `ClickUpApiException` carrying the `StatusCode`.

## Design

### Spec (`ClickUp/clickup-openapi.json`)
Add a new path `/v2/list/{list_id}/task/{task_id}` with two operations:
- `post` → `operationId: AddTaskToList`, path params `list_id` + `task_id`, **no** request
  body, a `200` response with **no `content`** (so Kiota generates a `PostAsync` returning
  `Task`, no payload).
- `delete` → `operationId: RemoveTaskFromList`, same path params, no body, empty `200`.

No new component schemas (both responses are empty). This is the only spec change.

### Regen
`dotnet kiota generate --language CSharp --openapi src/ClickUpTodo/ClickUp/clickup-openapi.json
--class-name ClickUpApiClient --namespace-name ClickUpTodo.ClickUp.Generated --output
src/ClickUpTodo/ClickUp/Generated --clean-output --exclude-backward-compatible`
(the `regen-client.ps1` body). This makes `TaskRequestBuilder` (under `V2/List/Item/
TaskNamespace/`) **indexable by task id**, yielding a new item builder with `PostAsync` /
`DeleteAsync`. No hand edits under `Generated/`.

### Facade (`IClickUpClient` + `ClickUpClient`)
Add two methods, mirroring the existing single-mutation style and the default-throwing-impl
pattern used by the other write methods (so read-only fakes needn't implement a write path):

```csharp
Task AddTaskToListAsync(string taskId, string listId, CancellationToken ct = default);
Task RemoveTaskFromListAsync(string taskId, string listId, CancellationToken ct = default);
```

- Argument order is `(taskId, listId)` to match the other task-first facade writes
  (`SetTaskStatusAsync(taskId, …)`), even though the URL is list-first.
- Bodies call `_client.V2.List[listId].Task[taskId].PostAsync(...)` / `.DeleteAsync(...)`
  inside `Guard("AddTaskToList" / "RemoveTaskFromList", …)` so an API failure (incl. the
  ClickApp-disabled case) becomes a `ClickUpApiException`, not an unhandled crash.
- Interface default impls throw `NotSupportedException` (mirrors `CreateTaskAsync` /
  `SetTaskDescriptionAsync`) so existing read-only fakes compile unchanged.
- Doc-comments record: no confirmable payload (returns `Task`), verify via a subsequent
  `GetTaskDetailAsync().Lists`; and the "Tasks in Multiple Lists disabled" ClickApp
  prerequisite → surfaces as a `ClickUpApiException` for the downstream call sites
  (#241/#242) to catch and flash.

**Home list is out of scope** — set only at create (#209); these manage the *additional*
locations. Moving the home list (move) is out of scope.

## Tests

### Unit — request building (`ClickUpClientListMembershipTests.cs`, new)
Drive the real generated client through the existing `CapturingHandler` idiom (no token, no
network), asserting method + URL for each:
- `AddTaskToListAsync` → `POST`, URI contains `/v2/list/{listId}/task/{taskId}`, empty/no body.
- `RemoveTaskFromListAsync` → `DELETE`, same URI shape.
- A non-2xx response (e.g. 400, the ClickApp-disabled shape) → the facade throws
  `ClickUpApiException` with the right `StatusCode` (not a raw Kiota `ApiException`).

### Integration — live round-trip (`SkippableFact`, env-gated on `CLICKUP_TOKEN`)
Mirror `ClickUpClientIntegrationTests`: add a task to a second list, fetch detail, assert the
list appears in `TaskDetail.Lists`; remove it, re-fetch, assert it's gone. Skips cleanly
without a token so CI stays green.

## Phases

1. **Spec + regen + facade + unit tests.** Edit `clickup-openapi.json`; regen; add the two
   facade methods (interface + class); add `ClickUpClientListMembershipTests`. Build/test/format
   green; commit; push (opens the draft PR).
2. **Integration test + docs polish.** Add the env-gated `SkippableFact`; tidy doc-comments;
   note the downstream call-site error-flashing responsibility. Build/test/format; commit; push.

## Out of scope (deferred to their own issues)

- UI wiring (New Task multi-list #241, Quick Updates List pane #242) — those catch the
  `ClickUpApiException` and flash it.
- Changing a task's **home** list (move).
