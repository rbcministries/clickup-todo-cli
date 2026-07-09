# Quick Updates: API facade — priority + assignee write methods (#154)

Part of the Quick Updates epic (#153). **Foundation — do first. No dependencies.** No UI in this
issue: pure facade plumbing that the screen shell (#156), the Status/Priority panes (#157) and the
Assignees pane (#158) build on.

## Goal

The facade (`IClickUpClient` / `ClickUpClient`, over the Kiota client in `ClickUp/Generated/`) exposes
**only** `SetTaskStatusAsync` for mutations today. Add write methods for **priority** and
**assignees**, behind the same seam the rest of the app is tested against, returning the
server-confirmed truth (mirroring `SetTaskStatusAsync`'s return-the-truth shape).

## API surface (added to both `IClickUpClient` and `ClickUpClient`)

- `Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default)`
  — set the priority to ClickUp's `1`..`4` importance level (Urgent…Low; lower = more urgent), or
  clear it when `priorityLevel` is `null`. Returns the server's **effective** importance level
  (read back from the PUT response via `ClickUpPriority.Level`), or null when cleared/unset.
- `Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default)`
- `Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default)`
  — ClickUp's `PUT /task/{id}` takes `assignees: { add: [...], rem: [...] }`. Each returns the task's
  **reconciled** assignee set from the response (`MapAssignees`), so a caller can reconcile the row.

## Spec change + regen (never hand-edit `Generated/`)

The curated `UpdateTaskRequest` schema in `src/ClickUpTodo/ClickUp/clickup-openapi.json` carries only
`status`. Add:

- `priority`: `{ "type": "integer", "format": "int32", "nullable": true }` — ClickUp's importance level.
- `assignees`: `$ref` a new `AssigneeUpdate` component `{ add: int64[], rem: int64[] }` (user ids are
  `integer/int64`, matching the `User.id` shape already in the spec).

Then regenerate:

```bash
dotnet tool restore
dotnet kiota generate --language CSharp --openapi src/ClickUpTodo/ClickUp/clickup-openapi.json \
  --class-name ClickUpApiClient --namespace-name ClickUpTodo.ClickUp.Generated \
  --output src/ClickUpTodo/ClickUp/Generated --clean-output --exclude-backward-compatible
```

(equivalent to `scripts/regen-client.ps1`; `pwsh` isn't on PATH in this environment).

### Clearing priority — Kiota null-omission caveat

Kiota's JSON writer **omits** a null typed nullable property, so `new UpdateTaskRequest { Priority = null }`
would serialize an empty body and *not* clear the priority. To send an explicit `"priority": null`,
the clear path writes it through `AdditionalData["priority"] = null` (Kiota's `WriteAdditionalData` →
`WriteNullValue` emits the JSON null). A serialization unit test pins both the set body (`"priority":<n>`)
and the clear body (`"priority":null`), and that only the relevant assignee side is present.

## Mapping

- Priority return: `ClickUpPriority.Level(updated?.Priority?.Id, updated?.Priority?.PriorityProp)`.
- Assignee return: reuse the existing private `MapAssignees(updated?.Assignees)`.
- All three go through the existing `Guard("UpdateTask", …)` wrapper (translates Kiota `ApiException`).

## Tests

`tests/ClickUpTodo.Tests/`:

1. **Request-body shape** (offline, capturing `HttpMessageHandler` like `ClickUpClientAuthSeamTests`):
   - `SetTaskPriorityAsync(_, 2)` → body `"priority":2`; `SetTaskPriorityAsync(_, null)` → body
     `"priority":null`.
   - `AddTaskAssigneeAsync(_, 123)` → body `assignees.add == [123]`, no `rem`; `RemoveTaskAssigneeAsync`
     → `assignees.rem == [123]`, no `add`.
2. **Response mapping** (canned updated-Task JSON): priority level read back for each of Urgent/High/
   Normal/Low and cleared→null; assignee set reconciled from the response.
3. Extend the `FakeClickUpClient` in `ResolveForeignSubtasksTests` to implement the three new interface
   members (stubbed — unused there) so the suite compiles.
4. Optionally an env-gated `SkippableFact` integration test if a `CLICKUP_TOKEN`/task id is present,
   mirroring the existing `ClickUpClientIntegrationTests` status round-trip (set→read→restore).

`ClickUpPriorityTests` already covers the level↔name mapping.

## Constraints honoured

- No `Generated/` hand-edits; the spec is the source of truth and is regenerated.
- Personal-token raw `Authorization` header untouched.
- Integration tests remain `SkippableFact`, env-gated.
- No TUI change (no rendering, no focusable pane).
