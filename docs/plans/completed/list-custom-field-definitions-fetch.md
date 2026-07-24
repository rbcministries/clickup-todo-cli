# Plan — Writing New Content (Q): fetch a list's Custom Field definitions (#249, slice 1)

Part of Epic #208, first slice of #249. **Foundation, no UI.** Unblocks the New Task
custom-field **input widgets** and **required-field enforcement** (deferred follow-up,
which will build on top of the in-flight New Task multi-list work in #366).

## Why this slice

#249 is large: it spans (§0) a spike, (§1) an API surface for reading field definitions
**and** writing values on create, (§2) per-type New Task input widgets, and (§3)
required-field enforcement. §2/§3 heavily edit `NewTaskScreen`, which open PR #366 is also
rewriting — so landing UI now would collide. This slice ships the one piece that is fully
independent of the UI and of #366: **reading a list's custom-field definitions** (their
`id`, `name`, `type`, `required` flag, and drop-down/label `options`). It is the
prerequisite every later part of #249 consumes, and it is unit-testable end-to-end with no
terminal surface.

## §0 spike result (recorded on the issue, drives §3 later)

Empirically (read-only, against real lists) **`GET /list/{list_id}/field` returns a
`required` boolean on every field object** — contradicting the issue's caveat that the flag
may be absent from the public API. So client-side required-enforcement is viable and will be
built in the deferred UI slice. The response also carries `type_config.options` for
`drop_down` (`{id,name,color,orderindex}`) and `labels` (`{id,label,color,orderindex}`),
matching the taxonomy `TaskDetailFormatter.CustomFieldValue` already handles on the read/detail
side.

## ClickUp v2 request shape (verified live)

`GET /v2/list/{list_id}/field` → `{ "fields": [ { id, name, type, type_config{…}, required,
hide_from_guests, date_created } … ] }`. Only the stable identity + `required` are typed in the
curated spec; the loosely-typed `type_config` (whose `options` sub-array shape varies by type)
is left to Kiota `AdditionalData` and read back with `System.Text.Json`, exactly as the
existing task-`CustomField` read path does (issue #35).

## Hard rules honored

- **No hand edits under `Generated/`.** Add the endpoint + schemas to the curated spec
  `src/ClickUpTodo/ClickUp/clickup-openapi.json`, then regenerate with Kiota
  (`dotnet tool restore` + `scripts/regen-client.ps1`, or the equivalent `dotnet kiota`
  invocation when `pwsh` is unavailable).
- Generated types never escape the facade — map onto a new stable `CustomFieldDefinition`
  domain record.
- Reuse the existing `CustomFieldReader` options-reading (identical `type_config.options`
  logic) rather than duplicating it.
- Personal-token raw `Authorization` header untouched. Any credentialed test is a
  `SkippableFact` gated on `CLICKUP_TOKEN`.

## Phases

### Phase 1 — spec + regen + model + facade + tests (single cohesive slice)

1. **Spec** (`clickup-openapi.json`):
   - Add a `get` operation to a new path `/v2/list/{list_id}/field`:
     `operationId: GetListCustomFields`, `list_id` path param, `200` → `$ref:
     CustomFieldsResponse`.
   - Add component schema `CustomFieldDefinition`: `id` string, `name` string nullable,
     `type` string nullable, `required` boolean nullable, `hide_from_guests` boolean
     nullable. (`type_config` intentionally **not** listed → lands in `AdditionalData`.)
   - Add component schema `CustomFieldsResponse`: `{ fields: [CustomFieldDefinition] }`.
2. **Regenerate** the client. Expect `Models/CustomFieldDefinition.cs`,
   `Models/CustomFieldsResponse.cs`, and a `Field` request builder with `GetAsync` on the
   List builder.
3. **Domain record** in `Models.cs`:
   `CustomFieldDefinition(string Id, string Name, string? Type, bool Required,
   IReadOnlyList<CustomFieldOption> Options)` — reusing the existing `CustomFieldOption`
   record. Documents which types are fillable vs read-only/computed for the later widget
   slice (informational XML doc only; no behaviour here).
4. **Reader**: expose the options-reading already inside `CustomFieldReader` (extract the
   private `ReadOptions` into a public method, keeping `Read` delegating to it) so the facade
   can read a definition's options from its re-serialized JSON without duplicating logic.
5. **Facade**: `Task<IReadOnlyList<CustomFieldDefinition>> GetListCustomFieldsAsync(string
   listId, CancellationToken ct = default)` on `IClickUpClient` + `ClickUpClient`,
   guard-wrapped (`"GetAccessibleCustomFields"`). Maps each generated definition via a new
   internal `MapCustomFieldDefinition` that mirrors `MapCustomField`: re-serialize to
   `JsonElement` (existing `SerializeToJson` seam) and read `options`; take `id/name/type/
   required` from the typed props (`required` defaulting to `false` when null). Skips entries
   with a blank `id` (unusable downstream). Degrades a malformed single field to
   name/type-only rather than sinking the whole fetch.
6. **Tests**:
   - `ClickUpClientCustomFieldDefinitionsTests` (offline, `JsonParseNodeFactory` round-trip
     mirroring `ClickUpClientCustomFieldTests`): a drop-down definition surfaces `Required`
     and its `options` (id+name); a `labels` definition maps `label`→option name; a scalar
     (`text`/`number`) definition has empty options; `required` absent ⇒ `false`; a
     malformed `type_config` degrades gracefully.
   - `ClickUpClientListCustomFieldsFetchTests` (offline, capturing `HttpMessageHandler`
     mirroring `ClickUpClientCreateTaskTests`): asserts a `GET` to `/v2/list/{id}/field` and
     that the response maps to the domain list (count, ids, required, options).
   - Integration `SkippableFact` in `ClickUpClientIntegrationTests` gated on
     `CLICKUP_TOKEN`+`CLICKUP_LIST_ID`: fetch the list's fields and assert the call
     succeeds and each returned definition has a non-empty id/type. Minimal, self-skipping.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green, integration self-skips),
  `dotnet format`. No TUI surface touched → no `tui-validate` needed for this slice.

## Deferred (out of scope this slice, tracked in a new follow-up issue)

- **§1 write path** — `custom_fields` array on the create request + a pure, unit-tested
  per-type value-serialization helper. Deferred because each type's create-payload value
  shape (drop-down option id, labels id array, date epoch-ms + time flag, users id list, …)
  is best co-developed with, and validated by, the widgets that produce those values.
- **§2 New Task input widgets** — per-type widgets rendered after the target list is chosen,
  re-fetched when the list changes. Deferred to build on top of #366's `NewTaskScreen`
  rewrite (avoids a merge collision).
- **§3 required-field enforcement UI** — block Save until required fields have values (now
  known viable from the §0 spike), plus surfacing any server-side 4xx as a fallback.
