# Threaded comments (B): load reply threads into the comment pipeline

Issue: #328 (part of epic #314 — Threaded comments). Builds on **A** (#327,
the reply-fetch/post facade + `reply_count` mapping, merged). Foundation for
**C** (#329, render nested threads) and **D** (#330, post a reply).

## Goal

Bring a parent comment's replies into the domain model and the comment-loading
path so the Task Detail Comments/Stream tabs have thread data to work with —
without rendering them yet (that's **C**/#329). Guarded and batched so comments
without replies incur no extra calls and a busy task doesn't trigger an N+1
fetch storm.

## Verified current state (repo)

- `CommentItem` (`ClickUp/Models.cs`) already carries `ReplyCount` (mapped from
  the wire `reply_count` by `ClickUpClient.MapComment`, #327) but **no reply
  linkage** — no parent id, no nested replies.
- `ClickUpClient.GetThreadedCommentsAsync(commentId)` (#327) already fetches a
  thread's replies (single `GET /comment/{id}/reply`, mapped via `MapComment`,
  `TaskId` left null for the caller to stamp). Exposed on `IClickUpClient` with a
  throwing default so existing fakes still compile.
- Detail-view comments load flat via `TaskService.GetTaskCommentsAsync` →
  `client.GetTaskCommentsAsync`, called from `TodoApp.OpenTaskDetail` /
  `RefreshDetail`, `SingleTaskApp` refresh, and `Program` single-task boot.
- The feed (`FeedService`) fans comment fetches out over assigned tasks via the
  `internal static`, delegate-driven, `SemaphoreSlim`-bounded `GatherAsync` —
  the idiom this slice mirrors for its own bounded reply fan-out.

## Changes

### 1. Model (`ClickUp/Models.cs`)

`CommentItem` gains:

- `ParentCommentId` (`string?`, default `null`) — set on a **reply** item to the
  id of the comment it answers; `null` on a top-level comment.
- `Replies` (`IReadOnlyList<CommentItem>`, default `[]`) — a parent's loaded
  replies, oldest-first; empty when the comment has no thread or replies weren't
  loaded. Given the same by-reference-equality caveat already documented on
  `MentionedUserIds` (no consumer relies on `CommentItem` value equality; the
  feed de-dups by `Id`).

Defaults keep every existing construction/mapping site unchanged.

### 2. Loader (`Services/CommentThreadLoader.cs`) — pure, testable seam

`internal static Task<IReadOnlyList<CommentItem>> LoadRepliesAsync(comments,
fetchReplies, maxConcurrency, ct)`, delegate-driven and `SemaphoreSlim`-bounded,
mirroring `FeedService.GatherAsync`:

- Only comments with `ReplyCount > 0` **and** a non-empty `Id` trigger a fetch;
  every other comment is returned unchanged and **incurs no call** (asserted in
  tests via a call-recording fake).
- Each fetched reply is stamped with `ParentCommentId = parent.Id` and
  `TaskId = parent.TaskId` (a reply payload carries no task context; #327 leaves
  it null on purpose), then ordered oldest-first by `DateMs` (ties by `Id`) for a
  deterministic, top-down thread order.
- Best-effort per thread: a reply fetch that throws yields the parent with empty
  `Replies` rather than failing the whole load (mirrors `GatherAsync`); genuine
  caller cancellation (`ct`) propagates.
- Order and identity of the input comment list are preserved.

### 3. Pipeline (`Services/TaskService.cs`)

`GetTaskCommentsWithRepliesAsync(taskId, ct)`: fetch the flat comments via the
client, then enrich them through `CommentThreadLoader.LoadRepliesAsync` using
`client.GetThreadedCommentsAsync` as the fetcher, bounded by a
`DefaultMaxReplyConcurrency` const. Returns the same flat top-level list, now
with `Replies` populated on parents that have them.

### 4. Wire the detail-view load path

Swap the four detail-view comment loads (`TodoApp.OpenTaskDetail`,
`TodoApp.RefreshDetail`, `SingleTaskApp` refresh, `Program` single-task boot)
from `GetTaskCommentsAsync` to `GetTaskCommentsWithRepliesAsync`. This is a
data-pipeline swap only — the current renderer ignores `Replies`, so there is no
visual change until **C**/#329. All four sites already fetch off the UI thread.

### 5. Tests

- **`CommentThreadLoaderTests`** (pure, in-memory recording fetcher):
  replies attach to parents with `ReplyCount > 0`; `ReplyCount == 0` (and
  empty-id) comments trigger **no** fetch; `ParentCommentId`/`TaskId` stamped;
  oldest-first reply order; best-effort on a throwing fetch; input order
  preserved; `maxConcurrency` never exceeded; caller cancellation propagates.
- **`TaskServiceThreadedCommentsTests`** (fake `IClickUpClient`):
  `GetTaskCommentsWithRepliesAsync` fetches flat comments then the threads for
  the ones with replies, and returns them enriched.

## Invariants

- **No `Generated/` hand-edit; no curated-spec change** — #327 already modelled
  the endpoint; this slice is model + service + wiring only.
- **Auth quirk** untouched.
- **No TUI structural change** — no new focusable pane, no keybinding, no
  rendering change (rendering is #329); a single data-source swap on existing
  off-thread loads.
- **Tests** — pure unit tests, no live API; nothing env-gated added here.

## Deferred (tracked)

- Rendering nested threads in the Comments/Stream tabs — **C / #329**
  (a `tui-validate` scenario exercising thread rendering lands there, with the
  E2E fake backend extended with reply payloads).
- Posting a reply from the composer — **D / #330**.
- Thread data in the **feed** view: the feed already carries an accurate
  `ReplyCount` per comment (via `MapComment`) with no extra calls; a feed-wide
  reply fan-out is deliberately **not** done here (it would be an unbounded
  storm across all assigned tasks) and is left to #329's render decisions.
