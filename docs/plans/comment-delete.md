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

### Phase 2 — wire `Delete` on the Comments/Stream tab

1. `KeyAction.DeleteComment`; bind `Delete` in the `Comments` (and `Stream`)
   detail sub-context via `Keybindings.ResolveDetail`, so dispatch and the footer
   hint stay in lock-step (exactly as the checklist `Delete` does). Keep the
   #355 cross-check + `HelpItemSets` green.
2. Reuse the comment-picker overlay pattern (`ShowReplyPicker`/`PickReplyTarget`)
   for a **delete** picker: list the deletable (top-level + reply) comments,
   pick one, then the shared `ConfirmDialog` (native-modal flag) / inline armed
   confirm.
3. Pure model: a `Remove` / re-insert transform on the comment list for the
   optimistic removal + revert (mirroring `CommentComposerModel.Revert`/
   `RevertReply`), unit-tested.
4. Optimistic remove → off-thread `DeleteCommentAsync` → confirm (re-render/
   refetch) / revert + flash the API permission error on failure.
5. `tui-validate` script driving `Delete` → pick → confirm → row disappears, and
   the cancel/permission-revert legs; footer/help/README updates.

## Deferred (stays tracked on #594)

- **Task / subtask delete from Task Detail.** `ClickUpClient.DeleteTaskAsync`
  already exists but is unwired; wiring it from Detail raises a
  **navigation-after-delete** question (what replaces the detail view once the
  viewed task is gone) that overlaps #402 (navigation taxonomy) and #545 (H,
  main-list `Delete`). Best sequenced with those, per #594's own note — not
  bolted on here.
