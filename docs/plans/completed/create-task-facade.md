# Plan — Writing New Content (A): create-task endpoint + `CreateTaskAsync` facade (#209)

Part of Epic #208. **Foundation, no UI.** Unblocks the New Task screen (#213 E, #215 F).

## Goal

Add the ability to **create a task in a list** to the API facade:
`POST /v2/list/{list_id}/task`, carrying `name` (required), `description`, `assignees`
(`long[]`), `priority` (`int?`, ClickUp level 1–4), and `due_date` (epoch ms). Return the
created task mapped to the stable `TaskItem` domain record so the New Task screen can insert it.

## ClickUp v2 request shape (verified)

`POST /list/{list_id}/task` body — `assignees` is a **flat array of user ids** (unlike the
add/rem shape of the *update* endpoint), `priority` an int (1=Urgent…4=Low) or null, `due_date`
epoch ms. Response is the created task object (HTTP 200), same shape as `GET /task/{id}`.

```json
{ "name": "…", "description": "…", "assignees": [183], "priority": 3, "due_date": 1508369194377 }
```

## Hard rules honored

- **No hand edits under `Generated/`.** Edit the curated spec
  `src/ClickUpTodo/ClickUp/clickup-openapi.json`, then regenerate with Kiota.
- Generated types never escape the facade — map onto `TaskItem` via the existing `Map(TaskObject)`.
- New domain input is a small domain record (`NewTaskRequest`) in `Models.cs`, forward-compatible
  so the later Tags epic can add tags without a reshape.
- Personal-token raw `Authorization` header untouched. Integration test is a `SkippableFact`.

## Phases

### Phase 1 — spec + regen + facade + tests (single cohesive slice)

1. **Spec** (`clickup-openapi.json`):
   - Add a `post` operation to `/v2/list/{list_id}/task`: `operationId: CreateTask`, required
     `requestBody` → `$ref: CreateTaskRequest`, `200` response → `$ref: Task`.
   - Add component schema `CreateTaskRequest`: `required: [name]`; `name` string; `description`
     string nullable; `assignees` array of int64; `priority` int32 nullable; `due_date` int64
     nullable.
2. **Regenerate** the client: `dotnet tool restore` then
   `dotnet kiota generate --language CSharp --openapi <spec> --class-name ClickUpApiClient
   --namespace-name ClickUpTodo.ClickUp.Generated --output <Generated> --clean-output
   --exclude-backward-compatible` (equivalent to `scripts/regen-client.ps1`; pwsh is absent).
   Expect a new `Models/CreateTaskRequest.cs` and a `PostAsync` on the List task builder.
3. **Domain record** `NewTaskRequest` in `Models.cs`: `Name` (required), `Description`,
   `Assignees` (`IReadOnlyList<long>` = []), `PriorityLevel` (`int?`), `DueDateMs` (`long?`).
4. **Facade**: `Task<TaskItem> CreateTaskAsync(string listId, NewTaskRequest task, CancellationToken ct = default)`
   on `IClickUpClient` + `ClickUpClient`. Guard-wrapped (`"CreateTask"`). Maps the domain record to
   the generated `CreateTaskRequest` — omitting unset optionals (Kiota drops null typed props, so a
   null description/priority/due-date/empty-assignees sends no key), always sending `name`. Throws a
   clear `ArgumentException` on blank name before the round-trip. Maps the created `TaskObject`
   response via `Map`.
5. **Tests**:
   - `ClickUpClientCreateTaskTests` (offline, capturing `HttpMessageHandler` mirroring
     `ClickUpClientWriteTests`): asserts `POST` to `/v2/list/{id}/task`; full-field body shape;
     optional-field omission when unset; `assignees` is a flat int array (not add/rem); returned
     `TaskItem` mapped from the response (id/name/status/priority/assignees); blank-name throws.
   - Integration `SkippableFact` in `ClickUpClientIntegrationTests` gated on
     `CLICKUP_TOKEN`+`CLICKUP_LIST_ID`: create a throwaway task, assert it comes back with an
     id/name and the sent fields. Keep it minimal and self-skipping.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green, integration self-skips),
  `dotnet format`. No TUI surface touched → no `tui-validate` needed.

## Deferred (out of scope, tracked)

- New Task **screen/UI** and `Ctrl+N` launch → #213 (E).
- Optional Due Date + Priority **fields in the screen** → #215 (F).
- Status / tags on create, context-aware target list → later Tags epic / follow-up.
