# Reusable `AssigneeSelectorView` component (#212)

Part of the "Writing New Content" epic (#208). Build the assignee selector **once**, as a
reusable Terminal.Gui `View`, so both the New Task screen (#213, E) and the Quick Updates
Assignees pane (#158) share a single implementation instead of growing two copies. This is
net-new: no reusable selector exists today (the Quick Updates Assignees pane is a stubbed
`ListView` of the current assignees, `QuickUpdatesModel.AssigneeRows`).

## Dependencies (all landed on `main`)

- Candidate pool: `AssigneeFrequencyCache` (#155) — `Match(query, exclude)`,
  `TopMostFrequent(n, exclude)`, backed by pure rules in `AssigneeFrequency`. Its query API has
  **no UI consumer yet**; this component becomes the first.
- Write methods (immediate-apply mode): `IClickUpClient.AddTaskAssigneeAsync(taskId, userId)` /
  `RemoveTaskAssigneeAsync(taskId, userId)` → `IReadOnlyList<TaskAssignee>`.
- Models: `TaskAssignee(long Id, string Name)`, `WorkspaceMember`.
- Reusable-`View` precedent: `Tui/DetailPaneView.cs` (pure `BuildCells` unit-tested; the draw
  override is CI-untestable Terminal.Gui glue) — mirror that split.
- `TimeProvider` seam (used by `TaskService`, `StatusCache`) for the debounce timer.

No curated-spec / Kiota / `Generated/` change, no new ClickUp API surface, no bare-letter
keybinding, no second focusable pane on the main list (this is a modal-screen component).

## Scope decision — foundation slice, consumers deferred

This ships the **reusable component + its pure companion + unit tests** — the same shape the
epic's other foundation slices took (#209 create-task facade / #210 create-comment facade both
shipped backend + tests with **no UI in the slice**, PRs #219/#218).

Adopting the component into a live screen is deferred to its consumer issues, and here's why
that's the right cut rather than scope-trimming:

- **Quick Updates Assignees pane → #158.** That issue *is* "Quick Updates: Assignees pane —
  search box + selector list, immediate apply", and its host-side immediate-apply plumbing
  (optimistic add/remove + revert in `TodoApp`, foreign/context-row reconcile) is its declared
  work. Wiring it now also collides head-on with **open PR #207**, which is actively reworking
  `QuickUpdatesScreen` / `ShowQuickUpdates` / `ApplyStatus` for the Status & Priority panes.
- **New Task screen → #213 (E).** The screen shell doesn't exist yet.

So the component is built to be consumed, both modes present in its API, and the hand-off is
noted on #158. `tui-validate` of the rendered View happens when a consumer hosts it (noted in
the PR); this slice validates the logic via the pure companion, per the repo's `DetailPaneView`
convention.

## Design — pure companion + thin View (the repo split)

### 1. Pure logic — `Tui/AssigneeSelectorModel.cs` (static + small records)

All row assembly, toggle/lock decisions, and the debounce **coalescing** decision live here so
they're unit-tested without a terminal.

- `record AssigneeRow(long Id, string Name, bool Selected, bool Locked)` — one rendered row.
  `Format(AssigneeRow)` → `"✓ {Name}"` when `Selected`, else `"  {Name}"` (2-col prefix, aligns
  with the status/priority rows' `"  "` indent). A locked row still shows `✓` (it's selected and
  non-removable).
- `EmptyStateRows(selected, lockedIds, topFrequent, capacity)` → `IReadOnlyList<AssigneeRow>`:
  the empty-search state. **All** currently-selected assignees first (`✓`, `Locked` when in
  `lockedIds`), then `topFrequent` candidates **excluding** anyone already selected, filling up
  to `capacity` total rows. Selected always shown even if they alone exceed `capacity` (the
  `ListView` scrolls beyond `capacity`). De-dupes by id; drops blank names / non-positive ids.
- `SearchResultRows(matches, selectedIds)` → rows for a non-blank query: `matches` mapped to
  **unselected** rows, excluding anyone already selected (you *add* via search; you *remove* via
  the empty-state `✓` rows). Blank names / dup ids dropped.
- `Toggle(selectedIds, lockedIds, id)` → `ToggleResult(ToggleKind Kind, long Id)` where
  `ToggleKind ∈ { Added, Removed, LockedNoOp }`. Pure decision only — the caller mutates its set
  and (immediate mode) fires the server write. Selecting a locked, already-selected id →
  `LockedNoOp`. Selecting an unknown id → `Added`. Selecting a selected, unlocked id → `Removed`.
- Debounce coalescing: `ShouldRunSearch(long capturedStamp, long currentStamp)` → `capturedStamp
  == currentStamp`. The View bumps a monotonic stamp on every keystroke, captures it, schedules a
  `TimeProvider` timer for the debounce interval; when the timer fires it runs `Match` only if no
  newer keystroke has arrived. Fully testable with no real waits.

### 2. Thin View — `Tui/AssigneeSelectorView.cs` (`: View`, CI-untestable glue)

- Layout: a `TextField` search box on the top row over a `ListView` filling the rest — **one**
  focusable composite (no second focusable pane on the main list; the component is used inside
  modal screens only). `Down`/`Up` move between the box and the list top.
- State: `_selected` (ordered `List<TaskAssignee>`), `_lockedIds` (`HashSet<long>`),
  `_searchStamp` (long). Ctor takes the candidate-pool query delegates
  (`Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> match` and a `topFrequent` provider —
  i.e. `AssigneeFrequencyCache.Match` / `TopMostFrequent`), an optional seeded/locked default
  assignee, an `AssigneeSelectorMode`, a `TimeProvider`, and the debounce interval.
- **Two modes** (`enum AssigneeSelectorMode { ImmediateApply, CollectSelection }`):
  - `ImmediateApply` (Quick Updates): on add/remove, invoke async `AddAssignee` / `RemoveAssignee`
    callbacks (host wraps `Add/RemoveTaskAssigneeAsync` with optimistic + revert). Callbacks are
    injected so the View stays free of the client and off-thread marshalling.
  - `CollectSelection` (New Task): add/remove mutate `_selected` only; **no** server write. Host
    reads `Selection` on Save and sends them via `CreateTaskAsync` (#209).
- Locked default: seeded assignee is added + its id put in `_lockedIds`; `Toggle` returns
  `LockedNoOp` on a remove attempt → a brief flash via an event (host shows it). Other hosts pass
  no lock.
- Typing bumps `_searchStamp`, schedules the debounce timer; on fire, `ShouldRunSearch` gate →
  run `match` **off the UI thread**, marshal results back and re-render via `SearchResultRows`.
  Empty box → `EmptyStateRows`. Picking a result `Toggle`s, clears the box, restores empty state.
- Exposes `IReadOnlyList<TaskAssignee> Selection`, a `SelectionChanged` event, and a `Flash`
  event (locked no-op / write-failure text). Keeps all cache/formatting/decision logic in
  `AssigneeSelectorModel`.

## Tests — `tests/ClickUpTodo.Tests/AssigneeSelectorModelTests.cs`

- `Format`: selected → `"✓ …"`, unselected → `"  …"`, alignment.
- `EmptyStateRows`: selected-first with `✓`; top-frequent top-up excluding selected; capacity
  cap with selected always shown (over-capacity selected still all present); locked flagged;
  blank/dup/non-positive dropped; empty pool → just selected (or nothing).
- `SearchResultRows`: matches mapped unselected; already-selected excluded; blanks/dups dropped.
- `Toggle`: Added (unknown id), Removed (selected unlocked), LockedNoOp (selected locked);
  caller-set mutation contract (returns decision, doesn't mutate its input).
- `ShouldRunSearch`: equal stamps → run; stale capture (newer keystroke) → skip; monotonic.

`dotnet build -c Release` 0/0, `dotnet test -c Release` green (integration self-skips),
`dotnet format --verify-no-changes` clean.

## Deferred (tracked)

- Adopt in the Quick Updates Assignees pane (immediate-apply, host plumbing, `tui-validate`) →
  **#158** (hand-off note posted).
- Adopt in the New Task screen (collect-selection) → **#213 (E)**.
