# Plan — tui-validate: foreign-subtask / context-parent Quick Updates (#232)

Follow-up to **#160** (Quick Updates on tasks not assigned to the current user, merged PR #233)
and the harness relocation **#256** (the PTY harness now lives at `tests/ClickUpTodo.Tui.E2E/`).

## Goal

Extend the tui-validate fake backend to seed a **foreign subtask** and a **context parent**
row — tasks that are *not the user's own work* — and add a drive script that opens Quick
Updates on such a row (proving the #160 ownership guard is gone), commits a Status, and
asserts the committed value **round-trips and sticks in place** and the row **isn't dropped**.

This is **test-harness only** — no product-source change. #160 already lifted the write block
in `TodoApp.OpenQuickUpdates` (`src/ClickUpTodo/Tui/TodoApp.cs:1635-1643`); this adds the
end-to-end coverage that was deferred to #232.

## App-side behaviour being exercised (already shipped)

- `ResolveForeignSubtasksAsync` (per-parent `GET /task/{id}?include_subtasks=true`) pulls in a
  parent's teammate-owned subtasks; `ForeignDescendants` keeps those whose parent chain reaches
  an in-snapshot task. A foreign subtask with a non-me assignee renders with
  `ForeignSubtaskMarker` = `· (not assigned to you)` (`TaskRowFormatter.cs:65,191`).
- `ResolveContextParentsAsync` fetches the missing `parent` of an in-view subtask
  (`MissingParentIds`) via `GET /task/{id}` and renders it as a header with
  `ContextParentMarker` = `· (parent — not assigned to you)` (`TaskRowFormatter.cs:62,188`).
- Both resolvers run only when `View.Subtasks != Hidden` (`TodoApp.cs:303-311`).
- Quick Updates opens on any task with a `ListId`; `QuickUpdatesTaskById` resolves rows from
  `_rows` (not just `_all`) so a foreign/context row is editable and reconciled in place
  (`TodoApp.cs:1793`, `ApplyStatus` `:1814`, `UpdateTaskRow` `:1973`).
- On commit the QU Status pane's `✓` only moves to the new status after the host confirms with
  the **server-returned** status (`SetEffectiveStatus`, `QuickUpdatesScreen.cs:140`), so a
  `✓ <new status>` on-screen proves the PUT round-trip, not just the optimistic move.

## Changes

### 1. `tests/ClickUpTodo.Tui.E2E/Program.cs` — opt-in `E2E_FOREIGN=1` scenario

Gated entirely behind the env flag so the default A/B byte-identical renders are undisturbed.

- When set: `config.View.Subtasks = SubtaskView.All` (runs both resolvers + nests), and
  `config.BadgeDisplay = BadgeDisplay.Text` so the row shows the **full status name** (the
  default Icons chip is a letter abbreviation, not assertable as text).
- `FakeClickUp` gains a `foreign` flag. When set it serves a tiny, deterministic snapshot:
  - **team-tasks** (`GET /team/{id}/task`): two assigned tasks — `fp` (a normal parent) and
    `mine` (assigned to me, `parent:"cpar"` where `cpar` is absent → triggers a context parent).
  - **subtasks** (`GET /task/{id}?include_subtasks=true`): `fp` returns one child `fsub`
    (`parent:"fp"`, assignee Grace Hopper id 102 != me → foreign/other); every other id returns
    `subtasks:[]` (stops the BFS recursion).
  - **detail** (`GET /task/cpar`, no `include_subtasks`): the context parent header task.
  - **PUT** `/task/{id}`: echoes the committed `status` (and `priority`) parsed from the request
    body so `SetTaskStatusAsync`'s read-back is truthful and the row/checkmark round-trip.
  - list statuses reuse the existing `ListJson`.

### 2. `tests/ClickUpTodo.Tui.E2E/foreign_quickupdates_check.py` — new drive script

`E2E_FOREIGN=1`, mirrors the existing `qu_*` scripts (pyte screen, `answer()` for size/cursor
queries):

1. Boot; assert both markers render: `(not assigned to you)` and `(parent — not assigned to you)`.
2. Navigate to the `fsub` row (the `(not assigned to you)` line); `Space` opens Quick Updates —
   assert it **opens** (not blocked) and the Status pane lists the workflow statuses with the
   seeded `✓ to do`.
3. `Down`x2 → `blocked` row, `Enter` to commit; assert the checkmark moves to `blocked`
   (`✓ blocked`) — the server-confirmed round-trip.
4. `Esc` back to the list; assert the `fsub` row is **still present**, still shows
   `(not assigned to you)`, and now shows the `blocked` status (round-tripped, in place).

## Test / validation plan

- `dotnet build -c Release` + `dotnet test -c Release` green first (harness change touches no
  product code, so the unit suite is unaffected — run it as the gate anyway).
- Build the E2E project; `pip install pyte`.
- Run `foreign_quickupdates_check.py` → PASS.
- Regression: run `quickupdates_check.py` and `qu_assignees_check.py` (unchanged default
  scenario) + one A/B `color_check.py` diff to confirm the default renders stay byte-identical
  (the new scenario is behind the env flag).

## Out of scope / deferred

- Priority and Assignees round-trips on a foreign row (Status is the representative edit; the
  Assignees pane already has its own `qu_assignees_check.py`). Noted in the PR if worth a
  follow-up.
