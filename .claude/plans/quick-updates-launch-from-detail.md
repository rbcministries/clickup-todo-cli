# Quick Updates: launchable from Task Detail — return to origin on exit (#159)

Part of Epic #153 (Quick Updates screen). Sub-issue **F**. Depends only on the
Quick Updates screen **shell (C, #156)**, which is merged on `main`. The three
panes (D #157 / E #158) "behave identically regardless of origin" — this slice
is **navigation + origin-return only** and does not wait on the open pane PRs
(#207/#158).

## Goal

Quick Updates must be reachable from two origins, and `Esc` must return to
whichever origin opened it:

- From the **main list** (Space) → `Esc` returns to the list with the affected
  task still selected (today's behaviour — unchanged).
- From the **Task Detail view** (`TaskDetailScreen`) → `Esc` returns to that
  task's **detail view** (not the list), showing any value just changed.

## Verified current state

- `TodoApp` holds screens on a stack (`_screens`; `ActiveScreen => _screens[^1]`).
  `ShowScreen`/`CloseScreen` already implement a **general stack** — on close they
  restore the layer beneath (screen-below or the list). Only the *callers* guard
  on `ActiveScreen is null`, so today only Help stacks over another screen —
  except `OpenTaskDetail`, which already stacks the detail over the **feed**
  (#115) by capturing the requesting layer and mounting only if it's still active.
- `OpenQuickUpdates()` (Space) reads `CurrentTask()`, guards context-parent /
  foreign-subtask / no-list cases, warms statuses (cached fast-path or off-thread
  cold fetch), then `ShowQuickUpdates(task, statuses)` — which bails if
  `ActiveScreen is not null` and, on close, applies the chosen status via
  `ApplyStatus` (optimistic row update + off-thread write + revert-on-failure).
- `QuickUpdatesScreen` is **origin-agnostic** already: exposes `Chosen` (status
  name) and closes on `Esc`. No change needed to the screen itself.
- `TaskDetailScreen.OnKey` uses Ctrl-chords (Ctrl+A dispatch, Ctrl+B browser,
  Ctrl+R/F5 refresh, Ctrl+PgUp/PgDn order) + Tab/F1/Esc. `Space` is free (bare
  letters are reserved; #12). `RefreshDetail(screen, taskId)` already re-fetches a
  detail screen's data in place and no-ops if it isn't front-most/mounted.

## Design

Because the stack already auto-restores the layer beneath on close,
**return-to-origin comes for free** once Quick Updates is allowed to stack over
the detail. Three focused changes:

### 1. Launch affordance from the detail view — `Ctrl+U`

`TaskDetailScreen` gains a `QuickUpdatesRequested` event, raised on **Ctrl+U**
(mnemonic "Quick **U**pdates"; free, matches the command model, inert while the
Dispatch pane is open — mirrors the `Ctrl+A` `!_promptBox.Visible` guard). The
detail exposes its current `TaskDetail` (`Task` getter) so the host operates on
the value reflecting any in-place refresh. `HelpItemSets.Detail` gains
`Ctrl+U Quick updates` (before the Esc item); the F1 `HelpScreen` text mentions it.

### 2. Stack Quick Updates over the detail

`OpenQuickUpdates()` (list) and a new detail-origin path both funnel through
`OpenQuickUpdatesFor(TaskItem task, TaskDetailScreen? detailOrigin)`.
`ShowQuickUpdates(task, statuses, detailOrigin)` gates the open by origin:

- `detailOrigin is null` (list): open only when `ActiveScreen is null` (today's
  guard — unchanged).
- `detailOrigin is not null` (detail): open only when
  `ReferenceEquals(ActiveScreen, detailOrigin)` (mirrors `OpenTaskDetail`'s
  requester check) so a second Ctrl+U or a torn-down screen can't double-stack.

The host resolves the detail's `TaskItem` by preferring the fresh snapshot row in
`_all` (real assignee ids etc.); when absent (e.g. a detail opened from the feed,
#115), it builds a synthetic item from the `TaskDetail` via a pure, tested
`QuickUpdatesModel.TaskItemFromDetail`.

### 3. Reflect the edit on both surfaces

`ApplyStatus(task, status, detailOrigin = null)` keeps its optimistic list-row
update + off-thread write. On the **server-confirmed** continuation, if the edit
was launched from a detail that is still mounted, it calls
`RefreshDetail(detailOrigin, task.Id)` — sequenced strictly *after* the confirmed
write so the re-fetch can't race ahead of it. The list row stays reconciled by the
existing `UpdateTaskRow` path regardless of origin. (When launched from the list,
`detailOrigin` is null → behaviour is byte-identical to today.)

## Invariants preserved

- **No second focusable pane (#3/#38):** one visible screen at a time; the
  `Application.Run` loop is untouched. Quick Updates stacks over the detail via the
  existing seam, exactly like Help/feed already do.
- **No bare-letter keybinding (#12):** the launch key is the `Ctrl+U` chord.
- **No curated-spec / Kiota / `Generated/` changes** — pure TUI navigation +
  wiring. Personal-token raw `Authorization` header untouched; no new API surface.

## Tests

- `QuickUpdatesModelTests`: `TaskItemFromDetail` maps id/name/status/list, derives
  `PriorityLevel` from the priority name, and carries assignee names (fallback ids)
  — with the no-priority / no-assignee cases.
- `HelpLineTests`: `HelpItemSets.Detail` contains `Ctrl+U Quick updates`, still
  ends with `Esc`, still offers `F1`.
- The navigation/stacking itself is Terminal.Gui glue (CI-untestable per
  CLAUDE.md) — verified via build + the `tui-validate` PTY harness: open a task's
  detail (Enter), press Ctrl+U to stack Quick Updates, set a status (Enter),
  confirm Esc returns to the **detail** (not the list) with the new value, and
  that the list-origin Space→Esc round-trip still lands on the list with the task
  selected. Plus the standard color/latency A/B against the stock renderer.

## Deferred / out of scope

- Priority/assignee **apply** on both surfaces rides on #157 (PR #207) / #158;
  this slice reflects the Status edit (the only pane that applies on `main`) and
  is forward-compatible (the detail refresh re-reads all attributes).
- Updates on tasks not assigned to the current user → #160.
