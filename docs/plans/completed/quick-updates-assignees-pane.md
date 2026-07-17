# Quick Updates: Assignees pane — adopt the reusable `AssigneeSelectorView` (#158)

Part of the Quick Updates epic (#153). This is the **consumer** slice for the reusable
`AssigneeSelectorView` (#212, PR #224): replace the stubbed Assignees pane in
`QuickUpdatesScreen` with an embedded selector in **immediate-apply** mode, and provide the
host-side write + row reconcile from `TodoApp`.

## Dependencies (all landed on `main`)

- **Reusable component** `AssigneeSelectorView` + pure `AssigneeSelectorModel` (#212). It already
  implements everything this issue's pane specifies: empty-search state = current assignees (`✓`)
  topped up from the #155 frequency pool; `TimeProvider`-debounced substring `Match` off the UI
  thread; pick a result → add, pick a `✓` row → remove; `AssigneeSelectorMode.ImmediateApply`
  (optimistic add/remove, reconcile from the server-confirmed set, revert-on-failure) driven by an
  injected `applyAsync` callback.
- **Quick Updates shell + Status/Priority panes** (#156/#157, PR #207) — `QuickUpdatesScreen` with
  the Tab-cycled `_panes`, the shared `OnPaneKey`, and the `ShowQuickUpdates`/`ApplyStatus`/
  `ApplyPriority`/`UpdateTaskRow` host plumbing to mirror.
- **Assignee facade** `IClickUpClient.AddTaskAssigneeAsync` / `RemoveTaskAssigneeAsync`
  → `IReadOnlyList<TaskAssignee>` (server-confirmed set) (#154).
- **Frequency cache** `AssigneeFrequencyCache.Match(query, exclude)` /
  `TopMostFrequent(n, exclude)` (#155), already held by `TodoApp._assignees` and warmed in
  `OnTasksLoaded`.

No curated-spec / Kiota / `Generated/` change, no new ClickUp API surface. The pane is a single
focusable composite inside a modal screen, so it does **not** reintroduce a second focusable pane
on the main list (#3), and it adds no bare-letter keybindings (#12) — inside the screen only
Tab/Shift+Tab/↑/↓/Enter/Esc/F1.

## Design

### 1. Service layer — `TaskService` (testable, no terminal)

- `Task<IReadOnlyList<TaskAssignee>> AddAssigneeAsync(taskId, userId, ct)` and
  `RemoveAssigneeAsync(taskId, userId, ct)` — thin wrappers over the client facade, mirroring
  `SetStatusAsync`/`SetPriorityAsync`.
- Pure `ApplyAssigneesChange(tasks, taskId, assignees)` — the assignee sibling of
  `ApplyStatusChange`/`ApplyPriorityChange`: returns a new snapshot with the matching task's
  `Assignees` replaced, order/count untouched, input not mutated. Unit-tested.

### 2. `QuickUpdatesScreen` — embed the selector

- Replace the stub `_assigneesList` (`ListView` over `QuickUpdatesModel.AssigneeRows`) with an
  `AssigneeSelectorView _assignees` built in `ImmediateApply` mode, seeded with the task's current
  assignees, **no** locked default (Quick Updates has no self-lock — that's the New Task screen's
  rule, #213).
- Constructor gains the pool delegates + the apply callback (plus optional `TimeProvider`/debounce
  for a future test seam), all supplied by the host:
  `Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> assigneeMatch`,
  `Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> assigneeTopFrequent`,
  `Func<ToggleKind, TaskAssignee, CancellationToken, Task<IReadOnlyList<TaskAssignee>>> applyAssignee`.
- `_panes` becomes `View[]` (`_statusList`, `_priorityList`, `_assignees`); `OnPaneKey` is attached
  to the selector too so bubbled **Tab/Shift+Tab** cycle focus, **Esc** exits, **F1** opens help.
  The selector handles **Enter** (add/remove) and **↑/↓** (box↔list) internally and marks them
  `Handled`, so they never reach `OnPaneKey`; the `Enter`-commit branch there stays keyed to the
  Status/Priority `ListView`s by identity, so it is inert for the selector.
- Forward the selector's `Flash` event to `RequestFlash` so a locked no-op / write failure surfaces
  in the shared status line.
- Remove the now-dead `QuickUpdatesModel.AssigneeRows` stub and its two unit tests (its job moved
  to `AssigneeSelectorModel.EmptyStateRows`, already tested under #212).

### 3. `TodoApp.ShowQuickUpdates` — supply delegates + apply/reconcile

- Pass `_assignees.Match` / `_assignees.TopMostFrequent` as the pool delegates.
- `applyAssignee` runs off the UI thread (the view calls it from its own task): dispatch on
  `ToggleKind` to `_tasks.AddAssigneeAsync` / `RemoveAssigneeAsync`, then marshal a row reconcile
  onto the UI thread — `UpdateTaskRow(task with { Assignees = confirmed }, sending: false)` — and
  return the confirmed set so the **view** reconciles its own pane display. `UpdateTaskRow` gains an
  `ApplyAssigneesChange` line so `_all` stays in sync per-field (the `updated` record carries the
  current value for the unchanged fields, so status/priority paths remain no-ops for assignees and
  vice-versa — same discipline #157 used when it added the priority line).
- The **view owns** the optimistic add/remove + revert-on-failure on its own pane and the
  out-of-order-write generation guard; the **host owns** the server write and the main-list row
  reconcile from the confirmed set. On write failure the host row simply never changed (it only
  updates on confirm), so there is nothing to revert host-side — consistent with the row only ever
  reflecting server-confirmed assignees. Overlapping same-task assignee writes settle on the
  last-returning confirmed set and self-heal on the next background refresh (documented; the
  status/priority paths accept the same superseded-continuation model).

## Tests

- `TaskService.ApplyAssigneesChange`: matching-task only; order/count preserved; input not mutated;
  no-match returns an equivalent snapshot; clear-to-empty.
- The selector's search/debounce/toggle/empty-state logic is already unit-tested via
  `AssigneeSelectorModelTests` (#212) — not re-covered here.
- `dotnet build -c Release` 0/0, `dotnet test -c Release` green (integration self-skips),
  `dotnet format --verify-no-changes` clean.
- **`tui-validate`** (host integration, per `CLAUDE.md`): open Quick Updates → Tab to Assignees →
  the empty-state list shows current assignees `✓` topped up from the frequency pool → type to get
  a debounced match → Enter adds (box clears, list restores) → remove a `✓` row → Esc returns and
  the main-list row reflects the change.

## Acceptance criteria (from #158)

- Tab into Assignees focuses the search box; `Down`/`Up` move between box and list top — **met by
  the embedded `AssigneeSelectorView`** (`OnSearchKey`/`OnListKey`).
- Empty search shows current assignees with `✓`, topped up to 10 rows from most-frequent
  assignees, list scrolls beyond 10 — **met by `AssigneeSelectorModel.EmptyStateRows` + capacity**.
- Typing replaces the list with debounced (~1s) partial-name matches; selecting a result adds the
  user immediately, clears the box, restores empty state — **met by the view's debounce + `Pick`**.
- Assignee add/remove is optimistic and reverts on server failure — **met by `ImmediateApply` +
  the host `applyAssignee` wrapping `Add/RemoveTaskAssigneeAsync`**.

## Deferred / not in scope

- Lifting the write-block on tasks not assigned to the current user (context parents / foreign
  subtasks) → **#160** (the `OpenQuickUpdates` guards are untouched here).
- New Task screen consumption of the same selector (collect-selection) → **#213**.
