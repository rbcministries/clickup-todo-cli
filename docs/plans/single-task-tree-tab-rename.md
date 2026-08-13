# Plan — Single-task mode: F2 rename on the Task Tree tab (#604)

The deferred follow-up split out of #545 (contextual chords **H**, tree half). The dashboard host
(`TodoApp`) already renames the highlighted Task Tree node's task on `F2` — the detail screen raises
`RenameTreeTaskRequested`, the host opens the `RenameTaskScreen` overlay and writes through
`SetTaskNameAsync`, optimistically reflecting the new title on the tree row + header and reverting on
failure (`ShowTreeRename` / `ApplyTreeRename` in `TodoApp.cs`, shipped in #605).

Single-task launch mode (`SingleTaskApp`, `clickup-todo --task <id>`) also has the Task Tree tab
(#374) and the detail screen raises the **same** `RenameTreeTaskRequested` event there — but its
subscriber currently only **flashes a deferral** rather than performing the rename:

> `Renaming from the Task Tree isn't available in single-task mode yet.`

This wires that subscription to actually rename, mirroring the dashboard.

## Decision — admit rename as the low-risk exception (resolving #604's open point)

#604 asks whether single-task mode should "keep task mutations gated wholesale or admit rename as a
low-risk exception." **This plan admits rename**, on the grounds the issue itself pre-argues:

- The rename write goes straight through the existing `SetTaskNameAsync` facade (slice E, #592) and
  **does not depend on the #297 working-set decoupling** that Quick Updates needs. There is no
  dashboard working-set snapshot to reconcile in single-task mode — the simplification vs. the
  dashboard path — so no lone write-path coupling is introduced.
- Quick Updates **stays deferred** (it still flashes "tracked on #297"); this is scoped strictly to
  the tree-tab rename the detail screen already raises. The gate for the multi-facet write path is
  unchanged; only the single-facet, snapshot-free rename is enabled.

Everything else about single-task mode's mutation posture is untouched.

## Why now / dependencies (all merged to `main`)

- `SetTaskNameAsync` facade + `TaskService` passthrough — #592 (slice E).
- `TaskDetailScreen.RenameTreeTaskRequested` event + `TreeTaskRenameRequest` record + the
  `RequestTreeRename`/`SelectedTreeTask` gesture and the optimistic `ApplyTreeRename(taskId, newName)`
  (tree row + header reflection) — #600/#605 (slice H). Single-task mode already subscribes the event
  (to flash), so the screen side needs no change.
- `RenameTaskScreen` (collect-only modal) + pure `RenameTaskModel` (classify/trim) — #597.
- The dashboard reference implementation `TodoApp.ShowTreeRename` / `ApplyTreeRename` — #605.
- `SingleTaskApp`'s `ShowScreen`/`CloseScreen` stack + `TerminalTitle.Retitle` — #296/#298/#425.

No open PR is a blocker.

## Design — `SingleTaskApp.cs`

Mirror the dashboard's `ShowTreeRename` / `ApplyTreeRename`, minus the main-list snapshot (single-task
mode holds none). The rename is keyed to the raising **tab** (`DetailTab`) so a task opened by walking
the tree (#374, stacked child) renames its own node.

### 1. Supersede guard (mirror `TodoApp`)

Add the per-task commit-generation guard `TodoApp` uses so a stacked rename of one task and a rename
of another don't cancel each other's continuation, and a superseding rename wins:

```csharp
private readonly Dictionary<string, int> _nameCommitGen = [];
private int NextNameCommitGen(string taskId) => _nameCommitGen[taskId] = _nameCommitGen.GetValueOrDefault(taskId) + 1;
private bool IsCurrentNameCommit(string taskId, int gen) => _nameCommitGen.GetValueOrDefault(taskId) == gen;
```

### 2. Replace the deferral subscription

In `BuildDetailTab`, swap the flash for:

```csharp
screen.RenameTreeTaskRequested += (_, req) => ShowTreeRename(tab, req);
```

### 3. `ShowTreeRename(DetailTab origin, TreeTaskRenameRequest req)`

Open the same `RenameTaskScreen(req.CurrentName)` stacked over the detail via this host's `ShowScreen`
(like `OpenHelp`/`DispatchAgent`'s one-off screen); on a confirmed submit (`screen.Result is { }`)
apply through `ApplyTreeRename`.

### 4. `ApplyTreeRename(DetailTab origin, string taskId, string previousName, string newName)`

- Take a fresh commit gen for `taskId`.
- Optimistically reflect via a shared `ReflectTreeRename(origin, taskId, newName)` helper, then flash
  "Renaming to '…'".
- Off-thread `SetTaskNameAsync(taskId, newName)`; on success reflect the server-confirmed name
  (`confirmed ?? newName`) guarded by `IsCurrentNameCommit`; on failure revert to `previousName`
  (same guard). Mirrors `TodoApp.ApplyTreeRename`.

### 5. `ReflectTreeRename(DetailTab origin, string taskId, string name)`

- If the origin screen is still mounted (the root, or still on `_stack`), call
  `origin.Screen.ApplyTreeRename(taskId, name)` — updates the tree row and, when the node is that
  screen's current task, its header (the screen owns that logic).
- When `taskId == origin.TaskId` (the renamed node is this tab's own task): update the cached
  `origin.Task = origin.Task with { Name = name }` so a later dispatch / new-tab launch / refresh
  baseline reads the fresh title, and — for the **launch (root)** tab, which titles the terminal
  window (#418) — optimistically retitle via `TerminalTitle.Retitle` (the issue's "consider updating
  the window title optimistically on a current-task rename"; #425 otherwise waits for the next
  refresh). A child node's row rename leaves the cache/title alone.

The screen-mounted guard prevents touching a stacked child detail the user Esc'd away from mid-write.

## Tests

- **`tui-validate` — `single_task_tree_rename_check.py`** (new): mirror `tree_rename_check.py` against
  `SingleTaskApp` (`E2E_SINGLE_TASK=t0` + `E2E_TREE=1`, boot straight into the detail — no list). Three
  legs, each its own boot (the fake's task Name is mutable via the default `PUT /task/{id}` applier):
  - Leg A — F2 on the default-highlighted current node (ROOT) renames it; **both** the tree row and the
    detail header reflect the new title, old title gone.
  - Leg B — rename a non-current child (CHILDTWO): the tree row updates, the header (still ROOT) does not.
  - Leg C — Esc cancels: overlay closes, nothing written.
  Round-trips through the default backend's mutable-Name `PUT /task/{id}`, so only the two env gates are
  needed. Run only after `dotnet test` is green.
- Pure/facade coverage already exists: `RenameTaskModel` (#597), `SetTaskNameAsync` (slice E),
  `TaskDetailScreen.ApplyTreeRename` reflection (#605). The new code is host wiring (not unit-testable
  under Terminal.Gui in CI), validated by the E2E leg above.

## Manual verification (TUI, not exercisable in CI)

`clickup-todo --task <id>` → `Ctrl+→` to the Task Tree tab → highlight a node → `F2` opens the rename
overlay pre-filled → save renames the node's task (row updates in place, cursor kept); renaming the
launch task also updates the detail header and the terminal tab title. `F2` on a "(no task tree)"/error
row flashes "Select a task to rename." No second focusable pane; `F2` is a function key so type-ahead
(#12) is intact.

## Scope / non-goals

- **Quick Updates in single-task mode** stays deferred (tracked on #297) — out of scope.
- **Per-tab footer split** — unchanged; the `DetailWithTaskTree` footer already advertises `F2 ✏ rename`
  (shared with the dashboard), so no footer change is needed here.
