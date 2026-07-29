# Task Tree tab in single-task launch mode (#374)

**Issue:** [#374](https://github.com/rbcministries/clickup-todo-cli/issues/374) — follow-up to
**#291** (the Task Tree tab in `TaskDetailScreen`), part of the multi-tab epic **#292**.

## Goal

`TodoApp` wires the Task Tree tab (#291) into every detail it opens; `SingleTaskApp` (the
`--task <id>` launch mode, #296) constructs the *same* `TaskDetailScreen` but never supplies the
tree loader, so a launched task comes up as a four-tab screen with no Task Tree tab. This wires
the tab into single-task mode and gives tree-row activation a navigation model that fits a host
with no main list to fall back to.

`TaskService.GetTaskTreeAsync`, the `TaskTreeArranger`, and the tab's rendering/hit-testing all
already exist from #291. This is **navigation wiring + a decision**, not new data plumbing — no
`Generated/` or curated-spec change, no `TaskDetailScreen` change.

## Key design decision — navigation semantics

The issue lists three candidate semantics for activating a tree row in single-task mode:

1. open in the same tab and **replace** the current task,
2. open a **new terminal tab** per #301, or
3. **push onto a per-launch nav history** (browser-style back, #298).

**Chosen: stack the target task's detail over the current one on `SingleTaskApp`'s existing
screen stack (`_stack`) — `Esc` walks back one task at a time; `Esc` at the launch-task root
exits the tab (via the #299 confirmation).**

Why:

- It is **uniform with the dashboard** (#291). `TodoApp` handles `OpenTaskRequested` by stacking
  the target's detail on its `_screens` back-stack (`Esc` = Back through the visited-task chain).
  That was the maintainer decision recorded in #401 (`Esc` = Back is canonical) and adopted
  across the Ctrl+O (#387) and tree (#291) detail→detail paths. Single-task mode should behave
  the same so the two launch modes don't diverge.
- It **reuses `SingleTaskApp`'s existing seam**. The host already stacks Help (F1), the one-off
  agent run (#345), and the exit confirmation (#299) over the root detail via
  `ShowScreen`/`CloseScreen`, hiding the layer beneath so only one screen is visible/focusable at
  a time (the #3 single-visible-screen invariant). Pushing a child `TaskDetailScreen` onto the
  same stack needs no new mechanism and no second focusable pane.
- It needs **zero `TaskDetailScreen` changes** — each stacked child is a fresh screen with its
  own tree loader, keyed to its own task id. (Replace-in-place, by contrast, would require the
  detail screen to reset its tree/editor state for a new task in place — new surface for no gain,
  and it contradicts the recorded #401 decision.)

`Esc` semantics, end to end: from a stacked child, `Esc` closes that child (pops back to the
task beneath). At the **root** launch task there is no task beneath and no main list, so `Esc`
falls through to the existing `RequestExit` → `ExitConfirmScreen` (#299) — exactly today's
launch-mode Esc behaviour, unchanged.

**Deferred to #298 (browser-style navigation history):** a first-class *forward* key and a
persisted/visible visited-task history. `#374` only gives the walkable-back chain the shared
stack already affords; the richer history model is that epic's domain, as the #291 plan notes.

## Badge display — parity with the dashboard (deviation from the issue's pre-#415 wording)

The issue text (written before #415) says to wire a **fixed `BadgeDisplay.Text`**. Since then
#415/#416 made the dashboard's tree tab seed its badge mode from the persisted
`AppConfig.BadgeDisplay` and cycle it in place with **F6**, reflecting the change across every
stacked detail. To keep single-task mode's tree tab identical to the dashboard's (the whole point
of #374 is parity), this seeds `treeBadgeDisplay` from `_config.BadgeDisplay` and wires
`CycleBadgeDisplayRequested` (F6) to cycle + persist + reflect across the root and any stacked
child details — mirroring `TodoApp.CycleTreeBadgeDisplay`. Documented here as a conscious
departure from the issue's stale "fixed Text" instruction.

## Implementation

All changes are in `src/ClickUpTodo/Tui/SingleTaskApp.cs`.

- Introduce a small private `DetailTab` record bundling a `TaskDetailScreen` with its live
  `TaskId` / `TaskDetail` / comments / `Refreshing` flag, so per-task state (agent dispatch,
  refresh target, browser URL) follows the front-most detail rather than a single root field.
- Extract `BuildDetailTab(task, comments)` that constructs a fully-wired `TaskDetailScreen`
  (now including `currentUserId: _tasks.UserId`, `treeBadgeDisplay: _config.BadgeDisplay`,
  `loadTaskTreeAsync: ct => _tasks.GetTaskTreeAsync(id, ct)`) and wires the common events
  (Refresh, AgentDispatch, QuickUpdates, Flash, Help, **OpenTaskRequested**,
  **CycleBadgeDisplayRequested**). Its callbacks key off the *specific* task id, so a stacked
  child posts comments / edits descriptions / loads its tree for its own task.
- `Build()` builds the **root** tab and wires its distinctive `Closed` handler (Ctrl+B →
  browser + quit; Esc → `RequestExit`).
- `OpenTaskDetail(taskId)` fetches the target's detail + comments off the UI thread, then (if the
  requesting layer is still front-most) builds a child tab and `ShowScreen`s it — `Esc` pops it,
  Ctrl+B on it opens the browser and pops (does **not** quit the whole tab, unlike the root).
- `RefreshTab(tab)` / `DispatchAgent(tab, request)` replace the old single-root `RefreshTask` /
  `DispatchAgent`, operating on the passed tab.
- `CycleTreeBadgeDisplay(screen)` cycles + persists `BadgeDisplay` and reflects it into the root
  and every stacked child detail (idempotent for a tree that hasn't loaded).

## Hard-rule compliance

- **No `Generated/` hand-edit, no curated-spec change** — reuses `TaskService.GetTaskTreeAsync`
  from #291.
- **No second focusable pane (#3)** — child details stack via the existing single-visible-screen
  `ShowScreen`/`CloseScreen` seam; each hides the layer beneath.
- **No bare-letter shortcut (#12)** — the tab is reached by the existing tab cycle; F6 (a
  function key) cycles badges; bare letters stay reserved for the tree list's type-ahead.
- **Tests land with the code** — the navigation is Terminal.Gui-bound (not unit-testable in CI,
  per CLAUDE.md); validated by a new `tui-validate` E2E scenario
  (`single_task_tree_check.py`) driving the real `SingleTaskApp` under a PTY, plus `dotnet build`.
  The pure tree assembly/service is already unit-tested from #291.

## Phases

1. **Wiring:** the `SingleTaskApp` refactor above. Build green → opens draft PR.
2. **E2E + finalize:** `single_task_tree_check.py` (boot `--task t0` with `E2E_TREE=1`, cycle to
   the Task Tree tab, assert ancestry + children render, Enter/double-click navigates and stacks,
   `Esc` walks back, `Esc` at the root raises the exit confirmation); document in SKILL.md; run
   `tui-validate`; mark ready; review subagent.
</content>
