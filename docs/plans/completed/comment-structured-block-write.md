# Plan: Comment structured-block write path — @-mention tags (#322, H)

Part of epic **#313** (Task Detail & Comments UX). Executes the schema/regen/facade plan
handed down by the **G** spike (#321, `docs/plans/completed/comment-mention-write-spike.md`),
and is **consumed by K** (#325, the @-mention composer wiring).

Goal: let a comment posted from the app carry **@-mention tag blocks** — so the #325 composer
can `@`-tag workspace members — while leaving the existing plain-text write path exactly as it
is today. No TUI change lands here; this is spec + regen + facade + tests only.

## What G already settled (so this issue doesn't re-litigate it)

From the spike (high confidence):

- ClickUp accepts a structured **`comment` blocks array** on `POST /v2/task/{task_id}/comment`
  (and identically on `POST /v2/comment/{comment_id}/reply`). A plain run is `{ "text": "…" }`;
  an @-mention run is `{ "type": "tag", "user": { "id": <numericUserId> } }`.
- Send the blocks array **by itself** (no `comment_text`) when there is a mention;
  `comment_text` is not required alongside blocks. `notify_all` stays a top-level sibling.
- The tag block needs **only** `type` + `user.id` (no `attributes`, no `text`). The read-side
  `CommentBlock` (#167) already models exactly this, so the write reuses it.
- @Brain / Super Agents are **not** mentionable via v2 (no member id) — the picker (#324/#325)
  surfaces human members only; nothing to model here.

## Phase 1 — Spec + regen

Edit the curated spec **`src/ClickUpTodo/ClickUp/clickup-openapi.json`** (never hand-edit
`Generated/`), then regenerate.

1. On `components.schemas.CreateCommentRequest`:
   - Add a `comment` property: a nullable array of `$ref: CommentBlock`.
   - **Drop `comment_text` from `required`** (either the text or the blocks satisfy the body;
     the facade guards "at least one non-empty" at the boundary).
   - Refresh the schema `description` to document the two mutually-exclusive bodies.
2. Reuse the existing `CommentBlock` component as-is. The optional forward-compat `attributes`
   bag is **deferred** (not needed for mentions; keeps the generated surface minimal).
3. Regenerate:
   ```bash
   dotnet tool restore
   pwsh scripts/regen-client.ps1          # pwsh absent in some envs → run the underlying
   # dotnet kiota generate --language CSharp --openapi src/ClickUpTodo/ClickUp/clickup-openapi.json \
   #   --class-name ClickUpApiClient --namespace-name ClickUpTodo.ClickUp.Generated \
   #   --output src/ClickUpTodo/ClickUp/Generated --clean-output --exclude-backward-compatible
   ```
4. **Verify the emitted member name** on `CreateCommentRequest`. Kiota may mangle `comment` to
   avoid colliding with the sibling `Comment` type (the read side became `CommentProp`, see the
   #167 plan). Bind the facade to whatever Kiota actually emits — do **not** assume `Comment`.
   Confirm no unexpected churn under `Generated/`; commit only regen output.

## Phase 2 — Domain run type + facade overload

The app must never see generated types (README rule), so introduce a small **domain** run type
in `ClickUp/Models.cs`:

```csharp
public abstract record CommentRun
{
    private CommentRun() { }                 // closed union: only the nested cases derive
    public sealed record Text(string Value) : CommentRun;
    public sealed record Mention(long UserId) : CommentRun;
}
```

The #325 composer builds `[new CommentRun.Text("hi "), new CommentRun.Mention(183)]` without
touching Kiota models.

Add a facade overload on `ClickUpClient` (+ the `IClickUpClient` seam, as a **default-throwing**
member so existing read-only fakes need not implement it — mirrors the other write additions):

```csharp
Task<CommentItem> CreateTaskCommentAsync(string taskId, IReadOnlyList<CommentRun> runs, CancellationToken ct = default);
```

Behaviour:

- Map each `CommentRun.Text` → `new CommentBlock { Text = v }`; each `CommentRun.Mention` →
  `new CommentBlock { Type = "tag", User = new User { Id = userId } }`. Kiota omits unset
  properties, so a mention block serializes to exactly `{ "type":"tag", "user":{ "id":183 } }`.
- Send `{ "comment": [...] }` with `notify_all=false`; **do not** set `comment_text`.
- **Guard at the boundary**: reject a null/empty run list, and reject a run list whose text runs
  are all empty/whitespace *and* that carries no mention (mirrors the existing
  `ThrowIfNullOrWhiteSpace` on the plain path — since `comment_text` left `required`, an empty
  body would otherwise reach ClickUp).
- Record the same `CommentFields` change-marker nudge (#294) as the plain path.
- Build the returned `CommentItem` from the response `id`/`date` (same minimal-response contract
  and id-stringify quirk as the plain overload) plus a **flattened text preview** of the runs
  (`Text` runs verbatim; a `Mention` renders as a stable placeholder token so the optimistic row
  reads sensibly until the next fetch reconciles the real `@Name`). `Author` left empty; the
  mentioned ids are surfaced on `CommentItem.MentionedUserIds` so an optimistic row already knows
  who was tagged.

The existing plain-text `CreateTaskCommentAsync(taskId, text, ct)` is untouched.

## Phase 3 — Tests

Mirror `ClickUpClientCommentCreateTests` (capturing `HttpMessageHandler`, offline, no token):

- `[Run("hi "), Mention(183)]` → body `{ "comment":[{"text":"hi "},{"type":"tag","user":{"id":183}}], "notify_all":false }`,
  POST, `/v2/task/{id}/comment`, and **no** `comment_text` key.
- A single `Mention` with no surrounding text → exactly one `{type:"tag",user:{id}}` block, no
  stray `text`/`username`/`email`.
- The returned `CommentItem` carries the response `id`/`date`, a text preview, `TaskId` stamped,
  and `MentionedUserIds` containing the tagged id.
- Null/empty run list, and an all-whitespace-text-with-no-mention list, throw at the boundary
  **without a network call** (handler asserts it was never reached).
- **Integration** (`SkippableFact`, gated on `CLICKUP_TOKEN` + a task id): post a comment
  mentioning the authenticated user (`GetMeAsync().Id` — a self-mention so no colleague is
  notified) and re-fetch to confirm `MentionedUserIds` contains that id. This is the spike's
  confirmation gate that a bare `{type:"tag",user:{id}}` (no `attributes`) really materializes a
  mention. Self-skips without credentials.

## Deferred (belongs to the consumer, K/#325)

- The E2E PTY fake backend's `POST comment` handler currently ignores the request body and
  returns a canned create response, so a structured post already succeeds against it. Teaching it
  to **echo** the structured `comment` array back is only exercised by K's `tui-validate`
  read-back assertion, so it lands with the composer (#325), not here.
- Reply-mode structured posts (`POST /comment/{id}/reply`) get the `comment` blocks array for
  free from the shared `CreateCommentRequest` (relevant to #330), but no reply-composer wiring is
  in scope for H.

## Invariants respected

- **No `Generated/` hand-edits** — spec edit + regen only.
- **App never sees generated types** — the composer speaks `CommentRun`; mapping to `CommentBlock`
  happens inside the facade.
- **Auth quirk untouched** — raw `Authorization` header, no `Bearer`.
- **Plain-text path unchanged** — the new overload is purely additive.
- **Tests land with the code**; integration tests are `SkippableFact`, env-gated.
