# E2E harness: `E2E_FOREIGN` scenario for Quick Updates on not-mine rows (#232)

## Goal

Close the automated-test gap left by #160 (PR #233): the tui-validate fake backend can't
produce a **foreign-subtask** (`_foreignSubtasks`, #70/#179) or **context-parent**
(`_contextParents`, #46) row, and doesn't model a `PUT /task/{id}` status/priority write
distinctly — so the headline #160 behaviour ("Quick Updates opens and an edit sticks on a task
that isn't the user's own work, and the row stays in place") can't be asserted end-to-end.

Add an **opt-in** scenario (`E2E_FOREIGN=1`) to `tests/ClickUpTodo.Tui.E2E/Program.cs` plus a
sibling drive script `foreign_quickupdates_check.py`. Everything is gated on the env flag so the
default A/B byte-identical renders (screen_check / color_check / detail_check) are undisturbed.

## What produces the rows (verified against the app)

- `E2E_FOREIGN=1` forces `config.View.Subtasks = SubtaskView.All` (so `ShowSubtasks` is true).
  In `TodoApp.FetchAsync` that runs **both** `ResolveContextParentsAsync` (gated on
  `ShowSubtasks`) and `ResolveForeignSubtasksAsync` (gated on `Subtasks != Hidden`).
- **Context parent:** a snapshot task whose `parent` id is **absent** from the snapshot →
  `TaskService.MissingParentIds` returns it → `ResolveContextParentsAsync` does
  `GET /task/{parentId}` (no `include_subtasks`) and renders it as a `(parent — not assigned to
  you)` header (`TaskRowFormatter.ContextParentMarker`).
- **Foreign subtask:** a subtask of an in-view (assigned) parent that is **not** itself in the
  snapshot → surfaced by the adaptive per-parent fetch. With ≤ `PerParentThreshold` (8) parents
  the plan is all per-parent, so `ResolveForeignSubtasksAsync` calls
  `GetSubtasksAsync(parentId)` = `GET /task/{id}?include_subtasks=true` and reads the `subtasks[]`
  array. `ForeignDescendants` keeps children that descend from a present parent. Rendered with
  `(not assigned to you)` (`TaskRowFormatter.ForeignSubtaskMarker`).

## Seeded snapshot (foreign mode only)

`GET /team/{id}/task` → two tasks (page 0, `last_page:true`):

- `pt1` "Assigned parent — my task AA" — top-level, list `plist`, status `to do`.
- `ct1` "My nested subtask BB" — list `plist`, `parent:"cp1"` (**absent** → context parent), status `to do`.

Resolvers:

- `GET /task/pt1?include_subtasks=true` → pt1 + `subtasks:[fs1]` where
  `fs1` "Foreign teammate subtask ZZ", `parent:"pt1"`, list `plist`, status `to do`,
  assignees `[Ada Lovelace 101]` (a teammate → not mine).
- `GET /task/ct1?include_subtasks=true` and `GET /task/fs1?include_subtasks=true` → no subtasks
  (terminates the BFS recursion).
- `GET /task/cp1` (no `include_subtasks`) → context-parent detail "Context parent PP", list
  `plist`, status `in review`, assignee `[Grace Hopper 102]`.

`PUT /task/{id}` (foreign mode): parse `status` (string) and/or `priority` (number|null) from the
body, persist into per-task override maps, and **echo the task reflecting the override** so the
committed value round-trips (`ClickUpClient.SetTaskStatusAsync` reads `.status.status`;
`SetTaskPriorityAsync` reads `.priority`). This is the piece the default fake lacks — today any
`/task` PUT falls through to a canned `in review` detail.

All other endpoints (`/user`, `/team` members, `/list/{id}` statuses, `/list/{id}/task` empty,
create-comment, create-task) are **unchanged** — foreign mode only overrides the
`/team/{id}/task`, `GET /task/{id}`, and `PUT /task/{id}` branches, each `_foreign ? … : …` so the
default path stays byte-identical.

## Drive script `foreign_quickupdates_check.py`

1. Boot with `E2E_FOREIGN=1`; assert both markers render — `(not assigned to you)` (fs1, after an
   idempotent expand-all `Ctrl+→`, since a normal parent folds collapsed) and
   `(parent — not assigned to you)` (cp1, always shown).
2. **Find-and-open** Quick Updates on the foreign subtask by its QU title (the screen title is
   `Quick Updates — {taskName}`): press Space, and if the open screen's title isn't fs1, Esc +
   Down and retry (robust to row ordering). Asserting QU **opens** on the foreign row is the #160
   headline — the pre-#160 build flashed "not assigned to you — unchanged" and never opened.
3. Commit a **changed** Status and Priority while the screen stays open (#207): Status pane
   preselects `to do` (row 0), Down → `in progress`, Enter; Tab to the Priority pane, Up×4 clamps
   to `Urgent` (row 0), Enter. The `✓` moving here is only an **optimistic** reflection —
   `ApplyStatus`/`ApplyPriority` set it before the server responds and reconcile to
   `confirmed ?? committed`, so a *null* echo still leaves the optimistic value in place — hence
   this step does **not** by itself prove the round-trip; it confirms the commit path fired.
4. Esc → back to the list; assert the fs1 row is still present and shows the committed status in
   place (`(IP)` — "the row stays in place and isn't dropped").
5. **Round-trip proof:** force a manual refresh (`F5`, never a delta) so the per-parent foreign
   fetch re-serves `fs1` from the fake's **persisted** model (`_foreignStatus`/`_foreignPriority`),
   replacing every optimistic value. Assert the re-rendered row is `(IP)` (and the foreign marker
   returns on the full render path), then reopen QU on `fs1` — seeded from the re-fetched task — and
   assert the Status pane marks `in progress` and the Priority pane marks `Urgent`. Had the modelled
   `PUT` not persisted the commit, `fs1` would re-serve its seed (`to do`, no priority) and these
   would fail — so this is what actually establishes the Status **and** Priority round-trip through
   the modelled write.

## Files

- `tests/ClickUpTodo.Tui.E2E/Program.cs` — `E2E_FOREIGN` flag + foreign scenario in `FakeClickUp`.
- `tests/ClickUpTodo.Tui.E2E/foreign_quickupdates_check.py` — the new drive script.

## Invariants

- No product-code change; no `Generated/` edit; no curated-spec / Kiota change; no new API surface.
- Default (non-foreign) harness behaviour byte-identical — the A/B guards must still pass.
- No second focusable pane, no keybinding change (test-harness only).
