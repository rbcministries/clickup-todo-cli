# Plan — Contextual chords (G): subtask-create facade (#544)

Slice **G** of the contextual key/chord remapping epic (#537), implemented against the model
recorded in slice **A** (`docs/plans/contextual-chord-model.md`). #544 asks for a `Ctrl+N`
**sibling-vs-child clarification** that, on the *child* choice, creates the new thing under the
highlighted parent:

| Context | Add (sibling) | Sub-add (child) → parent association |
| --- | --- | --- |
| Main list / Task Tree task | New task | **Subtask** of the highlighted task |
| Comments tab comment | New comment | **Reply** to the highlighted comment (reply-target flow #330) |
| Checklists tab item | New item | **Sub-item** under the highlighted item (parent id, #460) |

## Where the pieces already are

Slice **C** (#540, merged) built the full contextual `Ctrl+N` dispatch seam in Task Detail
(`DetailSubContext` + `Keybindings.ResolveDetail`), which G plugs into — **no new keymap token**.
The two child write paths for **two** of the three rows already ship:

- **Comment reply (child):** `TaskService.CreateThreadedCommentAsync` → `ClickUpClient` `POST
  /comment/{id}/reply` (#330), reachable today on `Ctrl+T`; the pure `CommentReplyModel` picks the
  target.
- **Checklist sub-item (child):** `TaskService.MoveChecklistItemAsync` sets `UpdateChecklistItemRequest.Parent`
  (#569/#576), so an item can be reparented under another after creation.

The **one genuinely-missing facade** is the first row's child path: **creating a task as a subtask**
of a parent. `NewTaskRequest` / `ClickUpClient.CreateTaskAsync` have no `parent` field, and neither
does the generated `CreateTaskRequest`.

## Scope of *this* PR: the subtask-create facade (the decision-free half)

This PR ships **only** the subtask-create facade — the self-contained, CI-verifiable foundation the
main-list/Task-Tree *Sub-add* branch consumes — mirroring how the epic landed its other write
foundations ahead of the UI (rename facade #542/#592, custom-field write #587/#596, comment-delete
facade #594/#599). It touches no Terminal.Gui surface, needs no modal, and cannot collide with the
in-flight keybinding/custom-field/comment PRs.

### Facade contract

- **`NewTaskRequest.ParentTaskId`** (`ClickUp/Models.cs`): a new optional `string?`. When set, the
  task is created as a **subtask** of that parent (ClickUp's top-level `parent` field). Null/blank
  (the default) creates a top-level task, leaving today's create body byte-for-byte unchanged.
- **`ClickUpClient.CreateTaskAsync`**: when `ParentTaskId` is non-blank, send it as `parent` on the
  request's **additional-data bag** (`request.AdditionalData["parent"] = new UntypedString(id)`) —
  the same loosely-typed create-time-extra pattern the method already uses for `custom_fields`
  (#368). **No spec change / Kiota regen** — `parent` is a plain top-level string, so it rides on
  `AdditionalData` exactly like the custom-field array does, with zero generated-code churn.
- The facade **signature is unchanged** (`CreateTaskAsync(listId, NewTaskRequest, ct)`), so
  `IClickUpClient` and the `TaskService` passthrough need no edit — the new capability rides on the
  request record. The returned `TaskItem.ParentId` (already mapped from the response `parent`,
  `ClickUpClient.cs:1037`) reflects the confirmed parent, so a future optimistic insert can nest the
  new subtask without a read-after-write.

### Tests (this PR)

- **Unit** (`ClickUpClientCreateTaskTests`, existing `CapturingHandler`):
  - a `NewTaskRequest { Name, ParentTaskId = "p1" }` issues `POST /v2/list/{id}/task` with a
    top-level `"parent": "p1"` **string** in the body, alongside `name`;
  - an unset / blank / whitespace `ParentTaskId` sends **no** `parent` key (today's top-level create
    is untouched) — parameterized, mirroring the empty-`custom_fields` and empty-optional-field
    guards already in the file.
- **Integration** (`ClickUpClientIntegrationTests`, `SkippableFact`, `CLICKUP_TOKEN`+`CLICKUP_LIST_ID`
  gated): create a throwaway parent task, then create a second task with `ParentTaskId = parent.Id`,
  assert the returned subtask's `ParentId == parent.Id`, and delete **both** in a `finally` (parent
  last) so the run is residue-free. Skips cleanly without credentials.

## Deferred to follow-up (tracked on #544 — this PR does **not** `Closes #544`)

The Terminal.Gui-coupled remainder of slice G is a large, latency-sensitive change validatable only
under `tui-validate`, and is deferred so this facade can land clean:

1. **The `ChoiceDialog`** — a native multi-choice modal ("Add {Task|Comment|item}" vs
   "{Subtask|Reply|sub-item}"), promoted from the same nested-`Application.Run` shape as slice F's
   yes/no `ConfirmDialog` (#543/#595), behind `CLICKUP_TODO_NATIVE_MODAL` with a non-native fallback.
   `ConfirmDialog` is yes/no only, so this is net-new.
2. **The pure sibling-vs-child classifier** — a terminal-free model (mirroring `CommentReplyModel`)
   mapping `(context, highlighted row) → { AddKind, parentId }`, including the "nothing highlighted →
   add sibling, no prompt" short-circuit.
3. **Per-context wiring** — route `Ctrl+N`'s child choice to: main-list/Task-Tree → this
   subtask-create facade; Comments → the existing reply path; Checklists → a sub-item (create +
   `MoveChecklistItemAsync`, or a `parent` on create). The main-list UX also has to decide how the
   subtask flows through today's full `NewTaskScreen` (which currently ignores the cursor task).

These stay tracked on **#544**, which remains open.

## Hard-rules check

- **No `Generated/` hand-edits, no spec change / Kiota regen** — `parent` rides on the additional-data
  bag, the established pattern for create-time extras in this very method.
- **ClickUp auth quirk** untouched.
- **Tests land with the code**; the integration test is a `SkippableFact` + env-gated; no test
  weakened or deleted.
- **No TUI change** — no second focusable pane, no new keybinding (#3/#12). No `tui-validate` run is
  warranted (no rendering/list-source/driver/keypress code touched); the TUI half is the deferred
  work above.
