# Per-task Custom Field write path (#587 §1)

Status: in flight — implements **§1** of #587 (the pure/CI-verifiable slice).
The Terminal.Gui-coupled **§2** (navigable rows) and **§3** (per-type
activation) are deferred to a follow-up (tracked on #587); they also want the
#537/#538 contextual-chord seam.

## Problem

Custom-field values can only be written at *create* time today
(`NewTaskRequest.CustomFields`, #368). On an existing task the Task Detail
**Other** tab renders custom fields strictly read-only — there is no facade to
change a value once a task exists. Before any UI can edit a field, the client
needs a write path.

## What ships here

A thin, tested write path mirroring the existing single-field task writes
(`SetTaskStatusAsync` / `SetTaskNameAsync` / the checklist writes):

### Curated spec + regen

Add to `src/ClickUpTodo/ClickUp/clickup-openapi.json`:

- `POST /v2/task/{task_id}/field/{field_id}` — **set** a value. Body
  `{ "value": … }`, where `value` is polymorphic (string / number / bool /
  array / object depending on the field type). ClickUp returns an empty object
  `{}`, so there is **no response schema** (like `DeleteTask`).
- `DELETE /v2/task/{task_id}/field/{field_id}` — **clear** a value. Empty
  response.

The set body is modelled as `SetCustomFieldValueRequest` — an object whose only
property `value` is untyped (no `type` in the schema), so Kiota carries it as an
`UntypedNode` we populate from the caller's `JsonElement` via the existing
`ToUntyped` helper (the same loosely-typed-value pattern `CreateTaskAsync`
already uses for `custom_fields`). No `Generated/` hand-edits — regen with
`dotnet kiota generate` (the `scripts/regen-client.ps1` body; pwsh not required).

### Facade (`ClickUpClient` + `IClickUpClient` + `TaskService`)

- `Task SetTaskCustomFieldAsync(string taskId, string fieldId, JsonElement value, CancellationToken ct = default)`
  → `POST /task/{task_id}/field/{field_id}` with `{ value }`. Rejects a blank
  `fieldId` client-side (an id is required to address the field). On a confirmed
  2xx, records a change-marker nudge (#294) keyed by `taskId` with a **null**
  server date (the empty response carries no `date_updated`, so the consumer
  always re-fetches — the "empty body ⇒ always re-fetch" shape the checklist /
  membership writes use).
- `Task ClearTaskCustomFieldAsync(string taskId, string fieldId, CancellationToken ct = default)`
  → `DELETE /task/{task_id}/field/{field_id}`. Same blank-id guard + change
  marker.
- `IClickUpClient` gets default-throwing declarations (matching the file's
  convention); `TaskService` gets passthroughs.
- New advisory field-hint constant `CustomFieldFields = ["custom_fields"]`.

## Tests

- **Unit** (`ClickUpClientCustomFieldWriteTests`, `CapturingHandler`): the set
  posts to `/v2/task/{id}/field/{fid}` with the JSON `value` kind preserved
  (string / number / bool / array / object / null), the clear issues a `DELETE`
  to the same path with no body, blank `fieldId` throws without hitting the
  transport, and both record the expected `custom_fields` change marker (null
  server date) only on success.
- **Integration** (`SkippableFact`, env-gated on `CLICKUP_TOKEN` +
  `CLICKUP_LIST_ID`): create a throwaway task, discover a fillable field via
  `GetListCustomFieldsAsync`, set a value, re-fetch and assert it round-trips,
  clear it, and delete the task in a `finally`. Skips cleanly when no token /
  list / no fillable text-like field is available, so CI stays green.

## Out of scope (deferred to #587 §2/§3)

- The Other-tab row model, per-type activation (Space/Enter/editor overlay),
  mouse wiring, required-clear validation, and `tui-validate` coverage. Those
  are Terminal.Gui-coupled and gated on the contextual-chord model (#538).
