# Plan — Task/subtask `Delete` from the Task Detail Task Tree tab (#594, part 2)

The last remaining slice of contextual `Delete` (F, epic #537, deferred from #543 and tracked on
**#594**). Part 1 — comment delete — shipped: the `DeleteCommentAsync` facade (#599) and the
Comments/Stream-tab delete UI (#610). This slice wires `Delete` on the **Task Tree tab** to delete the
highlighted node's task, over the `DeleteTaskAsync` facade that already exists on `ClickUpClient` but is
unwired to any UI.

## Why it's unblocked now

#594 deferred this half pending a **navigation-after-delete** decision it tied to #402 and #545. Both
resolved:

- **#402/#614** landed the accepted navigation ADR (`docs/navigation-model.md`). Its one `Esc` contract
  (rule 2) defines what replaces a deleted detail: *pop the destination detail to the layer beneath* —
  a previous detail, a transient modal beneath it, or the host root; and at a `SingleTaskApp` (`--task`)
  root, hand off to the exit seam. That is exactly the post-delete navigation this slice needs.
- **#545** closed — it was the F2-rename slice, not a Delete slice, so the "overlaps #545" caveat is moot.

## Scope of *this* PR

`Delete` on the **Task Tree tab** deletes the **highlighted node**, mirroring the checklist/comment delete
precedents (confirmation → optimistic → revert-on-failure + flash), with the target classified from the
tree's shape:

| Highlighted row | Delete behaviour |
| --- | --- |
| **The current task** (`TaskTreeRow.IsCurrent`) | Confirm → `DeleteTaskAsync` → on success raise `CurrentTaskDeleted`; the host navigates per the ADR (pop / quit). No optimistic close — the view stays until the write succeeds, so a failed delete just flashes. |
| **A descendant subtask** (a row below the current one) | Confirm → optimistic remove of the node **and its subtree** from `_loadedTreeRows` + re-render + revert-on-failure; the view (its subject task) is unaffected. |
| **An ancestor** (a row above the current one) | Inert-but-flashed. **Decision: delete is downward-only here** — deleting a parent from a child's view orphans the task you're looking at and mangles the displayed ancestry; do that from the parent's own view. Non-destructive F2-rename still allows ancestors; the more dangerous Delete is deliberately conservative. |
| **Placeholder / message row** (`(no task tree)`, `Loading…`, load error) | Inert-but-flashed, mirroring `RequestTreeRename`'s guard. |

Deleting a *subtask* in the general sense is still fully reachable: `Enter` into it (it becomes the current
task), then `Delete` — the current-task path — or delete it in place from its parent's tree.

### Post-delete navigation (the ADR, realised)

- **Dashboard (`TodoApp`):** `CurrentTaskDeleted` → `CloseScreen(screen)` (pop to the layer beneath) +
  `_refresh.RequestRefresh()` so the now-stale list/parent-tree reconciles promptly rather than waiting for
  the interval poll.
- **Single-task root (`SingleTaskApp` `_root`):** the tab's subject is gone and the user already confirmed
  the destructive action, so `CurrentTaskDeleted` → `Application.RequestStop()` (quit directly). Routing
  through the root's `Closed → RequestExit` would re-prompt "Confirm exit?" on top of the delete confirm —
  avoided. (Recorded decision.)
- **Single-task stacked child (opened by walking the tree, #374):** `CurrentTaskDeleted` →
  `RequestClose()` → pops to its parent via `ShowScreen`'s `Closed` handler (ADR "pop to layer beneath").

The layer beneath is not otherwise refreshed (the dashboard's explicit `RequestRefresh` aside): the ADR's
back-stack is a plain LIFO and staleness is reconciled by the app's refresh discipline — consistent with the
accepted model. Auto-refreshing every underlying screen is #291 history territory, out of scope.

## The pieces

1. **`TaskService.DeleteTaskAsync` passthrough** — `=> client.DeleteTaskAsync(taskId, ct)`, mirroring
   `DeleteCommentAsync`. The `ClickUpClient`/`IClickUpClient` facade + generated `DELETE /task/{id}` already
   exist (curated spec `clickup-openapi.json` has `DeleteTask`; `ClickUpClientDeleteTaskTests` covers it) —
   **no spec change / Kiota regen.** Refresh the two now-stale "the app has no delete UI" doc-comments.

2. **`TaskTreeDeleteModel`** (pure, `Services/`, unit-tested — analogue of `ChecklistItemEdits`):
   - `Resolve(IReadOnlyList<TaskTreeRow> rows, int selectedIndex) → TaskTreeDeleteTarget?` — classifies the
     selected row as `Current` / `Subtask` by its position relative to the `IsCurrent` row; `null` for an
     ancestor, an out-of-range index, or an unloaded/empty tree.
   - `RemoveSubtree(rows, taskId) → IReadOnlyList<TaskTreeRow>` — drops the node and its contiguous deeper
     descendants (a no-op when the id isn't present).
   - `SelectAfterDelete(removedIndex, newCount) → int` — clamped cursor placement after a subtree removal.

3. **`Keybindings`** — add `KeyAction.DeleteTask`, map `(Detail, DeleteTask) = "Delete"`, and add it to the
   `DetailSubContext.TaskTree` live set (`[AddComment, RenameTask, DeleteTask]`). `"Delete"` is the token
   already shared by `DeleteChecklistItem`/`DeleteComment`, disambiguated by sub-context — no collision
   (Checklists→checklist, Comments/Stream→comment, TaskTree→task), so the `#355` cross-check and the
   `AllBindingsOfAnAction_ShareOneKey` / no-token-collision invariants hold. **No footer change:** both
   Detail footer sets already carry `Del 🗑 delete` (Chord `Delete`) — inert on the tree today, live once
   `DeleteTask` binds — so `DetailFooter_PerSubContext_ShowsEveryLiveBinding(TaskTree)` already passes.

4. **`TaskDetailScreen`** — a `deleteTaskAsync` ctor seam + a `CurrentTaskDeleted` event; a tree-tab-guarded
   `Delete` dispatch block routed through `Keybindings.ResolveDetail`; a flag-off inline armed-confirm block
   (Enter/Esc) answered at the top of `OnKey`, gated to the tree tab (the checklist/comment precedent), plus
   the native `ConfirmDialog` path when `CLICKUP_TODO_NATIVE_MODAL` is on; and
   `DeleteSelectedTreeTask` / `PerformCurrentTaskDelete` / `PerformSubtaskDelete` (optimistic + revert), the
   `PerformChecklistItemDelete` shape.

5. **Host wiring (both hosts, kept identical bar the root-quit choice above)** — `deleteTaskAsync:
   (taskId, ct) => _tasks.DeleteTaskAsync(taskId, ct)` and the `CurrentTaskDeleted` subscription.

## Tests

- **Unit** — `TaskTreeDeleteModel` (classify current/subtask/ancestor/out-of-range/empty/no-current-row;
  `RemoveSubtree` drops node+descendants, keeps siblings/ancestors, no-ops on a missing id; `SelectAfterDelete`
  clamp/empty); `Keybindings` (`ResolveDetail(TaskTree, "Delete") == DeleteTask`, inert on other sub-contexts;
  the existing #355/#540 theories now also cover `DeleteTask` for free); a `TaskService.DeleteTaskAsync`
  passthrough test. No test weakened or deleted.
- **`tui-validate`** (after `dotnet test` green) — a `TaskDeleteLogScenario` capturing `DELETE /task/{id}`
  (env-gated, with a forbid leg), and a `task_delete_check.py`: navigate to the Task Tree tab → `Delete` on a
  subtask → inline confirm → `Enter` deletes (the row disappears; `DELETE` recorded); the forbid leg reverts +
  flashes; `Delete` on the current-task row → confirm → the detail closes back to the list. Mirrors
  `comment_delete_check.py` + `CommentDeleteLogScenario`.

## Hard-rules check

- **No `Generated/` hand-edits; no spec change / Kiota regen** — consumes the existing `DeleteTaskAsync`
  facade + generated `DELETE /task/{id}`.
- ClickUp auth quirk untouched.
- Logic in pure, unit-tested helpers; the E2E write scenario is a harness fixture, not a product test.
- **No second focusable pane / no latency regression (#3):** no new views — the confirm is the existing
  transient `ConfirmDialog` (flag-gated) or the inline armed-key confirm; the Task Tree tab stays a single
  focus target; dispatch adds only a pure sub-context lookup on the existing keypress path. Bare-letter
  type-ahead (**#12**) intact — `Delete` is a named key.

## Phases

1. Facade passthrough + `TaskTreeDeleteModel` + `Keybindings` + all unit tests. → draft PR.
2. `TaskDetailScreen` wiring + both hosts + doc-comment refresh.
3. `tui-validate` scenario + check + run. → ready-for-review.

## Deferred (tracked)

- In-place delete of an **ancestor** row (downward-only decision above).
- Auto-refreshing a stacked **layer beneath** beyond the dashboard's `RequestRefresh` (the #291 history).

If the full tree-tab scope lands cleanly the PR **Closes #594**; otherwise it is `Part of #594` with any
residue tracked there.
