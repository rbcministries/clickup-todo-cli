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

A placeholder/message row (`(no task tree)`, `Loading…`, a load error) **falls back to this screen's own
task** rather than flashing. This started as an inert-but-flashed guard mirroring `RequestTreeRename`, and
the review caught that it turns `Ctrl+U` into a dead key while the tree is still loading and permanently
after a load failure. Rename has no sensible fallback ("rename *what*?"); `Ctrl+U` is a context-wide Detail
action and the task in the header is right there, so it stays live on every tab in every state.

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
snapshot, whose copy of the node may differ in title/ancestry from the tree's own fetch.

Two corrections came out of the review of this branch:

- **The status colour has to travel with the name.** `ApplyStatusChange` folded only `StatusName`, so a
  committed status rendered in the *previous* status's colour. On the main list the next background poll
  repaints it; on the Task Tree tab it does not — `_treeLoaded` is set once per screen and never reset, so
  a stale colour would persist for the life of that view (the one-shot tree load is now tracked on its own,
  #632). `ApplyStatusChange` now takes the colour alongside the name (its only production caller is `ApplyFieldChanges`, which passes `updated.StatusColor`
  — a no-op for every caller that doesn't set it), and `ApplyStatus` resolves the committed colour it
  already computes for the header reflection onto the applied record, on the optimistic, confirmed and
  reverted apply alike. The list row's colour lag is fixed as a side effect.
- **A projected seed is not reflected.** The fold re-applies assignees, so reflecting a
  `TaskItemProjection.FromDetail` record (placeholder id `0` assignees) would degrade a tree row that holds
  the real ids — permanently, since the tree never re-fetches. The decoration is therefore applied only
  when the seed is authoritative (a snapshot/visible row, a tree row, or single-task mode's fetched item);
  a feed-opened task's tree row keeps its pre-commit values, exactly as before.

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
delegates: the list `Ctrl+U`, the detail `Ctrl+U`, and the new tree-target case. It seeds from the *same*
resolution its write target uses — `QuickUpdatesTaskById` (snapshot **then visible rows**), then the tree
row, then the detail projection as a last resort. Previously the seed consulted `_all` alone while the
target consulted `_all` + `_rows`, so a foreign subtask (#70/#179) or context parent (#46) — which live only
in `_rows` — was seeded from the lossy projection even with an authoritative row in hand, and an
Assignees-pane remove there would have written id `0`. (Pre-existing; found in review of this branch and
fixed here because the seed and the target must agree for the reflection decision above.)

### `SingleTaskApp`

Replaces the deferral flash with `OpenQuickUpdates(tab, request)`:
- tree request ⇒ target task is the tree row's `TaskItem`; else resolve the tab's task via
  `GetTaskItemAsync` off the UI thread.
- target = `new SingleTaskUpdateTarget(task)`, decorated with the tree-row reflect.
- `reflect` = the tab's screen when the target task **is** the tab's task, else null.

### `TaskDetailScreen`

- `QuickUpdatesRequested` becomes `EventHandler<QuickUpdatesRequest>`;
  `QuickUpdatesRequest(TaskItem? TreeTask)` carries the tree row's task (null ⇒ "the task I show").
- `RequestQuickUpdates()` — the tree-tab branch, falling back to this screen's task (decision 2).
- `ApplyTreeTaskFields(TaskItem updated)` + the shared private `ReplaceTreeRow` that `ApplyTreeRename`
  now also uses. `ReplaceTreeRow` gained a `_disposed` guard: a write continuation reaches it through the
  write target, which outlives the screen, so unlike every other reflection (all guarded by the
  coordinator's `isMounted` or the hosts' own mounted checks) it can be called on a torn-down view.

### Concurrency corrections (also from review)

- **`_statusCommitGen` / `_priorityCommitGen` are now per task id**, like `TodoApp._nameCommitGen`. One
  `Ctrl+U` always targets exactly one task, so this is never about staging two tasks at once; it is about
  two *sequential* commits whose (deliberately untokened) writes overlap in time. One global counter each
  let the second one's generation cancel the first task's confirm/revert — stranding that row on a value
  the server rejected, with no error flashed, or on a never-cleared `(sending…)` marker. **Pre-existing**:
  the main list could already reach it (commit on row A, Esc, move, commit on row B). Tree-scoped `Ctrl+U`
  makes it far easier to hit — same screen, no navigation — which is what surfaced it.
- **`_armedListRemoval` is behind a lock.** It is written from the thread-pool thread inside
  `ApplyListAsync` (the writes are deliberately untokened, so one can still be in flight) and cleared from
  the UI thread in `Show`; unsynchronised, that multi-word struct can be read torn. Pre-existing in
  `TodoApp`; fixed on the move rather than carried into a class documented as UI-thread-only.

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
    (the screen title names CHILDTWO, not ROOT); the commit repaints CHILDTWO's own row badge
    (`(IP)` → `(C )`/`(IR)`) and no sibling's, and the launch task's header **status** — not just its title
    — is unchanged. Both of those were vacuous in the first draft (a title-only assertion can't see a
    wrongly-repainted header status, and nothing asserted the row moved at all); each is now verified to
    fail against a deliberately broken build.
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
