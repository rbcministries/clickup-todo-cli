# Quick Updates launchable from Task Detail — return to origin on exit (#159)

Part of the Quick Updates epic (#153). Layers navigation on top of the merged screen
shell (#156). The three panes (#157/#158) behave identically regardless of origin — only
the **open path** and the **return target** differ.

## Goal / acceptance criteria

- Opening Quick Updates from the **list** (Space) and pressing `Esc` returns to the list
  with the same task selected (unchanged behaviour).
- Opening Quick Updates from the **Task Detail** view (`Ctrl+U`) and pressing `Esc`
  returns to that task's detail view, **showing any values just changed**.
- No regression to the detail view's own `Tab`/`Esc`/`Ctrl+*` handling.
- `dotnet test` green; `tui-validate` confirms both open→edit→exit round-trips land on the
  correct origin.

## Key design facts (verified on `main`)

- Screens live on a stack in `TodoApp` (`_screens`; `ActiveScreen => _screens[^1]`).
  `ShowScreen` hides the current top and mounts the new one; `CloseScreen` restores the
  layer **beneath** it (the screen below, or the list). **Return-to-origin is therefore
  free from the stack** — the only thing stopping Quick Updates from stacking over the
  detail today is the `ActiveScreen is null` guard in `ShowQuickUpdates`.
- `ShowScreen` does **not** start a nested `Application.Run` loop (it toggles `Visible` +
  focus), so stacking a second full screen keeps the #3/#38 single-run-loop invariant
  intact — exactly how Help already stacks over the detail.
- On `main` the Quick Updates screen applies **status only**: `Enter` sets
  `QuickUpdatesScreen.Chosen` and closes; the host's `onClosed` reads `Chosen` and applies
  it via the optimistic `ApplyStatus`. (Priority/assignee apply is #157/#158, in flight as
  PR #207 — out of scope here.)
- `ApplyStatus(TaskItem, status)` updates `_all` + the list row in place and does the
  off-thread write with revert-on-failure. It is a **no-op on the row** when the task isn't
  in `_all` (e.g. a feed-opened task), so it is safe to reuse for the detail origin.
- Detail Ctrl-chords already used: `Ctrl+B`/`Ctrl+A`/`Ctrl+R`/`Ctrl+PgUp`/`Ctrl+PgDn`.
  `Ctrl+U` is **free** and mnemonic (qUick Updates / "Update").
- `TaskDetail` and `TaskItem` differ in shape: `TaskDetail.Assignees` is `string[]`
  (names) and it has **no `PriorityLevel`/`StatusType`**. Quick Updates needs a `TaskItem`.

## Approach

Navigation-only. No changes to `QuickUpdatesScreen` (keeps PR #207's surface conflict-free
where possible).

### Phase 1 — testable projection + unit tests (test-first)

- New pure helper `Services/TaskItemProjection.cs`: `TaskItem FromDetail(TaskDetail)` —
  projects a detail into a `TaskItem` for the Quick Updates constructor, deriving
  `PriorityLevel`/`PriorityName` from the detail's priority **name** via `ClickUpPriority`,
  carrying status/list/dates, and mapping assignee **names** to `TaskAssignee(0, name)`
  (ids are unknown from a detail; the on-`main` assignee pane is display-only).
- `tests/TaskItemProjectionTests.cs`: id/name/url/list/status/dates carried; priority
  name->level+name (Urgent/High/Normal/Low, and null/unrecognised -> null); assignee names
  mapped; empty assignees.

### Phase 2 — detail launch + stacking + reflection

- `TaskDetailScreen`:
  - `public event EventHandler? QuickUpdatesRequested;`
  - `OnKey`: `Ctrl+U` (guarded `!_promptBox.Visible`, mirroring `Ctrl+A`) -> raise the event.
  - `public void ApplyOptimisticStatus(string statusName, string? statusColor)` ->
    `UpdateData(_task with { StatusName = statusName, StatusColor = statusColor }, _comments)`
    so the popped-back detail shows the new status immediately; the async write + 30s
    auto-refresh reconcile the authoritative value.
  - `HelpItemSets.Detail`: add `new("Ctrl+U", "quick update")`.
- `TodoApp`:
  - Wire `screen.QuickUpdatesRequested += (_, _) => OpenQuickUpdatesForDetail(screen, detail);`
    in `OpenTaskDetail`.
  - `OpenQuickUpdatesForDetail(TaskDetailScreen detailScreen, TaskDetail detail)`: resolve
    the `TaskItem` as `_all.FirstOrDefault(id) ?? TaskItemProjection.FromDetail(detail)`;
    flash + bail when it has no list; then load statuses (cached fast-path or off-thread)
    and call the shared show path with the detail origin.
  - Give `ShowQuickUpdates` an optional `TaskDetailScreen? detailOrigin = null`. Guard:
    list origin requires `ActiveScreen is null` (unchanged); detail origin requires
    `ReferenceEquals(ActiveScreen, detailOrigin)`. In `onClosed`, apply status via
    `ApplyStatus` (both origins) and, for the detail origin with a chosen status, call
    `detailOrigin.ApplyOptimisticStatus(chosen, statusColorForChosen)` before teardown.

### Phase 3 — validate

- `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format`.
- `tui-validate`: drive list->Space->Esc (lands on list, same task) and
  detail->Ctrl+U->pick status->Esc (lands on detail, status updated); confirm latency +
  A/B cell signatures vs stock (no rendering regression).

## Invariants preserved

- No second **focusable** pane and no nested run-loop (#3/#38): the stack toggles
  visibility; one screen focused at a time.
- No bare-letter keybinding (#12): the launch key is the `Ctrl+U` chord.
- No curated-spec / Kiota / `Generated/` changes; personal-token raw `Authorization`
  header untouched; integration tests stay `SkippableFact`.

## Deferred / coordination

- Priority/assignee **apply** (and reflecting those onto the detail) is #157/#158 (PR
  #207). When #207 lands, its keep-open-on-commit model should extend the detail-origin
  reflection beyond status. Noted on the PR.
- Updates on tasks not assigned to the user is #160 (separate). This PR does not change the
  list's context-parent/foreign-subtask guards.
