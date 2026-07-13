# Plan — #210 Writing New Content (B): create-comment endpoint + `CreateTaskCommentAsync` facade

Part of the #208 "Writing New Content" epic. **Foundation — no UI dependency.** Unblocks the Task
Detail `Ctrl+N` comment composer (G, #216). No blockers (reading comments already works; only the
create operation is missing at every layer). Zero file overlap with the two in-flight PRs (#207
Quick Updates, #214 feed cache).

## Goal / acceptance (from the issue)

- `CreateTaskCommentAsync` posts a **plain-text** comment (`comment_text`) and returns it as a
  `CommentItem`.
- Client regenerated from the edited curated spec (`regen-client.ps1`); **no hand edits under
  `Generated/`**.
- `dotnet test` green; the live round-trip test is a `SkippableFact` that self-skips without
  `CLICKUP_TOKEN`.
- **Rich content out of scope:** @-mentions / task links / entity tagging are a follow-up epic. Send
  a plain-text body only; do not construct `comment` rich blocks.

## Verified current state

- `POST /task/{id}/comment` is **not** in the curated spec — the generated
  `CommentRequestBuilder` (`ClickUp/Generated/V2/TaskNamespace/Item/Comment/CommentRequestBuilder.cs`)
  exposes only `GetAsync`.
- Reading is in place: `GetTaskCommentsAsync` (`ClickUpClient.cs:320`, de-pages via
  `DePageCommentsAsync`) → `CommentItem` (`ClickUp/Models.cs:176`), mapper `MapComment`
  (`ClickUpClient.cs:397`).
- Write precedent + facade style: `SetTaskPriorityAsync` / `UpdateAssigneesAsync` (`ClickUpClient.cs`),
  all wrapped in the `Guard(operation, call)` helper.
- Offline write-test precedent: `ClickUpClientWriteTests` drives the **real** generated client through
  a capturing `HttpMessageHandler` (no token, no network), asserting the outgoing body shape and the
  parsed return — reusable here.

## Key API-shape finding (drives the design)

ClickUp v2 `POST /task/{task_id}/comment` request body is `{ "comment_text": "...", "notify_all": bool,
"assignee": id }`; only `comment_text` matters for this epic. Its **response is minimal** —
`{ "id": "<comment id>", "hist_id": "...", "date": <epoch-ms number> }` — it does **not** echo the
comment text, author, or structured blocks. So `MapComment` (which reads text/user/blocks off a full
`Comment`) cannot recover them from the create response.

Design decision (documented, deviates from the issue's "reuse `MapComment`" suggestion for a concrete
reason): build the returned `CommentItem` from the response `id`/`date` **plus the plain text we just
posted** (lossless for plain text). `Author` is left empty for the caller's optimistic row to stamp
(the UI knows "me") and is reconciled on the next comment fetch. This matches the optimistic-append
intent without an extra read-after-write round trip.

## Phases

### Phase 1 — Spec + regen + facade + unit tests (the whole slice)

This issue is small enough to land as one focused phase; commit + push opens the draft PR.

1. **Curated spec** (`src/ClickUpTodo/ClickUp/clickup-openapi.json`):
   - Add `CreateCommentRequest` schema: `comment_text` (string, required), `notify_all` (bool,
     nullable). Forward-compatible; `assignee` omitted (not needed).
   - Add `CreateCommentResponse` schema: `id` (string), `hist_id` (string, nullable), `date`
     (integer int64, nullable) — matching ClickUp's documented create response.
   - Add `post` to `/v2/task/{task_id}/comment`: `operationId: CreateTaskComment`, `task_id` path
     param, `requestBody` → `CreateCommentRequest`, `200` → `CreateCommentResponse`.
2. **Regen**: `dotnet kiota generate` via the pinned tool (equivalent of `regen-client.ps1`; pwsh is
   absent in this env). Verify only expected new/changed files under `Generated/`; no hand edits.
3. **Facade** (`IClickUpClient` + `ClickUpClient`):
   `Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default)`
   — posts `{ comment_text = text, notify_all = false }`, returns
   `new CommentItem(Id: resp.Id, Author: "", DateMs: resp.Date, Text: text, Resolved: false,
   TaskId: taskId)`, wrapped in `Guard("CreateTaskComment", …)`. XML-doc the minimal-response rationale.
4. **Unit tests** (`ClickUpClientCommentCreateTests.cs`, mirroring the capturing-handler pattern):
   - posts `comment_text` as a JSON string, method POST, URI `/v2/task/{id}/comment`;
   - returns the response `id`/`date` and **echoes the posted text** (text absent from the canned
     response proves it's echoed, not read back); `TaskId` stamped;
   - `notify_all` sent as `false`; body carries no rich `comment` blocks;
   - a minimal `{}` response → empty `Id`, null `DateMs`, text still echoed (no throw).
5. **Integration test** (`ClickUpClientIntegrationTests`): `SkippableFact` gated on `CLICKUP_TOKEN` +
   `CLICKUP_TASK_ID` — posts a comment, asserts a non-empty id comes back.

Quality gate from repo root: `dotnet build -c Release` (0/0), `dotnet test -c Release` (green,
integration self-skips), `dotnet format` (clean). Commit (standard footer) + push → draft PR.

## Out of scope / deferred

- Rich comment content (@-mentions, task links, entity tagging) → later epic; tracked under #208.
- The `Ctrl+N` compose/post UI itself → #216 (this issue is the facade it consumes).
- `assignee` / threaded-comment create → not needed for #208; omitted from the request DTO (schema is
  forward-compatible if a later issue needs them).
