# Plan — Contextual chords (H, tree half): F2 = rename on the Task Tree tab (#545)

The remaining half of slice **H** (#545) of the contextual key/chord remapping epic (#537). The
**main-list** half shipped in #597 (F2 → `RenameTask` from the main list, reusing the `SetTaskNameAsync`
facade E/#592 and the `RenameTaskScreen`/`RenameTaskModel` overlay). This delivers the **Task Tree tab**
half: `F2` on a highlighted tree node renames that node's task title in place.

Implemented against the ratified model note `docs/plans/contextual-chord-model.md`:

- §3 (line 224): *"Task Tree tab → rename the task (`RenameTask` targeting the highlighted node). Here
  the only editable facet is the **title**, so *Rename* is accurate."*
- §5-H: *"`F2` = rename in the main task list **and Task Tree tab**, reusing E's facade… add a `MainList`
  `F2 → RenameTask` binding directly… when H lands, generalize the activation layer beyond Detail."*
- §2.2 activation table: `[DetailSubContext.TaskTree] = [KeyAction.RenameTask]`.

Unlike the non-item-tab `F2`/`Ctrl+E` alias question (#542, still an open maintainer decision), the Task
Tree tab is **unambiguous**: it has a highlighted node, and `F2` there is a plain title rename with no
alias to `Ctrl+E`. So this half is decision-free and independent of #542.

## Why now / dependencies (all merged to `main`)

- `SetTaskNameAsync` facade + `TaskService` passthrough — #592 (slice E).
- `DetailSubContext` + `ResolveDetail`/`DetailBindings` sub-context seam — #586 (slice C, #540).
- `RenameTaskScreen` (collect-only modal) + pure `RenameTaskModel` (classify) + `ApplyRename`
  (optimistic write + revert) — #597 (slice H, main-list half).
- The Task Tree tab (`_treeList`, `_loadedTreeRows`, `RenderTreeRows`) with per-row selection — #291.

No open PR is a blocker.

## Design

### 1. Keybinding table + sub-context activation (`Keybindings.cs`)

- Add `[(ScreenContext.Detail, KeyAction.RenameTask)] = "F2"`. `F2` is currently free in `Detail`
  (checklist rename is still `F8`; slice D/#600 moves it to `F2` on the **Checklists** sub-context, a
  different tab — the two coexist exactly as §2.2 designs: two actions share `F2`, disambiguated by
  sub-context). `RenameTask` keeps one token (`F2`) across `MainList` and `Detail`, so
  `AllBindingsOfAnAction_ShareOneKey` holds.
- Add `KeyAction.RenameTask` to `DetailTabActions[DetailSubContext.TaskTree]` (alongside the existing
  `AddComment`). `ResolveDetail(TaskTree, "F2") == RenameTask`; every other sub-context resolves `F2` to
  `null` (inert). No token collision within the TaskTree sub-context (`Ctrl+N`→AddComment, `F2`→RenameTask,
  plus the distinct context-wide chords).

### 2. Footer (`HelpLine.cs`)

Add `new("F2", "✏ rename")` to **both** `Detail` and `DetailWithTaskTree` (matching the main list's
`F2 ✏ rename` hint and the established "checklist chords appear in both base sets" precedent — the footer
is not yet split per tab, only the shared `Ctrl+N` label is relabelled). This satisfies both the flat
`Footer_ShowsTheTableKey_ForEveryBinding` cross-check (`FooterFor(Detail) = HelpItemSets.Detail`) and the
per-sub-context `DetailFooter_PerSubContext_ShowsEveryLiveBinding` (which iterates `sub=TaskTree` for both
tree-present/absent variants). `WithContextualNewLabel` still only reallocates for the Checklists tab, so
the `Assert.Same` footer-identity tests are unaffected.

> Cosmetic note (out of scope, inherent to the not-yet-per-tab footer): on the tree-present footer the
> checklist `F8 ✏ rename` hint and the new `F2 ✏ rename` hint both show even though each is live on only
> its own tab. Fully splitting the footer per sub-context is future work (composes with slice D/#600).

### 3. Detail screen (`TaskDetailScreen.cs`)

- New `public event EventHandler<TreeTaskRenameRequest>? RenameTreeTaskRequested;` with a small
  `public readonly record struct TreeTaskRenameRequest(string TaskId, string CurrentName)` (sibling of
  `LinkActivationRequest`). The screen only *requests* the rename; the host owns the overlay + write
  (mirroring `RenameTaskScreen` being collect-only and `TodoApp.ApplyRename` doing the write).
- `OnKey`: an `F2` branch, placed with the other tree-tab handlers (Enter/F6), guarded on the tree being
  front-most and routed through `Keybindings.ResolveDetail(CurrentDetailSubContext(), "F2") == RenameTask`
  (keeps dispatch and the per-tab footer label in lock-step, like the `Ctrl+N`/`Delete` blocks). A
  placeholder/message row (null task) is inert-but-flashed.
- `SelectedTreeTask()` helper → the selected row's `TaskItem?` (null for message/placeholder rows).
- `public void ApplyTreeRename(string taskId, string newName)` — the optimistic reflection the host calls:
  updates the matching `_loadedTreeRows` entry's task name and re-renders that row in place (cursor
  preserved, mirroring `SetTreeBadgeDisplay`); when `taskId == _task.Id` (the highlighted node is the
  current task — the default cursor position), also `UpdateData(_task with { Name = newName }, _comments)`
  so the detail **header** stays consistent. No-op when the tree hasn't loaded or doesn't contain the id.

### 4. Host — dashboard (`TodoApp.cs`)

- Subscribe `screen.RenameTreeTaskRequested += (_, req) => ShowTreeRename(screen, req);` at the detail
  construction site (beside `CycleBadgeDisplayRequested`).
- `ShowTreeRename(origin, req)`: open `RenameTaskScreen(req.CurrentName)` stacked over the detail (like
  Quick-Updates-from-detail), and on a confirmed submit call `ApplyTreeRename(origin, taskId, previousName,
  newName)`.
- `ApplyTreeRename(origin, taskId, previousName, newName)`: reuses the `_nameCommitGen` supersede guard.
  Optimistically calls `origin.ApplyTreeRename(taskId, newName)` (tree row + header) and, when the task is
  also in the main-list snapshot (`QuickUpdatesTaskById`), `UpdateTaskRow(... Name=newName ...)` so the list
  behind the detail matches too (a subtree node may be outside the snapshot — then only the tree updates).
  Off-thread `SetTaskNameAsync`; on success reflect the server-confirmed name to both, on failure revert
  both to `previousName`. Mirrors the existing `ApplyRename`.

### 5. Host — single-task mode (`SingleTaskApp.cs`) — deferred, flashed

Single-task mode intentionally defers task mutations (Quick Updates flashes "not available… tracked on
#297"). To stay consistent, subscribe `RenameTreeTaskRequested` to the same style of flash rather than
introducing a lone task-mutation write there. Tracked as a small follow-up issue, linked from the PR.

## Tests

- `KeybindingsTests`: `ResolveDetail(TaskTree,"F2")==RenameTask` and inert (`null`) on the other
  sub-contexts; `(Detail, RenameTask)` token is `F2`; update the `F2-is-RenameTask-only` guard's comment to
  note Detail now also binds it (a strengthening, not a weakening). The existing collision / round-trip /
  per-sub-context-footer theories cover the new binding automatically.
- `HelpLineTests`: the `Detail`/`DetailWithTaskTree` sets carry an `F2 ✏ rename` action item.
- `RenameTaskModel` is already unit-tested (#597).
- **`tui-validate`**: a Task Tree tab leg — open a task with subtasks, tab to Task Tree, `F2` renames the
  highlighted node, the row (and, for the current node, the header) reflect the new title with the cursor
  kept; a message/placeholder row `F2` is inert. Run only after `dotnet test` is green.

## Manual verification (TUI, not exercisable in CI)

Open a task with a subtree → `Ctrl+→` to the Task Tree tab → highlight a node → `F2` opens the rename
overlay pre-filled → save renames the node's task (row updates in place, cursor stays); renaming the
current-task row also updates the detail header. `F2` on a "(no task tree)"/error row flashes. No second
focusable pane; `F2` is a function key so type-ahead (#12) is intact.

## Scope / deferrals

- **Single-task-mode Task Tree rename** — deferred (flashed), tracked by a follow-up issue linked from the
  PR, consistent with the single-task Quick-Updates deferral (#297).
- **Per-tab footer split** (dropping inert chords per sub-context) — out of scope; future work with #600.
