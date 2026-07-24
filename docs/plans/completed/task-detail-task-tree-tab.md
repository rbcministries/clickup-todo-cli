# Task Tree tab in Task Detail (#291)

**Issue:** [#291](https://github.com/rbcministries/clickup-todo-cli/issues/291) — Mouse/UX (F)
of the mouse-interaction epic [#283]. Blockers **P** (#284, `TaskRowRenderer`) and **A**
(#286, `RowHitTester`) are merged.

## Goal

Add a fifth **Task Tree** tab to `TaskDetailScreen` that shows the current task's **ancestry
(parent chain) + the task itself + its descendants (subtasks)**, indented and badged exactly
like the main list (via the shared `TaskRowRenderer`, **P**). Pressing **Enter** or
**double-clicking** a tree item navigates the detail screen to that task; the current-task row
is a no-op. After walking into one or more tasks via the tree, a **single Esc returns to the
main list** (not an ever-growing back-stack); opening detail from the list and Esc-ing straight
back is unchanged.

## Key design decisions

### Navigation = host-driven stacking (walkable back)

`TaskDetailScreen` holds no services and its write callbacks are bound per-task by the host, so
detail→detail navigation is host-driven:

- Add `event EventHandler<string>? OpenTaskRequested` to `TaskDetailScreen` (mirrors
  `NotificationsFeedScreen`). The tree tab raises it with the target task id.
- `TodoApp` wires `screen.OpenTaskRequested += (_, id) => OpenTaskDetail(id);` — the target's
  detail is **stacked over** the current one on the single `_screens` back-stack.

Net stack effect of tree navigation from detail **A** to task **B**: `[list, A]` → `[list, A, B]`.
`Esc` = Back walks back one task at a time (`B` → `A` → list), and at the list root `Esc`/Quit
hands off to the exit seam (`RequestExit`). First-open-from-list is unchanged.

> **Alignment note (updated for #401/#298/#387):** an earlier revision of this PR did
> detail→detail as **replace-in-place** (close the prior detail so one `Esc` returned straight to
> the list). Per the maintainer decision recorded in #401 — `Esc` = Back is canonical, browse the
> visited-task chain — and to stay uniform with #387's Ctrl+O detail→detail path, this PR now
> **stacks** instead. There is one back-stack (`_screens`), not a discarded history. Wiring the
> shared pure `NavigationHistory<T>` model (#401, currently unused) as the logical mirror is a
> coordination point with the in-flight #387 (which also opens details) — see the PR discussion.

### Data: reuse `SubtaskArranger`, no `Generated/` or spec change

- **Fetch** (`TaskService.GetTaskTreeAsync(taskId, ct)`, off-thread, best-effort):
  1. `current = GetTaskItemAsync(taskId)` — the task as a stable `TaskItem` (carries `ParentId`,
     structured assignees, status/priority colours — everything `TaskRowRenderer` needs).
  2. **Ancestry:** walk up from `current.ParentId`, fetching each ancestor via
     `GetTaskItemAsync`, cycle-safe, capped at `MaxAncestorFetches` (10). Best-effort: a failed
     fetch stops the walk.
  3. **Descendants:** bounded BFS from `taskId` via the existing `GetSubtasksAsync`, capped at
     `MaxTreeSubtaskFetches` (25 round-trips) + a depth cap, deduped, best-effort.
  4. Assemble via the pure `TaskTreeArranger.Build(...)`.
- **Assemble** (`TaskTreeArranger`, pure, unit-tested): concatenate `[ancestorsTopDown…,
  current, descendants…]` into one ordered `TaskItem` list and delegate nesting to the existing
  `SubtaskArranger.Arrange` (all-expanded, no context parents, no suppression). Map each
  `ArrangedRow` to `TaskTreeRow(TaskItem Task, int Depth, bool IsCurrent)` with `IsCurrent =
  (Task.Id == currentTaskId)`. Ancestors nest into a single chain; the current task and its
  subtree hang off the nearest ancestor. No-ancestor and no-descendant cases fall out naturally.

New client method `IClickUpClient.GetTaskItemAsync(id, ct)` = `GET /task/{id}` → the existing
private `Map` (not `MapDetail`), mirroring `GetTaskDetailAsync`. Interface addition ⇒ a one-line
`NotImplementedException` stub in each of the 7 in-memory test fakes (kept honest).

### Rendering: `TaskRowRenderer` + `StatusBadgeListSource` over a `ListView`

The tree tab is a focusable `ListView` (its own scroll target) with `Title = "Task Tree"`,
appended to the length-4 `_tabContents`/`_scrollTargets` arrays only when a tree loader was
supplied (so `SingleTaskApp` is unaffected). `CycleTab`/`FocusCurrentPane`/`ScrollActiveTab` are
array-length-driven and pick it up automatically.

- **Lazy load:** the tree is fetched the first time the user cycles to the tab (a "Loading task
  tree…" placeholder until then), via an injected
  `Func<CancellationToken, Task<IReadOnlyList<TaskTreeRow>>>? loadTaskTreeAsync` — same
  injected-async seam the comment/description callbacks use. Opening any detail is not slowed.
- **Badges without the F6 toggle:** render each row with a fixed `BadgeDisplay.Text` — the
  `"{glyph} {name}"` form already shows the icon **and** the full text, satisfying the issue's
  "both icon and text, no toggle". `currentUserId` + `badgeDisplay` are threaded into the ctor
  (host passes `_tasks.UserId` / `_config.BadgeDisplay`).
- Rows feed a `StatusBadgeListSource(display, badges, headerAttrs: null, searchKeys: titles)`
  (type-ahead by title, #12). A parallel `List<TaskItem?> _treeRows` backs hit-testing/selection.

- **Enter:** in `OnKey`, when the tree tab is front-most, Enter resolves the selected
  `_treeRows[SelectedItem]`; a non-current task raises `OpenTaskRequested`, the current task
  no-ops (a brief "Already viewing this task." flash).
- **Double-click:** `_treeList.MouseEvent` → `RowHitTester.TaskAt(pos.Y, _treeList.Viewport.Y,
  _treeRows)` (the shared **A** helper), same non-current/no-op rule.

## Hard-rule compliance

- **No `Generated/` hand-edit, no curated-spec change** — `GetTaskItemAsync` uses the existing
  generated `GET /task/{id}` builder + the existing `Map`.
- **No second focusable pane (#3)** — the tree is a tab *inside* the existing single detail
  screen; the dashboard's single sectioned `ListView` model is untouched.
- **No bare-letter shortcut (#12)** — the tab is reached by the existing `Ctrl+→`/`Ctrl+←`
  cycle; bare letters stay reserved for the tree list's type-ahead.
- **Tests land with the code** — pure `TaskTreeArranger` + `TaskService.GetTaskTreeAsync`
  (fake-client) unit tests; TUI verified by build + a new `tui-validate` scenario.

## Phases

1. **Model + service:** `GetTaskItemAsync` (interface + impl + fake stubs); `TaskTreeRow` +
   `TaskTreeArranger`; `TaskService.GetTaskTreeAsync`. Unit tests. → opens draft PR.
2. **TUI:** `TaskDetailScreen` Task Tree tab (lazy load, render, Enter, double-click,
   `OpenTaskRequested`); `TodoApp` replace-in-place navigation wiring.
3. **E2E + finalize:** `tree_tab_check.py` + fake-backend `parent`/`subtasks` payloads; run
   `tui-validate`; mark PR ready; review subagent.

## Deferred (tracked)

- **Task Tree tab in single-task launch mode** (`SingleTaskApp`): its Esc = "quit the tab" model
  differs from "return to the main list", and cross-tab navigation is the domain of the
  multi-tab epic (#292, #298). A follow-up issue is filed and linked from the PR.
- **Fold interactivity in the tree tab:** the tree renders fully expanded (indentation only, no
  ▶/▼). Folding within the tree is not required by #291 and is left out of scope.
