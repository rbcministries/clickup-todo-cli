# New Task screen shell + `Ctrl+N` launch (#213)

Part of the **Writing New Content** epic (#208), sub-issue **E**. A dedicated compose screen to
file a new task from the main list — the core three fields (Name, Description, Assignees). Optional
Due Date / Priority are sub-issue **F** (#215) and are explicitly out of scope here; Status and Tags
are out of scope for the whole slice (new tasks take the list default).

## Foundations already on `main`
- **Create-task facade** (#209, PR #219): `IClickUpClient.CreateTaskAsync(listId, NewTaskRequest, ct)`
  → `TaskItem`. `NewTaskRequest { Name (required), Description?, Assignees (long[]), PriorityLevel?,
  DueDateMs? }`. Optional fields omitted when unset. `ClickUpClient` implements it; the interface
  default throws (read-only fakes needn't implement it).
- **Reusable `AssigneeSelectorView`** (#212, PR #224) + pure `AssigneeSelectorModel`. Constructor takes
  `match` / `topFrequent` pool delegates, `initialSelected`, a `lockedDefault` (pre-selected,
  non-removable), a `mode` (**`CollectSelection`** for New Task — in-memory until save), and exposes
  `Selection` (`IReadOnlyList<TaskAssignee>`), `SelectionChanged`, and `Flash`. A single tab stop (the
  search box), so Tab/Esc/F1 fall through to the host screen — no second focusable pane (#3/#38).
- **Candidate pool** `AssigneeFrequencyCache` (#155): `Match(query, exclude)`,
  `TopMostFrequent(n, exclude)`, already wired in `TodoApp` (`_assignees`, warmed in `OnTasksLoaded`).

## Phases

### Phase 1 — service seam + pure validator + tests
- `TaskService.CreateTaskAsync(listId, NewTaskRequest, ct)` — thin passthrough to
  `client.CreateTaskAsync` (mirrors `SetStatusAsync`/`SetPriorityAsync`).
- `TaskService.UserName` — the signed-in user's display name, needed to seed the locked-self assignee
  (the `AssigneeSelectorView` drops a locked default with a blank name). Added as a **trailing optional**
  ctor param (`string userName = ""`) so existing positional call sites (Program.cs + tests) are
  unaffected. `Program.cs` captures the `GetMeAsync()` result and passes `userName: me.DisplayName`.
- **Pure `NewTaskForm`** (mirrors `FilterSortGroupForm`): `TryBuild(name, description, assigneeIds,
  out NewTaskRequest, out error)`. Trims name → required (else error); trims description → null when
  empty (so the facade omits it); de-dupes + drops non-positive assignee ids preserving order.
  Unit-tested in `NewTaskFormTests`.

### Phase 2 — `NewTaskScreen : Screen` + help
- `NewTaskScreen`: **Name** `TextField` (Y=1), **Description** multi-line `TextView`
  (`TabKeyAddsTab = false`), embedded **`AssigneeSelectorView`** in `CollectSelection` mode seeded with
  the current user as the `lockedDefault`, **Save** (`IsDefault`) / **Cancel** buttons.
- **Keep-open-on-failure** requires the create to run while the screen is still mounted, so the screen
  takes an injected `Func<NewTaskRequest, CancellationToken, Task<TaskItem>> createAsync` (the same
  injected-async-callback pattern #212's `applyAsync` established) rather than the Result-then-close
  pattern of `FilterSortGroupScreen`. On Save: validate via `NewTaskForm` (flash + return on error);
  disable Save + flash "Creating…"; run `createAsync` off the UI thread; on success raise
  `Created(TaskItem)` then `Close()`; on failure re-enable Save and flash the error (screen stays open).
  A screen-owned `CancellationTokenSource` is cancelled on `Dispose`. `Esc`/Cancel closes without
  creating; `F1` help; `OnShown` focuses Name.
- Help: `HelpItemSets.NewTask` (Tab moves · Enter/Save · Esc cancel · F1 help), `NewTaskScreen.HelpItems`
  returns it, and a `Ctrl+N new task` item is added to `HelpItemSets.MainList`. `HelpLineTests` assert
  both.

### Phase 3 — TodoApp wiring
- `OnListKey`: `Ctrl+N` → `OpenNewTask()` (in the `IsCtrl` switch, `Ctrl+N` is free).
- `OpenNewTask()`: guard `ActiveScreen is not null`; guard a configured `PersonalTasksListId`
  (flash + return if unset); build the selector pool delegates off `_assignees`, the locked-self
  `TaskAssignee(_tasks.UserId, name-or-"Me")`, and `createAsync => _tasks.CreateTaskAsync(listId, …)`;
  `ShowScreen`. On `Created`: set `_pendingSelectId = created.Id`, flash, `RequestRefresh()`.
- `_pendingSelectId`: honored in `OnTasksLoaded`'s `Render(keepTaskId: …)` (prefer it over the current
  cursor, then clear) so the refreshed list lands on the new task when present; falls back to the first
  row otherwise (`Render` already does this).

### Phase 4 — validate & ship
- `dotnet build -c Release` (0/0), `dotnet test -c Release` green, `dotnet format`.
- `tui-validate`: `Ctrl+N` opens the screen → fill Name → Tab to Assignees → self is locked `✓`
  (remove refused) → add another → Save → returns to the list, refreshed, new task selected; A/B
  non-regression (latency + color + detail) vs stock.
- Draft PR → subagent review → address → ready.

## Invariants
- No `Generated/` edit, no curated-spec change, no Kiota regen — the create facade already exists.
- No second focusable pane (#3/#38): the selector is a single-tab-stop composite in a modal screen;
  the main list stays a single sectioned `ListView`.
- No bare-letter keybinding (#12): the launch is the `Ctrl+N` chord; inside the screen only
  Tab/Shift+Tab/↑/↓/Enter/Esc/F1.
- Personal-token raw `Authorization` header untouched; integration tests stay `SkippableFact`.

## Deferred (tracked)
- Optional **Due Date + Priority** fields → #215 (F). `NewTaskRequest` already carries them; this
  screen just doesn't surface them.
- Context-aware target list (create into the selected task's list rather than always
  `PersonalTasksListId`) → later follow-up (noted in #209).
