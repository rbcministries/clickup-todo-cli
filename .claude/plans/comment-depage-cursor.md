# Plan: De-page task comments via ClickUp's start/start_id cursor (#130)

Deferred follow-up from #111 (`feed-comment-fetch.md`). #111 shipped a **single-page**
`ClickUpClient.GetTaskCommentsAsync` because the curated spec didn't model the comment
endpoint's pagination cursor, so the generated `CommentRequestBuilder.GetAsync` took only
`DefaultQueryParameters`. This issue adds the cursor to the spec, regenerates, and wraps the
fetch in a cursor-driven de-paging loop so a busy task's full comment history reaches the
feed (#112).

## Why the existing `PageAsync` helper doesn't apply

`PageAsync` is **task-typed** (`Func<int, Task<TasksResponse?>>` → `List<TaskItem>`) and
**page-number** driven (`?page=0,1,…`, stop on `last_page`/short page). ClickUp's
`GET /task/{task_id}/comment` instead returns the most-recent 25 comments and paginates by a
**cursor**: pass `start` (epoch-ms of the *oldest* comment you received) + `start_id` (that
comment's id) to get the next-older page. So this needs its own comment-typed, cursor-driven
de-pager.

## Phase 1 — Spec + regen

- Add two query parameters to `GET /v2/task/{task_id}/comment` in the curated
  `src/ClickUpTodo/ClickUp/clickup-openapi.json`:
  - `start` — `integer`, `format: int64` (epoch-ms of the oldest comment from the previous
    page). int64 because epoch-ms (~1.7e12) overflows int32; Kiota -> `long? Start`.
  - `start_id` — `string` (that comment's id). Kiota -> `string? StartId`.
- Regenerate with the pinned Kiota tool (pwsh isn't on PATH here, so invoke `dotnet kiota
  generate` directly with the exact args from `scripts/regen-client.ps1`).
  **No hand-edits under `Generated/`.**
- Verify the generated `CommentRequestBuilderGetQueryParameters` now exposes
  `[QueryParameter("start")] long? Start` and `[QueryParameter("start_id")] string? StartId`.

## Phase 2 — Cursor de-paging loop + tests

In `ClickUpClient` (all mapping/paging stays in the facade; no generated type escapes it):

- `internal readonly record struct CommentCursor(long Start, string StartId)` — the next-page
  anchor.
- `internal static CommentCursor? NextCommentCursor(IReadOnlyList<Comment> page)` — pure:
  scans from the end (comments are newest-first, so the oldest is last) for the first comment
  with a non-empty id and a parseable epoch-ms date, and returns its cursor. `null` when none
  qualifies (defensive; also the empty-page case).
- `internal static async Task<List<Comment>> DePageCommentsAsync(fetchPage, onCapReached, ct)`
  — cursor loop, testable offline via a fake `fetchPage` delegate returning constructed
  `CommentsResponse`s:
  - Accumulate, **de-duped by comment id** (a boundary cursor can re-return its anchor).
  - Stop on a short/empty page (`< CommentPageSize` => no older history), or when a full page
    yields **no unseen** comments (stuck cursor guard).
  - Stop at the `MaxCommentPages` cap, invoking `onCapReached` so the truncation is
    observable -- **not silent** (issue requirement). Documented on the method + constants.
  - `CommentPageSize = 25` (ClickUp's comment page size); `MaxCommentPages = 40` => <= 1000
    comments worst case.
- `GetTaskCommentsAsync` calls `DePageCommentsAsync`, threading `start`/`start_id` from the
  cursor into the generated query params, then maps via the unchanged `MapComment`.

### Tests (`ClickUpClientCommentDePageTests`, offline -- mirrors the mapper tests)

- `NextCommentCursor`: newest-first list -> oldest (last) comment's cursor; skips a
  trailing unparseable-date/blank-id comment; empty list -> null.
- `DePageCommentsAsync`: single short page -> one fetch, all returned; multi-page walk stops
  on the first short page and threads the derived cursor between calls; full page of all-seen
  ids terminates (no infinite loop); id de-dup across an overlapping boundary; cap reached ->
  stops at `MaxCommentPages` and fires `onCapReached`; cancellation observed.

`MapComment` is unchanged (existing `ClickUpClientCommentMapTests` still cover it). No TUI
surface touched. Integration tests remain `SkippableFact`/env-gated.

## Out of scope

Feed aggregation (#112) and the v3 client evaluation (#2). This issue only completes the
comment fetch's history coverage.
