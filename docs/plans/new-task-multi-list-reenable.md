# New Task — re-enable the multi-list create (#524)

Follow-up to #241/PR #366, which shipped the New Task multi-list create
**disabled** pending the list-change field/status migration (#365). This issue
drops the disabled gate so a task filed with more than one list selected is
created in its home/primary list and then added to each additional selected
list.

See the original design in `docs/plans/completed/new-task-multi-list-create.md`
— its "Re-enabling when #365 lands" note specifies the exact change.

## Why this is unblocked now (independent of the in-flight #365 PR #523)

The gate was conservative. The #365 analysis established that a task↔list
membership **add** is always safe: an add only *exposes* the target list's
Custom Fields / statuses to the task, it never *strands* a value the way a
**remove** can. New Task only ever issues adds (create in the primary list, then
add to the extras), so it needs **no strand preflight and no confirmation UX** —
none of the `ListMembershipMigration` / `ListMembershipApplyPlanner` machinery
that PR #523 adds for the Quick Updates List pane (where removes are possible).

Concretely, the re-enable depends only on code already merged to `main`:

- `Services/NewTaskCreator.CreateAsync` — the pure create-then-add orchestration
  and its partial-failure model (task never rolled back; failed additional adds
  recorded, remaining adds still run; cancellation rethrown). Shipped + unit
  tested in #366.
- `ClickUpClient.AddTaskToListAsync` (#237) via `TaskService`, already wired into
  `NewTaskScreen` as `addToListAsync` and into `TodoApp`.
- `TodoApp`'s `Created` handler, which already composes the partial-failure flash
  from `NewTaskCreateResult` (kept wired, "dormant" only because no adds fired).

So this is a pure activation, not new behaviour built on unmerged code.

## Change

### `Tui/Screens/NewTaskScreen.cs`

- `StartCreate` currently calls
  `NewTaskCreator.CreateAsync(primary!, [primary!], …)` — the single-list gate.
  Pass the **full selection** instead. Capture the selection snapshot on the UI
  thread in `SaveBaseFields` (a new `_pendingSelection` field, alongside the
  existing `_pendingPrimary`) so the off-UI-thread create reads a materialised
  list rather than touching `_lists.Selection` (a live Terminal.Gui view read)
  from a background thread.
- Remove the `MultiListDisabledNote` constant and the `_lists.HasFocusChanged`
  handler that flashed it — the note only existed to warn that extra lists were
  ignored, which is no longer true.
- Update the doc comments that describe the create as "primary list only".

`NewTaskCreator` de-dupes the selection and drops the primary, so the single-list
path (only the home list selected) still issues **zero** add calls — byte-for-byte
the same as before for the common case.

### `Tui/TodoApp.cs`

- The `Created` handler's partial-failure branch is already live code; only its
  comment calls it "dormant … while multi-list create is disabled". Update the
  comment to say the branch is now active.

## Tests

- **Unit:** `NewTaskCreatorTests` already covers the orchestration end to end
  (single-list no-add path, N-list create + ordered adds, primary excluded from
  adds, de-dup/blank-id drop, partial failure recorded while the task is kept,
  create-failure propagation, cancellation). No new orchestration logic is added,
  so these stand as the guard for the behaviour this activates; they must stay
  green.
- **TUI (`tui-validate`, `new_task_check.py`):** the New Task screen wiring isn't
  CI-unit-testable (CLAUDE.md). Flip the existing check from asserting the second
  list is *ignored* to asserting it is *applied*: with `Q3 Website Refresh` (home)
  and `Ministry Ops` both selected at Save, the created task's detail **Other**
  tab now shows a `Lists:` line including `Ministry Ops` (the fake records the
  membership `POST /v2/list/{listId}/task/{taskId}` into its `locations`, reflected
  on the next detail GET). Drop the disabled-note assertion (the note is gone).

## Hard-rules check

- No `Generated/` edits, no `clickup-openapi.json` change, no Kiota regen — both
  facade methods (`CreateTaskAsync` #209, `AddTaskToListAsync` #237) already exist.
- Single sectioned `ListView` model untouched; no second focusable pane; no new
  keybinding — the List selector is the existing embedded `ListSelectorView`.

## Scope boundary

- Quick Updates List-pane re-enable (removes, with strand handling + confirm) is
  #365/PR #523 — separate screen, separate mechanism, not touched here.
