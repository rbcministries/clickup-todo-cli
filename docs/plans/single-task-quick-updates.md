# Plan — Quick Updates in single-task mode, tree-scoped on the Task Tree tab (#297 follow-up)

Single-task launch mode (`SingleTaskApp`, `clickup-todo --task <id>`) still answers `Ctrl+U` with a
deferral flash:

> `Quick Updates isn't available in single-task mode yet (tracked on #297).`

**Nothing blocks it any more.** #297 (the write-path decoupling from the main-list snapshot `_all`) is
closed and merged (PR #360): `IQuickUpdateTarget` / `SingleTaskUpdateTarget` already exist and are
unit-tested, and #290 (Quick Updates → `Ctrl+U` on both origins, PR #354) is merged too. What is left is
purely **host wiring** — the dashboard's Quick Updates orchestration lives inline in `TodoApp` and has no
seam a second host can call.

## Decisions

### 1. Extract the orchestration instead of duplicating it

The Quick Updates open/commit machinery (~430 lines in `TodoApp.cs`) is lifted verbatim into a new
host-agnostic `QuickUpdatesCoordinator` — the same move #345 made for agent dispatch
(`DispatchCoordinator`, explicitly "so the dashboard and the single-task launch host drive one code path
instead of duplicating ~130 lines of glue"). The host-specific bits become four small delegates over
each host's `Flash` / `ShowScreen` / front-most / mounted checks, plus the `IQuickUpdateTarget` the host
picks. Dashboard behaviour is unchanged by construction: the moved code is byte-identical apart from
`_screens.Contains` / `ActiveScreen` becoming the injected predicates.

### 2. On the Task Tree tab, `Ctrl+U` targets the highlighted node — in **both** hosts

The maintainer's call for this work: on the Task Tree tab, Quick Updates opens and applies to the task
**selected in the tree**, not the task the detail is showing. That gesture is implemented screen-side, in
`TaskDetailScreen`, so it behaves the same in the dashboard and in single-task mode. Deliberate, and worth
stating plainly because it *changes dashboard behaviour*: before this, `Ctrl+U` on the dashboard detail's
Task Tree tab quick-updated the open task.

Reasons to make it uniform rather than single-task-only:

- The tree tab's other row-scoped chords already target the highlighted node in both hosts — `F2`
  rename (#545/#605/#604), `Delete` (#594), `Enter`/double-click navigate (#291/#374). A `Ctrl+U` that
  silently meant "not this row, the other task" would be the odd one out.
- Host drift is the thing this codebase works hardest to avoid (`DispatchCoordinator`,
  `DispatchWorkingDirectoryPreFill`, `AppHostLaunch` all exist for that reason), and #290's whole premise
  is that one action means one thing everywhere.
- The write path it needs is exactly what #297 built: a tree node may be an ancestor or a foreign subtask
  that isn't in `_all`, which is the `SingleTaskUpdateTarget` case.

The **current** node is not special-cased away: `Ctrl+U` on the current task's own tree row goes down the
same tree path (so its row repaints), and because the target task *is* the detail's task, the committed
status/priority is still reflected onto the detail header exactly as a non-tree `Ctrl+U` would.

A placeholder/message row (`(no task tree)`, `Loading…`, a load error) is inert-but-flashed
("Select a task to update."), mirroring `RequestTreeRename`.

### 3. Single-task mode seeds Quick Updates from an authoritative `TaskItem`, not a projection

`TaskItemProjection.FromDetail` is lossy where it matters most for this screen: assignees come back with
a **placeholder id of `0`** (a `TaskDetail` carries only names), so an Assignees-pane remove would write
`id 0`. The dashboard hides this by preferring the `_all` row; single-task mode has no snapshot, so it
resolves the launch/current task through `TaskService.GetTaskItemAsync` (one GET, the same call the tree
and the nudge reconcile use) before opening. Tree-origin launches need no extra fetch — a tree row *is*
a `GetTaskItemAsync`-fetched `TaskItem`.

(The dashboard's own feed-opened-task path, #115, still uses the lossy projection. Out of scope here,
noted as a follow-up.)

### 4. The tree row repaints on a commit

Otherwise a status change made from the tree would show nowhere but the modal's own ✓. A new pure
decorator `ReflectingQuickUpdateTarget` wraps whichever target the host picked and additionally hands the
settled record to a reflection callback; both hosts point that at
`TaskDetailScreen.ApplyTreeTaskFields(TaskItem)`, which re-renders the one row (badges included) with the
selection preserved — the row-replace half of `ApplyTreeRename`, factored out and shared. Optimistic,
server-confirmed and reverted values all flow through it, so a failed write puts the row back.

Every detail-origin launch is decorated, not just a tree-origin one: the current task has a tree row too,
and it would otherwise go stale after a `Ctrl+U` from another tab. `ApplyTreeTaskFields` no-ops when the
tree tab was never opened.

The three fields are **folded** onto the row through `TaskService.ApplyFieldChanges` rather than the row
being replaced by the settled record wholesale — in the dashboard that record can come from the main-list
snapshot, whose copy of the node may differ in title/ancestry from the tree's own fetch. Status **colour**
is not folded (only the name), matching what the main-list row does today (`TaskService.ApplyStatusChange`);
the badge colour reconciles on the next tree load.

### 5. Single-task mode gets the Lists pane too, with a persisted-only candidate pool

`QuickUpdatesScreen` is a four-pane screen and its List pane (#242/#365) is not optional, so
`SingleTaskApp` is handed a `ListFrequencyCache` (constructed exactly like the `--task` host's
`AssigneeFrequencyCache`, #473). It has no fetch delegate of its own by design, and single-task mode runs
no list-hierarchy walk (#236), so the *candidate* pool is whatever a prior dashboard session persisted —
the same warm/cold story as the mention pool. Current membership, add-from-pool and the strand-guarded
remove all work regardless. The coordinator treats both caches as nullable and falls back to empty
matchers, so a host that supplies neither still gets working Status/Priority panes rather than a dead key.

## Design

### `src/ClickUpTodo/Tui/QuickUpdatesCoordinator.cs` (new)

```csharp
public sealed class QuickUpdatesCoordinator(
    TaskService tasks,
    AssigneeFrequencyCache? assignees,
    ListFrequencyCache? lists,
    Func<Screen?, bool> isFrontMost,   // "is this layer front-most" (null = the host's root/list)
    Func<Screen, bool> isMounted,      // async-reconcile guard
    Action<string> flash,
    Action<Screen> mount)              // the host's ShowScreen
```

- `Open(TaskItem task, IQuickUpdateTarget target, Screen? origin, TaskDetailScreen? reflect)` — the
  no-list guard, the front-most guard, the cached-statuses fast path / off-thread fetch, then `Show`.
- `Show(...)` — builds and wires `QuickUpdatesScreen` (verbatim from `TodoApp.ShowQuickUpdates`).
  `reflect` doubles as the seeded-membership source (`reflect.Task.Lists`); when it is null the
  background `EnrichListMemberships` runs instead — which is what a list-origin launch does today and
  what a tree-origin launch needs.
- Moved verbatim: `ApplyStatus`, `ApplyPriority`, `ApplyAssigneeAsync`, `ApplyListAsync`,
  `ReadMembershipAsync`, `FetchPerListDefinitionsAsync`, `EnrichListMemberships`, `HomeListOf`,
  `WithPriority`, `ColorForStatus`, the `_statusCommitGen`/`_priorityCommitGen` supersede guards, the
  `_armedListRemoval` arm state, and the screen/detail reconcile helpers.
- `internal static` pure bits (`ColorForStatus`, `WithPriority`, `AdditionalLists`) get unit tests.

### `TodoApp`

Keeps `ListUpdateTarget` / `QuickUpdatesTaskById` / `UpdateTaskRow` (its own snapshot concerns) and
delegates: the list `Ctrl+U`, the detail `Ctrl+U`, and the new tree-target case. `OpenQuickUpdatesForDetail`
keeps its "prefer the richer `_all` row, else project / else the tree row" resolution and its
list-target-vs-`SingleTaskUpdateTarget` choice.

### `SingleTaskApp`

Replaces the deferral flash with `OpenQuickUpdates(tab, request)`:
- tree request ⇒ target task is the tree row's `TaskItem`; else resolve the tab's task via
  `GetTaskItemAsync` off the UI thread.
- target = `new SingleTaskUpdateTarget(task)`, decorated with the tree-row reflect.
- `reflect` = the tab's screen when the target task **is** the tab's task, else null.

### `TaskDetailScreen`

- `QuickUpdatesRequested` becomes `EventHandler<QuickUpdatesRequest>`;
  `QuickUpdatesRequest(TaskItem? TreeTask)` carries the tree row's task (null ⇒ "the task I show").
- `RequestQuickUpdates()` — the tree-tab branch + the placeholder-row flash.
- `ApplyTreeTaskFields(TaskItem updated)` + the shared private `ReplaceTreeRow` that `ApplyTreeRename`
  now also uses.

## Tests

- **Docs**: the README's Task Detail key table and its Quick Updates paragraph now say `Ctrl+U` works in
  a `--task` tab and targets the highlighted node on the Task Tree tab.
- **Unit** (`QuickUpdateTargetTests`, extended): `ReflectingQuickUpdateTarget` forwards `Resolve`,
  forwards `Apply` to the inner target, reflects the **settled** record (so the fold is visible to the
  reflection), reflects on the optimistic *and* the revert apply, and no-ops the reflection when the
  inner target can't resolve the task.
- **Unit** (`QuickUpdatesCoordinatorTests`, new): the pure lifted helpers — `ColorForStatus`
  (case-insensitive hit, unknown status, null status, null options), `WithPriority` (level → canonical
  name+colour, null clears all three), `AdditionalLists` (home list excluded, empty when no detail).
- **E2E** (`single_task_quickupdates_check.py`, new — after `dotnet test` is green): four legs, each its
  own boot, `E2E_SINGLE_TASK=t0` + `E2E_TREE=1`.
  - A — `Ctrl+U` in single-task mode opens Quick Updates titled with the launch task (it used to flash a
    deferral); `Esc` pops back to the detail without quitting the tab.
  - B — `Enter` on a status row applies with no `_all` present: the screen stays open (#207), a
    `Set…`/`Setting…` flash appears and no `Could not set status` error; `Esc` returns to the detail.
  - C — on the Task Tree tab with `CHILDTWO` highlighted, `Ctrl+U` opens Quick Updates for **that** task
    (the screen title names CHILDTWO, not ROOT) and applying it leaves the detail header on ROOT.
  - D — dashboard parity for the same gesture (no `E2E_SINGLE_TASK`): list → detail → tree tab →
    `CHILDTWO` → `Ctrl+U` names CHILDTWO.

## Manual verification (TUI, not exercisable in CI)

`clickup-todo --task <id>` → `Ctrl+U` opens Quick Updates over the detail; Status/Priority commit on
`Enter` with the ✓ reconciling from the server value, the Assignees pane adds/removes by real member id,
the Lists pane shows the task's memberships. `Ctrl+→` to the Task Tree tab → highlight a subtask →
`Ctrl+U` opens *that* task's Quick Updates and its tree row's badges follow the commit. `Ctrl+U` on a
"(no task tree)" row flashes "Select a task to update."

## Scope / non-goals

- The dashboard's feed-opened-task path keeps `TaskItemProjection.FromDetail`'s placeholder assignee ids
  (#115) — a separate fix.
- No new keybinding: `Ctrl+U` is already context-wide in Detail (`Keybindings`) and already on the
  `DetailWithTaskTree` footer, so no footer/help change is needed.
- Single-task mode still runs no list-hierarchy walk, so the List pane's candidate pool stays
  persisted-only (decision 5).
