# Plan — Contextual chords (G): `Ctrl+N` Add-task/Add-subtask on the main list (#544)

The Terminal.Gui half of slice **G** (#544, epic #537), main-list task path — implemented against the
model in `docs/plans/contextual-chord-model.md` §4 and consuming the **subtask-create facade** that
shipped ahead of it in #603 (`NewTaskRequest.ParentTaskId` → ClickUp's top-level `parent`, PR #603,
merged).

#544 asks: when a task is highlighted and `Ctrl+N` is pressed, present a short **Add (sibling) vs
Sub-add (child)** clarification, then create with the correct parent association. This plan delivers
the **headline first row of that table** — the main task list — as a clean, reviewable slice:

| Context | Add (sibling) | Sub-add (child) → parent |
| --- | --- | --- |
| **Main list task** | New task (today's `Ctrl+N`) | **Subtask** of the highlighted task |

## Scope of *this* PR

1. **`TaskAddChoiceModel`** (pure, `Tui/Screens/`) — the sibling-vs-child classifier for a main-list
   cursor, mirroring the pure-glue split of `CommentReplyModel`. `ForCursor(highlightedTaskId,
   highlightedTaskName, isContextParent, isForeignSubtask)` → `AddChoice(Prompt, ParentTaskId,
   ParentTaskName)`:
   - **No prompt (add a sibling directly)** for a header row / empty pane (`null`/blank id), a #46
     **context parent**, or a #70/#179 **foreign subtask** — rows that aren't the user's own fileable
     work and so have no valid own-task parent to sub-add under. This reuses exactly the "own work"
     gate `NewTaskForm.ResolveListSeed` already applies (and that `RenameCurrentTask` refuses on), so
     the two agree.
   - **Prompt** for the user's own highlighted task, carrying it as the Sub-add parent.
2. **`ChoiceDialog`** (`Tui/`) — a native multi-choice modal, promoted from the yes/no `ConfirmDialog`
   (#543/#595) shape: a nested `Application.Run(dialog)` disposed through
   `TuiTeardown.DisposeSwallowingTeardownBug`, an `_open`/`TryBeginOpen` single-slot guard, and the
   `[native choice]` title marker so the `tui-validate` harness can prove the native path was taken. N
   choice buttons in order (the **first is default + focused**, so a reflexive `Enter` takes the
   pre-existing "Add task" behaviour), then a Cancel; `Esc`/Cancel → index `-1`. Gated on
   `CLICKUP_TODO_NATIVE_MODAL` (`ChoiceDialog.Enabled == NativeModalSpike.Enabled`), the same gate as
   `ConfirmDialog`, per the model's §4 caveat (native path stays behind the flag until the `windows`
   and `dotnet` drivers are confirmed; the `tui-validate` harness is ANSI-only).
3. **`TodoApp` wiring** — `Ctrl+N` (`KeyAction.NewTask`) now routes through `BeginNewTask`:
   - classify the cursor; if no prompt **or** the native flag is off → `OpenNewTask()` exactly as
     today (the flag-off default is byte-identical);
   - otherwise open the `ChoiceDialog` ("Add task" / "Add subtask"); **Add task** → `OpenNewTask()`,
     **Add subtask** → `OpenNewTask(parentId)`.
   - `OpenNewTask` gains an optional `parentTaskId`: when set, the host's `createAsync` lambda rewrites
     the built request with `request with { ParentTaskId = parentTaskId }` (so **`NewTaskScreen`
     itself is untouched** — zero screen churn), the screen titles "New subtask", and the success
     flash reads "Created subtask …". The List selector already seeds from the cursor, which **is** the
     parent for a sub-add, so the subtask is POSTed to the parent's list by default.

## Tests

- **Unit** — `TaskAddChoiceModelTests`: header/empty (`null`/blank/whitespace id) → no prompt; own task
  → prompt + parent id/name; #46 context parent → no prompt; #70/#179 foreign subtask → no prompt.
- **`tui-validate`** — a new `ctrl_n_subtask_check.py` (native flag on): `Ctrl+N` on a highlighted task
  opens the `[native choice]` dialog listing **Add task** / **Add subtask**; **Add subtask** opens the
  "New subtask" screen → type a name → Save → the fake backend receives a `POST …/task` whose body
  carries the `parent` id. Control legs: `Ctrl+N` on a header/empty row opens New task with **no**
  dialog; and with the flag **off** `Ctrl+N` opens New task directly (dialog absent), proving the
  default path is unchanged.

## Deferred to follow-up (kept tracked on #544 — this PR is `Part of #544`, not `Closes`)

The remaining rows of #544's table, each its own reviewed slice (as the #603 facade PR anticipated):

- **Task Tree tab** `Ctrl+N` → new task / subtask of the highlighted node. That surface's `Ctrl+N`
  is currently `AddComment` in the `DetailSubContext.TaskTree` activation table, and re-pointing it
  entangles with the comment-add semantics and needs `NewTaskScreen` to launch from inside
  `TaskDetailScreen` — a materially larger, latency-sensitive Detail change best done on its own.
- **Comments tab** `Ctrl+N` sub-add → reply (already reachable on `Ctrl+T`/#330).
- **Checklists tab** `Ctrl+N` sub-add → sub-item (#460/#569/#576).
- A **non-native inline fallback** for the flag-off default (today the flag-off path simply keeps the
  plain new-task behaviour); it arrives for free when the maintainer flips the native-modal flag
  default per the model's §4, or as a small follow-up if wanted sooner.

## Hard-rules check

- **No `Generated/` hand-edits, no spec change / Kiota regen** — consumes the merged #603 facade; the
  `parent` field already rides `CreateTaskAsync`'s additional-data bag.
- **ClickUp auth quirk** untouched.
- **Tests land with the code**; none weakened or deleted. The pure classifier is unit-tested; the
  end-to-end subtask create is `tui-validate`-verified against the fake backend.
- **#3 / #12** — no second focusable pane; the native modal is a nested run-loop proven not to regress
  latency (model §4). No bare-letter binding — `Ctrl+N` is unchanged; the choice is a modal, not a new
  chord. The main-list keypress path gains only a pure classifier lookup before the existing open.
