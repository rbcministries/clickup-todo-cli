# Plan — F12 "Show Completed" toggle (tasks + subtasks) — issue #178

## Goal

Add an explicit, user-facing **F12 = "Show Completed"** toggle to the main task list.
Today "don't show completed" is real but implicit and inconsistent between top-level
tasks (server-side `IncludeClosed=false`) and pulled-in subtasks (fetched closed for
chain integrity, only hidden client-side by `Status IS NOT` rules), which lets completed
subtasks leak into the view (the #172 / PR #177 symptom).

- **Default: off** — completed/closed tasks hidden (matches today's top-level behaviour;
  additionally hides completed *subtasks* that currently leak).
- **On** — completed/closed tasks are fetched and displayed at every level.
- Persist in `ViewSettings`; surface in the frame-title flags + a `Flash`; add to the F1
  help line and Help screen.

## Definition of "completed" (scoped, per the issue's "simplest first cut")

Mirror `IncludeClosed`: a task is *completed* when its ClickUp **status type is `closed`**
(ClickUp's terminal closed type — exactly what `IncludeClosed=false` drops server-side).
This keeps default-off behaviour identical to today for top-level tasks and fixes the
closed-**subtask** leak. The broader question ("any status the user considers done", e.g.
a custom `Complete` or a `done`-type status) is explicitly deferred — see Deferred work.

## Design — single funnel

The codebase already decides list visibility in exactly one place: `TaskView.Apply` →
`Filter`. `ViewSettings` already flows into `Apply`, and pulled-in foreign subtasks flow
through `Apply` "like any other task". So the toggle rides that seam:

1. **Domain** (`ClickUp/Models.cs`): add `TaskItem.StatusType` (string?, ClickUp
   `status.type`). Map it in `ClickUpClient.Map` (list-item mapper). `IsCompleted(null)`
   is false, so pre-existing test data (no status type) is unaffected.
2. **ViewSettings**: add `bool ShowCompleted` (default `false`). New bool ⇒ absent in old
   configs ⇒ `false` ⇒ today's behaviour; **no migration / SchemaVersion bump needed**.
3. **Fetch**: add `bool includeClosed = false` to `IClickUpClient.GetAssignedTasksAsync`
   (set `QueryParameters.IncludeClosed`); `GetListTasksAsync` already has the param.
   `TaskService.LoadAsync` threads `config.View.ShowCompleted` into both the assigned and
   personal fetches (smaller payload when off; correctness is enforced by the display gate
   regardless).
4. **Display gate** (`TaskView`): pure `IsCompleted(TaskItem)` + gate inside `Apply` — when
   `!settings.ShowCompleted`, drop completed tasks after `Filter`. This covers the whole
   non-pinned set incl. foreign subtasks nested there. Also pre-filter the `foreignList`
   fed into the Focus section so a completed foreign subtask doesn't nest under a pinned
   parent when off. Explicitly-pinned anchors stay visible (pins already ignore F3 filters).
5. **TUI** (`TodoApp`): `F12` → `ToggleShowCompleted()` (persist, `Flash`, re-render; when
   turning **on**, `RequestRefresh()` so the server returns the closed tasks it had dropped).
   Add `ShowCompleted` to `CurrentSignature` so a toggle is never a no-op refresh. Add a
   `+completed` flag to `BuildFrameTitle` when on. Add `F12 completed` to `HelpItemSets.MainList`
   and a line to `HelpScreen`.

## Tests (test-first where practical)

- `TaskViewTests`: `Apply`/`Filter` drops `closed`-type when `ShowCompleted=false`, keeps
  it when `true`; never drops `open`/`custom`/`done`/null. `IsCompleted` truth table
  (closed → true, case-insensitive; others/null → false).
- `ClickUpClientMapTests`: `Map` carries `StatusType` from `status.type`.
- `TaskService.LoadAsync` threads `ShowCompleted` → `includeClosed` on both fetches
  (compact fake `IClickUpClient` capturing the flags).
- Existing suites stay green (null status type ⇒ no behaviour change).

## Keep the invariants

- No second focusable pane (#3): F12 is a pure list toggle + re-render.
- Bare letters reserved for type-ahead (#12): F12 is a function key.
- Generated client untouched; `status.type` is already in the curated spec + generated
  `Status`, so **no spec edit / Kiota regen**.

## Deferred work (file follow-up issue, link from PR)

Broader "completed" definition beyond ClickUp's `closed` type — a `done`-type status or a
custom "Complete" status the user considers done. Left out of this slice to keep default-off
behaviour identical to today; needs a maintainer call on scope.
