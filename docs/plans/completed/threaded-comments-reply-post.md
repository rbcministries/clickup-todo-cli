# Threaded comments (D): post a reply into a thread (#330)

Sub-issue **D** of the Threaded comments epic (#314). Depends on **A** (#327, the create-reply
facade) and **B** (#328, thread linkage on `CommentItem`) — both merged — and renders through **C**
(#329, nested reply rendering). This adds the **write** side: a user can reply *into* a specific
comment's thread from Task Detail, not just post a top-level comment.

## Verified current state (repo)

- **Facade is done (A).** `IClickUpClient.CreateThreadedCommentAsync(commentId, text, ct)` and its
  `ClickUpClient` implementation (`ClickUp/ClickUpClient.cs:725`) already post a plain-text reply
  (`POST /comment/{comment_id}/reply`, `notify_all=false`) and return the new reply as a
  `CommentItem` for optimistic append (minimal-response echo, same id-stringify quirk as
  `CreateTaskCommentAsync`). `TaskService` exposes `CreateTaskCommentAsync` (`:715`) but **no**
  reply passthrough yet.
- **Model carries threads (B).** `CommentItem` has `ParentCommentId` (set on a reply) and `Replies`
  (a parent's loaded replies, oldest-first). The detail screen's `_comments` is the **top-level**
  list; each parent carries its `.Replies`, loaded by `TaskService.GetTaskCommentsWithRepliesAsync`
  at all four detail load sites — so the screen already has thread data in both hosts.
- **Render is nested (C).** `TaskDetailFormatter.Thread` flattens a parent + its `.Replies`
  (recursively, `InActivityOrder`) with `ReplyMarker "↳ "` and per-depth indent. A comment with no
  replies is byte-identical to its old `CommentBlock`. ClickUp threads are one level deep.
- **Composer posts top-level only.** `Ctrl+N` → `ShowCommentComposer` → `PostComment`
  (`TaskDetailScreen.cs:1369`) appends a provisional via `CommentComposerModel.Provisional/Append`,
  writes through the injected `_postCommentAsync(text, ct)` (wired `TodoApp.cs:2018` and
  `SingleTaskApp.cs:250` to `TaskService.CreateTaskCommentAsync`), and reconciles/reverts. The pure
  `CommentComposerModel` owns the flat-list transforms; the panes are read-only `DetailPaneView`
  text (no per-comment cursor).

## Design

### Selecting a target — a transient reply-target picker

The Comments/Stream panes are read-only text with no row cursor, so the user needs an explicit way
to pick *which* comment to reply to. A **transient bottom-anchored `FrameView` + `ListView`** overlay
(`_replyPickerBox`), modelled exactly on the existing comment-composer / Dispatch-prompt overlays,
lists the task's **top-level** comments; `↑/↓` move, `Enter` (or a row click — the `ListView`'s
`OpenSelectedItem`) picks, `Esc` cancels. This keeps the **single sectioned `ListView` / no second
*persistent* focusable pane** rule (#3): the picker is shown-then-dismissed like every other overlay,
never a permanently-focusable companion pane.

Only top-level comments are reply targets (ClickUp's reply endpoint is keyed by the parent comment;
threads are one level deep), and **pending/optimistic** comments (client sentinel id) are excluded —
you can't reply to a comment the server hasn't confirmed.

### Chord allocation

New `KeyAction.ReplyToComment`, bound to **`Ctrl+T`** ("reply into **T**hread") in
`ScreenContext.Detail`, footer glyph **`↩`**. `Ctrl+R` — the natural "Reply" mnemonic — is already the
Detail **refresh** alias (`F5`/`Ctrl+R`), so it's unavailable; `Ctrl+T` is free across every context.
Available on any tab (like `Ctrl+N`), gated on a reply callback being supplied and inert while the
Dispatch prompt / composer / editor own the keyboard. Empty case (no top-level comments) flashes
"No comments to reply to." and does nothing.

### Composer reply mode (reuse `_commentBox`)

`ShowCommentComposer` gains an optional target. In reply mode it stashes `_replyToCommentId` and
retitles the frame to `Reply to {author} — Ctrl+Enter or Tab→Post · Esc cancel`; plain `Ctrl+N`
clears it. `PostComment` branches: with a target + `_postReplyAsync`, it optimistically **nests** the
provisional under its parent and writes through `_postReplyAsync(commentId, text, ct)`, reconciling /
reverting **inside the parent's `.Replies`**; otherwise the unchanged top-level path.

### Pure logic (unit-tested)

- `CommentComposerModel` — add reply transforms mirroring the flat ones, plus a shared
  `PendingIdPrefix` const (the `__pending__` sentinel, so the picker can exclude pending comments and
  `PostComment` reuses it):
  - `ProvisionalReply(id, text, dateMs, parentCommentId, taskId)` → a `CommentItem` with
    `ParentCommentId`/`TaskId` set.
  - `AppendReply(comments, parentId, provisional)` → the parent (matched by id) with `provisional`
    appended to `.Replies` and `ReplyCount+1`; **no-op** (list unchanged) if the parent is gone (a
    refresh landed mid-post) — self-healing like the flat reconcile.
  - `ReconcileReply(comments, parentId, provisionalId, confirmed)` → replace the provisional reply in
    the parent's `.Replies` with `confirmed`, **re-stamping** `ParentCommentId`/`TaskId` (the facade
    returns them null) so it stays nested; no-op if parent or provisional is gone.
  - `RevertReply(comments, parentId, provisionalId)` → drop the provisional reply and `ReplyCount-1`.
- `CommentReplyModel` (new, pure) — projects `_comments` → `ReplyTarget { CommentId, Author, Label }`
  pick list: top-level, non-pending, **newest-first** (id tiebreak); `Label` = `"{author} · {snippet}"`
  with a single-lined, length-capped snippet and a `" (N replies)"` suffix when `ReplyCount > 0`.
- `TaskService.CreateThreadedCommentAsync(commentId, text, ct)` — thin passthrough to the facade
  (mirrors `CreateTaskCommentAsync`), unit-tested through the `IClickUpClient` fake.

### Host wiring

`TaskDetailScreen` ctor gains `postReplyAsync: Func<string,string,CancellationToken,Task<CommentItem>>?`
(commentId, text, ct). `null` disables reply (chord inert), so a non-interactive host is unaffected.
`TodoApp` and `SingleTaskApp` pass
`postReplyAsync: (commentId, text, ct) => _tasks.CreateThreadedCommentAsync(commentId, text, ct)`.
Present in **both** hosts (both already load replies), so reply works in the dashboard detail and in
single-task launch mode.

### Footer / help / keybindings

`↩` added to `HelpItemSets.Detail` **and** `DetailWithTaskTree`, `KeyAction.ReplyToComment` added to
`Keybindings` under `ScreenContext.Detail`; the existing keybinding↔help cross-check tests (#355) are
updated, not loosened.

## Phases

1. **Pure logic + service** — `CommentComposerModel` reply transforms + `PendingIdPrefix`,
   `CommentReplyModel`, `TaskService.CreateThreadedCommentAsync`; unit tests. (Opens the draft PR.)
2. **TUI glue + wiring** — reply-target picker overlay, composer reply mode, both-host wiring,
   `Ctrl+T` chord + footer/help + keybinding cross-check test updates; `dotnet build`/`test`/`format`.
3. **tui-validate** — `reply_check.py`: `Ctrl+T` → pick a comment → compose → post; assert the reply
   renders nested (`↳`) and the fake backend recorded a `POST …/reply`; `detail_check.py` A/B and the
   existing comment/thread checks stay green.

## Acceptance criteria (from #330)

- [ ] Reply to a specific comment; it posts into that thread in ClickUp and appears nested locally
      (per C), reconciling/reverting correctly.
- [ ] Top-level commenting still works unchanged.
- [ ] `dotnet test` green, then `tui-validate` drives select-comment → reply → post.

## Out of scope / deferred

- **@-mention in a reply** — replies post plain text, mirroring the top-level composer; structured
  mention authoring is the separate #325/#326 line.
- **Reply to a reply** — ClickUp threads are one level deep; the picker offers only top-level
  comments, and a reply nests directly under its parent.
