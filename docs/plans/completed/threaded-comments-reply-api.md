# Threaded comments (A): model the reply-thread API + Kiota regen

Issue: #327 (part of epic #314 — Threaded comments). Foundation for **B** (#328,
load reply threads into the pipeline), **C** (#329, render), **D** (#330, post a
reply).

## Goal

Model ClickUp's threaded-comment endpoints in the curated spec, regenerate the
Kiota client, and add facade methods to **fetch** a comment's replies and
**post** a new reply — the API foundation the rest of the epic builds on. No
`Generated/` hand-edits; no TUI change.

## ClickUp v2 endpoints (verified against the public v2 reference)

- `GET  /v2/comment/{comment_id}/reply` → `{ "comments": [ <comment>, ... ] }`.
  Replies come back in the **same object shape** as flat task comments (`id`,
  `comment_text`, `comment` blocks, `user`, `date`, `resolved`). The reply
  endpoint is **not cursor-paginated** the way `/task/{id}/comment` is — it
  returns the thread's replies in one response (a thread is bounded by its parent
  comment), so the facade does a single fetch + map rather than de-paging.
- `POST /v2/comment/{comment_id}/reply` with body `{ "comment_text": "...",
  "notify_all": false }` → the **minimal** created-comment shape (`id`,
  `hist_id`, `date`) — identical to `POST /task/{id}/comment`. So the create
  response omits text/author/blocks and the facade echoes the posted text, exactly
  like `CreateTaskCommentAsync`.
- Parent comments expose **`reply_count`** (integer) on the flat task-comment
  object, letting a caller (B/C) know a comment has a thread worth fetching
  without a probe request.

## Changes

### 1. Curated spec (`src/ClickUpTodo/ClickUp/clickup-openapi.json`)

- Add `reply_count` (integer, nullable) to the `Comment` schema. Reused by both
  the flat and reply endpoints (same schema).
- Add path `/v2/comment/{comment_id}/reply`:
  - `get` — `GetThreadedComments`, path param `comment_id`; 200 → reuse
    `CommentsResponse` (`{ comments: [Comment] }`).
  - `post` — `CreateThreadedComment`, path param `comment_id`, body reuse
    `CreateCommentRequest`; 200 → reuse `CreateCommentResponse`.
- Reusing the existing component schemas keeps the generated surface minimal and
  the mapping identical (`MapComment`).

### 2. Regenerate the client

`dotnet tool restore` then the `scripts/regen-client.ps1` command
(`dotnet kiota generate …`, run directly since `pwsh` is unavailable in this
environment — same invocation). Produces a new `V2/Comment/{comment_id}/Reply`
builder; nothing hand-edited.

### 3. Facade (`ClickUp/ClickUpClient.cs`) + `IClickUpClient`

- `GetThreadedCommentsAsync(string commentId, CancellationToken ct = default)`
  → `IReadOnlyList<CommentItem>`. Single `GET …/reply` fetch, map each with the
  existing `MapComment` (TaskId null — the reply payload carries no task context;
  B stamps the parent's task at the call site). Reuses the `Guard` wrapper.
- `CreateThreadedCommentAsync(string commentId, string text,
  CancellationToken ct = default)` → `CommentItem`. Mirrors
  `CreateTaskCommentAsync`: guards empty text at the boundary, posts
  `comment_text` + `notify_all=false`, builds the returned `CommentItem` from the
  minimal response `id`/`date` plus the echoed text.
- `CommentItem` gains `ReplyCount` (int, default 0) so a caller can tell a
  comment has replies; `MapComment` reads it from the generated `reply_count`.
  Default keeps every existing construction/mapping site unchanged.

### 4. Tests

- **Unit (offline, capturing `HttpMessageHandler`)** — mirror
  `ClickUpClientCommentCreateTests`:
  - `CreateThreadedComment` posts to `/v2/comment/{id}/reply`, sends
    `comment_text` + `notify_all=false`, echoes the text, maps the minimal
    response; rejects empty/whitespace text without hitting the network.
  - `GetThreadedComments` fetches `/v2/comment/{id}/reply` and maps the reply
    list (id, author, text, date, resolved).
- **Unit — `MapComment` reply_count** — extends the map tests: `reply_count`
  surfaces as `ReplyCount`; absent → 0.
- **Integration (`SkippableFact`, `CLICKUP_TOKEN`-gated)** — mirror the flat
  comment integration tests: post a reply to `CLICKUP_COMMENT_ID` (skip when
  unset) and re-fetch the thread to find it; a read-only fetch test.

## Invariants

- **Generated client / curated spec** — new endpoint modelled in the curated
  spec + regen only; no `Generated/` hand-edit.
- **Auth quirk** — untouched (raw `Authorization`, no `Bearer`).
- **No TUI change** — API-layer only; no focusable pane, no keybinding, no
  rendering.
- **Tests skippable** — every live-API test is `SkippableFact` and skips without
  `CLICKUP_TOKEN`.

## Deferred (to later sub-issues, already tracked)

- Loading reply threads into the comment pipeline — **B / #328**.
- Rendering nested threads — **C / #329**.
- Posting a reply from the composer UI — **D / #330**.
- Extending the E2E fake backend with reply payloads lands with **B**, where a
  `tui-validate` scenario actually exercises the thread rendering (this API-only
  slice has no TUI surface to validate).
