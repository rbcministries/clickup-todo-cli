# Plan — Writing New Content (C): task description write (#211)

Part of Epic #208. **Foundation — no UI.** Unblocks the Task Detail `Ctrl+E` description
editor (#217).

## Goal

Add the ability to write a task's description through the facade:
`SetTaskDescriptionAsync(taskId, description, ct)`. The `PUT /task/{id}` endpoint is already
generated and driven for status/priority/assignees; its request DTO (`UpdateTaskRequest`) just
can't carry a description yet.

## Key decisions

### Spec edit + regen (not `AdditionalData`)

The issue allows either the regenerated field or the `AdditionalData` route, and asks to prefer
the regenerated field. We take the **spec + regen** path:

- The curated spec is the documented source of truth (README + CLAUDE.md); every other write field
  (`status`, `priority`, `assignees`) is a typed property on `UpdateTaskRequest`. A typed
  `Description` matches `SetTaskStatusAsync`'s clean `new UpdateTaskRequest { Status = ... }` style.
- The spec change is tiny and isolated (two string properties on one existing schema). The two
  other in-flight regen PRs (#218 create-comment, #219 create-task) touch *different* schemas/paths,
  so the only overlap is `kiota-lock.json`'s hash — a trivial re-run-regen-after-rebase conflict,
  not a code conflict.
- `AdditionalData` stays reserved for the one case a typed property can't express — the explicit
  `"priority": null` clear (`SetTaskPriorityAsync`).

Add both `description` **and** `name` to the DTO (the issue asks for `name` too, since the same
`PUT` will back a future rename). Only `description` gets a facade method now; `name` is
forward-compatible DTO surface with no consumer yet.

### Plain text, not markdown

ClickUp exposes `description` (plain) and `markdown_description` (markdown). The detail view reads
plain text today (`TaskDetail.Description = text_content ?? description`, `MapDetail`). We **write
the plain `description` field** so a read → edit → write → re-read round-trip is lossless for plain
text (no markdown re-interpretation). `markdown_description` is out of scope.

### Return-the-truth

Mirror `SetTaskStatusAsync`: return `Task<string?>` — the **server-confirmed** description parsed
from the `PUT` response (`text_content` preferred, falling back to `description`, matching
`MapDetail`), so a caller can show the confirmed value without a read-after-write. Empty string is a
legitimate value (clear the description) and is sent as `"description": ""` (Kiota writes a non-null
string). A `null` argument is rejected up front — callers clear with `""`, not null — so the method
never silently no-ops (Kiota omits a null typed property).

## Phases

### Phase 1 — spec + regen + facade + unit tests
1. Edit `clickup-openapi.json`: add `description` (string) and `name` (string) to the
   `UpdateTaskRequest` schema.
2. `dotnet tool restore` + regenerate (`dotnet kiota generate …`, the body of `regen-client.ps1`;
   `pwsh` is absent in this env). Expect only `UpdateTaskRequest.cs` + `kiota-lock.json` to change.
   **No hand edits under `Generated/`.**
3. Add `SetTaskDescriptionAsync` to `IClickUpClient` and implement it on `ClickUpClient`
   (guard `ArgumentNullException` on null; `new UpdateTaskRequest { Description = description }`;
   read back `text_content ?? description`).
4. Unit tests in `ClickUpClientWriteTests` (capturing `HttpMessageHandler`, no token/network):
   - `PUT /v2/task/{id}` with a JSON string `description` body; does **not** touch
     status/priority/assignees.
   - Returns the **server-confirmed** description from the response (distinct request vs response
     text proves it's read back, not echoed); prefers `text_content` over `description`.
   - Empty-string description is sent as `"description": ""` (a real clear), not omitted.
   - Null argument throws `ArgumentNullException` before any transport call.

### Phase 2 — integration test + finalize
1. `SkippableFact` in `ClickUpClientIntegrationTests`, gated on `CLICKUP_TOKEN` + `CLICKUP_TASK_ID`:
   write a uniquely-marked description, then `GetTaskDetailAsync` and assert it round-trips.
   Restore-friendly (writes a clearly-labelled marker string).
2. Full gate: `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format`.

## Invariants
- Generated client **regenerated, not hand-edited**.
- Personal-token raw `Authorization` header untouched.
- No TUI change (no second focusable pane, no bare-letter keybindings) — this slice is facade-only.
- Integration test is `SkippableFact`, env-gated.

## Deferred (already tracked)
- The Task Detail `Ctrl+E` description **editor UI** → #217 (H).
