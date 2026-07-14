# Plan — Writing New Content (C): task description write (#211)

Part of Epic #208. **Foundation — no UI dependency.** Unblocks the Task Detail
`Ctrl+E` description editor (#217).

## Goal

Add the ability to **edit a task's description** through the facade. Today
`PUT /task/{id}` is generated and the facade drives it for status / priority /
assignees, but the write DTO `UpdateTaskRequest` only carries `assignees`,
`priority`, `status` — there is no `description` (or `name`) to send.

## Design decisions

### 1. Spec-regen (preferred) over `AdditionalData`
Add the **regenerated typed field** (the issue's stated preference) rather than
threading through `AdditionalData`. Add both `description` and `name` to the
`UpdateTaskRequest` component schema (the same `PUT` covers a future rename, so
`name` is added now for forward-compat even though no facade method drives it
yet). `AdditionalData` stays the right tool only for the explicit-`null` clear
case (priority), which does not apply to description here (see §3).

### 2. Plain text, not markdown
ClickUp exposes `description` (plain) and `markdown_description` (markdown). The
detail view reads description as `MapDetail`:
`!IsNullOrWhiteSpace(text_content) ? text_content : description` (both plain). To
keep **read → edit → write → re-read lossless for plain text** and consistent
with what the UI shows, `SetTaskDescriptionAsync` writes the plain
**`description`** field. Markdown authoring is out of scope — noted in the PR.

### 3. No explicit-null clear needed
Description is a plain string: passing an empty string writes `"description": ""`
(clears it); passing text sets it. No `AdditionalData` null dance needed.
`SetTaskDescriptionAsync` takes a non-null `string description` (empty clears).

### 4. Return-the-truth
Mirror `SetTaskStatusAsync`: return the **server-confirmed** description from the
`PUT` response, mapped the same way the detail view maps it (`text_content`
preferred, `description` fallback). Signature:
`Task<string?> SetTaskDescriptionAsync(string taskId, string description, CancellationToken ct = default)`.

## Phases

1. **Spec + regen.** Add `description` (nullable string) and `name` (string) to
   `components.schemas.UpdateTaskRequest`; regenerate via `dotnet kiota generate`
   (same args as `regen-client.ps1`; `pwsh` absent). Verify only
   `UpdateTaskRequest.cs` + `kiota-lock.json` change under `Generated/`.
2. **Facade.** Add `SetTaskDescriptionAsync` to `IClickUpClient` + `ClickUpClient`.
3. **Tests.** Unit via capturing `HttpMessageHandler` (mirror
   `ClickUpClientWriteTests`): `PUT /v2/task/{id}` body carries `description` as a
   JSON string, doesn't touch status/priority/assignees, return comes from the
   response (not an echo). Integration `SkippableFact` (`CLICKUP_TOKEN` +
   `CLICKUP_TASK_ID`): set a marked description, re-fetch, assert round-trip;
   restore original in `finally`.

## Quality gate (each phase)
`dotnet build -c Release` (0 warn/0 err) → `dotnet test -c Release` (green) → `dotnet format`.

## Invariants
- No hand edits under `Generated/`. Raw `Authorization` header untouched.
- Integration tests `SkippableFact`, env-gated. No TUI change this slice.

## Coordination
No overlap with in-flight PRs. #219 adds a *new* `CreateTaskRequest`, not
`UpdateTaskRequest`. #217 (Ctrl+E editor) will consume this method.

## Deferred (tracked)
- `Ctrl+E` description editor UI → #217.
- Markdown (`markdown_description`) authoring → out of scope.
- `SetTaskNameAsync` rename facade → future; DTO `name` field added now.
