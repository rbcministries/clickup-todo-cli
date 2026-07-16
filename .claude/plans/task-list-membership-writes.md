# Task↔list membership writes — add/remove a task to/from a List (#237)

Part of #208 (Writing New Content). **Foundation — no UI dependency.** Enables
multi-list task creation (#241, N) and the adjacent Quick Updates List pane
(#242). ClickUp calls this **"Tasks in Multiple Lists."**

A task has one **home** list (set at creation) plus optional additional
**locations**. Selecting more than one list — on the New Task screen or in Quick
Updates — means adding the task to those extra lists. That write does not exist
at any layer today; this slice adds it at the spec → generated-client → facade
layers, with unit + integration coverage. No call sites yet (they land in #241 /
#242).

## Verified current state

- **Read side exists:** `TaskDetail.Lists` (`Models.cs`) surfaces ClickUp's
  multi-list `locations` (empty for the single-list case). `TaskItem` carries
  only the single home list (`ListId`/`ListName`).
- **No write endpoint generated:** the `/v2/list/{list_id}/task` builder
  (`Generated/V2/List/Item/TaskNamespace/TaskRequestBuilder.cs`) exposes only
  `GetAsync` + `PostAsync` (create). It is **not indexable by `task_id`**, so
  there is no `/list/{list_id}/task/{task_id}` builder. The facade has no
  add/remove-to-list method.
- Facade mutation pattern to mirror: `SetTaskStatusAsync` /
  `AddTaskAssigneeAsync` — a single generated call wrapped in `Guard(operation,
  …)`, which maps a Kiota `ApiException` → `ClickUpApiException(statusCode,
  operation, inner)`.

## API shape (confirmed against ClickUp v2 reference)

- **Add:** `POST /v2/list/{list_id}/task/{task_id}` — no request body; success is
  an empty `{}` 200.
- **Remove:** `DELETE /v2/list/{list_id}/task/{task_id}` — no request body; empty
  `{}` 200.
- Both require the workspace's **"Tasks in Multiple Lists"** ClickApp to be
  enabled; when it is off the API returns a 4xx error (surfaced as a
  `ClickUpApiException`, not a crash).

## Design

### 1. Curated spec (`ClickUp/clickup-openapi.json`) + Kiota regen

Add one new path, `/v2/list/{list_id}/task/{task_id}`, with `post`
(`operationId: AddTaskToList`) and `delete` (`operationId: RemoveTaskFromList`).
Each declares the two path params and a bodyless `200` with **no content**, so
Kiota emits void `PostAsync`/`DeleteAsync` on the new item builder.

Regenerate with the pinned Kiota tool (the repo's `scripts/regen-client.ps1` is
a single `dotnet kiota generate`; `pwsh` is unavailable in this environment, so
run the underlying command directly):

```bash
dotnet tool restore
dotnet kiota generate --language CSharp \
  --openapi src/ClickUpTodo/ClickUp/clickup-openapi.json \
  --class-name ClickUpApiClient \
  --namespace-name ClickUpTodo.ClickUp.Generated \
  --output src/ClickUpTodo/ClickUp/Generated \
  --clean-output --exclude-backward-compatible
```

Adding the `{task_id}` child makes the existing `.Task` builder indexable, so the
new call site is `_client.V2.List[listId].Task[taskId].PostAsync/DeleteAsync`.
**No hand edits under `Generated/`.** The regen diff is inspected to confirm it is
limited to the new item builder + the indexer on `TaskRequestBuilder` (no churn
from a Kiota-version mismatch).

### 2. Facade (`IClickUpClient` + `ClickUpClient`)

```csharp
Task AddTaskToListAsync(string taskId, string listId, CancellationToken ct = default);
Task RemoveTaskFromListAsync(string taskId, string listId, CancellationToken ct = default);
```

- On `IClickUpClient` these get **default-throwing** implementations (mirroring
  `CreateTaskAsync` / `SetTaskDescriptionAsync` / the delta fetches) so the many
  read-only fakes in the test suite need not implement a write path they never
  call.
- On `ClickUpClient` they call the generated bodyless `PostAsync`/`DeleteAsync`
  inside a new **non-generic `Guard(operation, Func<Task>)`** overload (the
  existing `Guard<T>` requires a return value; these calls have none). Argument
  order is `(taskId, listId)` per the issue, even though the URL is
  `/list/{listId}/task/{taskId}`.
- The "Tasks in Multiple Lists disabled" ClickApp error rides the existing
  `Guard` → `ClickUpApiException` path; call sites (#241/#242) catch it and flash.
  Documented on the methods.

### 3. Tests

- **Unit (`ClickUpClientListMembershipTests.cs`, new):** drive the real
  generated client through the `CapturingHandler` idiom (from
  `ClickUpClientWriteTests`), asserting:
  - `AddTaskToListAsync` issues `POST /v2/list/{listId}/task/{taskId}` with **no
    body**;
  - `RemoveTaskFromListAsync` issues `DELETE` to the same URL with no body;
  - a 4xx response maps to a `ClickUpApiException` carrying the status code (the
    "feature disabled" path), and the operation name is preserved.
- **Integration (`SkippableFact` in `ClickUpClientIntegrationTests.cs`):**
  add-then-remove round-trip gated on `CLICKUP_TOKEN` + `CLICKUP_TASK_ID` +
  `CLICKUP_SECONDARY_LIST_ID`, verified via `GetTaskDetailAsync(...).Lists`.
  Idempotent (removes what it added). A `ClickUpApiException` on the add (feature
  disabled on the test workspace) is turned into a `Skip`, not a failure.

## Out of scope / deferred

- **Call sites** — multi-list New Task create (#241) and the Quick Updates List
  pane (#242) consume these methods; not in this slice.
- **Home-list change (move)** — the home list is set only by the create endpoint
  (#209); these methods manage the *additional* locations only.
- **Bulk enumeration of a task's lists** — the read side (`TaskDetail.Lists`)
  already covers it.

## Acceptance criteria (from #237)

- `AddTaskToListAsync` / `RemoveTaskFromListAsync` add/remove a task's list
  membership, verified by a subsequent detail fetch (`TaskDetail.Lists`). ✅ integration
- A "Tasks in Multiple Lists disabled" API error is caught and reported, not
  fatal. ✅ mapped to `ClickUpApiException` (unit-tested), surfaced by callers.
- Client regenerated from the edited spec; `dotnet test` green (integration
  `SkippableFact`). ✅
