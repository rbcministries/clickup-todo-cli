# Comment delete (#594, deferred comment half of the contextual-Delete slice #543)

Follow-up to **#543** (contextual chords **F**), which landed contextual `Delete`
on the **Checklists** tab behind a confirmation and the reusable `ConfirmDialog`.
#543 scoped `Delete` to "where an API exists, defer the rest with a note"; #594
tracks the two deferred contexts. This plan covers the **comment** context; the
**task/subtask** context is deferred (see *Deferred*).

## Acceptance criteria (from #594)

- `Delete` deletes the highlighted **comment** on the Comments/Stream tab behind
  the shared confirmation, backed by a unit- + integration-tested
  `DeleteCommentAsync` facade; permission failures revert + flash.
- `#355` cross-check + `tui-validate` green; no second focusable pane / latency
  regression (#3).

## Verified current state

- The curated spec (`clickup-openapi.json`) had **no** `DELETE /comment/{id}`
  path — only `/task/{task_id}/comment` (GET/POST) and
  `/comment/{comment_id}/reply` (GET/POST). The generated
  `WithComment_ItemRequestBuilder` therefore exposed only `.Reply`, **no**
  `DeleteAsync`. So a spec edit + Kiota regen is required (not a hand-edit).
- No `DeleteCommentAsync` facade exists on `ClickUpClient`.
- The Comments/Stream tabs render as read-only **text** panes (`DetailPaneView`)
  with link-focus — there is **no** row-selectable "highlighted comment" model
  like the Checklists tab's `ListView`. The screen already has a transient
  **comment picker** overlay (`_replyPickerBox` + `CommentReplyModel.Targets`)
  used by the reply flow (#330) to choose which comment to act on. That overlay
  is the natural, precedent-backed way to choose which comment to delete —
  a transient modal, **not** a second persistent focusable pane (#3-safe).
- `CommentItem` carries no author id, so "only the author's own comments are
  deletable" cannot be pre-filtered client-side; per #594 the write is attempted
  and a non-author permission error is surfaced on revert.

## Phases

### Phase 1 — the tested facade (this PR's first commit)

The decision-free, fully CI-verifiable foundation, mirroring how the
`SetTaskNameAsync` facade (#592) and the custom-field write path (#596 §1) landed
ahead of their UI.

1. **Spec:** add `DELETE /v2/comment/{comment_id}` (`operationId: DeleteComment`)
   to `clickup-openapi.json`, mirroring `DeleteChecklist` (empty-object response,
   no schema).
2. **Regen:** `dotnet kiota generate …` (the `scripts/regen-client.ps1` body) —
   the only generated delta is `WithComment_ItemRequestBuilder` gaining
   `DeleteAsync` (+ `kiota-lock.json`). **No `Generated/` hand-edits.**
3. **Facade:** `ClickUpClient.DeleteCommentAsync(commentId, ct)` over
   `DELETE /comment/{id}`, mirroring `DeleteChecklistAsync` (empty body, optimistic
   removal + revert-on-failure) with a blank-id fast-fail; `IClickUpClient`
   default-throwing declaration; `TaskService` passthrough.
4. **Tests:** `CapturingHandler` unit tests (DELETE to `/v2/comment/{id}`, no body,
   not the `/reply` endpoint; a 403 permission error → `ClickUpApiException`; a
   blank id throws before any transport call) + a `SkippableFact` integration
   round-trip (create a throwaway task, post a comment, assert present, delete,
   assert gone, delete the task in a `finally`).

### Phase 2 — wire `Delete` on the Comments/Stream tab (this PR)

Concrete decisions taken for this slice (grounding the sketch above):

1. **`KeyAction.DeleteComment`, bound `Delete` on the `Comments` and `Stream`
   sub-contexts.** The base `Map` gains `(Detail, DeleteComment) = "Delete"` —
   the same token `DeleteChecklistItem` already carries, disambiguated by
   sub-context exactly as `Ctrl+N` (`AddComment` vs `AddChecklistItem`) is. So
   `Delete` resolves to `DeleteComment` on Comments/Stream, `DeleteChecklistItem`
   on Checklists, and `null` (inert) on Description/Other/TaskTree — no collision
   within any one sub-context (`DetailBindings_HaveNoTokenCollision…` holds).
   - A **new `DetailSubContext.Stream`** is introduced so the Stream tab (which
     also shows comments) binds `Delete`/`Ctrl+N` **without** dragging
     Description/Other along: those stay `Default` (comment-less, `Delete` inert).
     `CurrentDetailSubContext()` returns `Stream` when `_streamPane` is front.
   - No footer restructure: the base Detail/DetailWithTaskTree footers already
     carry `Del 🗑 delete`, so the `#355` cross-check (`Footer_Shows…`,
     `DetailFooter_PerSubContext_ShowsEveryLiveBinding`) is satisfied for the new
     sub-contexts unchanged. The generic `🗑 delete` label serves both actions.
2. **A dedicated transient delete-picker overlay** (`_commentDeletePickerBox` +
   `_commentDeletePicker`), a parallel of the reply picker (#330) — a modal list,
   **not** a second persistent focusable pane (#3-safe). It lists the deletable
   **top-level + reply** comments (`CommentDeleteModel.Targets`, newest-first
   top-level with each thread's replies nested under it, `↳`-prefixed); pending /
   blank-id comments excluded. `Delete` on Comments/Stream opens it (or flashes
   "No comments to delete." when empty).
3. **Pick → confirm (two steps).** Picking a comment does **not** delete it
   (destructive + irreversible): with `CLICKUP_TODO_NATIVE_MODAL` on it opens the
   shared `ConfirmDialog`; off (the default, and the `tui-validate` path) it arms
   the inline `Enter`/`Esc` confirm answered at the top of `OnKey`
   (`_commentDeletePending`), mirroring the checklist delete.
4. **Pure model `CommentDeleteModel`** (`Targets`/`HasTargets`/`Remove`):
   `Remove(comments, commentId)` drops a top-level comment (and its thread) or a
   nested reply (decrementing the parent's `ReplyCount`) — the optimistic
   transform, unit-tested. Revert restores a whole-list snapshot (like the
   checklist delete), and a `_pendingCommentEdit` overlay re-applies `Remove` onto
   any refresh landing mid-write so a poll can't resurrect the just-deleted
   comment (the comment analogue of `_pendingChecklistEdit`).
5. Optimistic remove → off-thread `DeleteCommentAsync` → success (overlay
   cleared; next refresh reconciles) / revert-to-snapshot + flash the API error on
   failure. Only the author's own comments are deletable; `CommentItem` carries no
   author id so a non-author permission error surfaces on revert (per #594).
6. Wired in **both hosts** (`TodoApp`, `SingleTaskApp`) via
   `deleteCommentAsync: (commentId, ct) => _tasks.DeleteCommentAsync(commentId, ct)`.
7. `tui-validate` script driving `Delete` → pick → confirm → the comment
   disappears, plus the cancel and permission-revert legs; footer/help/README
   updates.

## Deferred (stays tracked on #594)

- **Task / subtask delete from Task Detail.** `ClickUpClient.DeleteTaskAsync`
  already exists but is unwired; wiring it from Detail raises a
  **navigation-after-delete** question (what replaces the detail view once the
  viewed task is gone) that overlaps #402 (navigation taxonomy) and #545 (H,
  main-list `Delete`). Best sequenced with those, per #594's own note — not
  bolted on here.
