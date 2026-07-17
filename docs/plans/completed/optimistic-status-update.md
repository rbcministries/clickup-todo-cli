# Plan — Optimistic in-place status update (issue #11)

## Goal
When a task's status is set via the Space → StatusPicker flow, reflect the change
immediately and reliably without a jarring full-list reload and without depending on
ClickUp read-after-write consistency.

## Current behavior
- `ClickUpClient.SetTaskStatusAsync` issues `PUT /task/{id}` and **discards** the
  returned updated task (returns `Task`).
- `TodoApp.ApplyStatus` calls `SetStatusAsync` then `_refresh.RequestRefresh()` — a
  full re-fetch + re-render. The status column can lag if the API read hasn't caught
  up to the write, and the whole list rebuilds (SetSource) which resets the cursor.

## Acceptance criteria (from issue)
1. **Optimistic UI:** on Space-select, immediately render the row as `{newStatus} (sending…)`.
2. **In-place single-row update** rather than reloading the whole list.
3. **Use the write response:** `PUT /task/{id}` returns the updated task; return the
   confirmed status from the response and display it on success; on failure, revert
   the row and show an error.

## Design

### Phase 1 — client + service (testable core)
- `ClickUpClient.SetTaskStatusAsync` → return `Task<string?>`: the **confirmed**
  status name from the `PutAsync` response (`updated?.Status?.StatusProp`), or null
  if absent. No spec/regen needed — the 200 is already typed as `TaskObject`.
- `TaskService.SetStatusAsync` → return `Task<string?>` (propagate confirmed status).
- New pure helper `TaskService.ApplyStatusChange(IReadOnlyList<TaskItem>, taskId, newStatus)`
  returning a new list with the matching task's `StatusName` replaced (record `with`).
  This is the "update one record in place" logic, pure and unit-testable.

### Phase 2 — TUI wiring (single-row, optimistic)
- Keep a reference to the ListView's backing `ObservableCollection<string>` as a field
  (`_display`) so a single row can be replaced without `SetSource` (no full redraw /
  cursor reset — respects the single-ListView model, no new pane).
- `UpdateTaskRow(TaskItem updated, bool sending)`:
  - replace the task in `_all` via `ApplyStatusChange` (canonical snapshot stays
    consistent so the periodic background refresh reconciles **silently** — same
    status → same signature → no redraw);
  - re-sync `_signature` to the updated `_all`;
  - replace `_rows[i]` and `_display[i]` for the one row (append `  (sending…)` when
    `sending`).
- `ApplyStatus(task, status)`:
  1. optimistic: `UpdateTaskRow(task with { StatusName = status }, sending: true)` + flash;
  2. await `SetStatusAsync`; on success `UpdateTaskRow(task with { StatusName = confirmed ?? status }, sending: false)` + flash confirmed;
  3. on failure: `UpdateTaskRow(task, sending: false)` (revert to original) + error flash.
- Remove the `_refresh.RequestRefresh()` from the success path (no full reload).

### Phase 3 — tests
- Unit tests for `ApplyStatusChange`: replaces matching id, leaves others, no-match is a
  no-op, returns a new list (immutability of input), preserves order.
- Extend integration tests (`SkippableFact`, env-gated on `CLICKUP_TOKEN` +
  `CLICKUP_LIST_ID`): set a status round-trip and assert the confirmed status is
  returned from the write response (restore original status afterward).

## Out of scope / deferred
- Per-substring status coloring (#16), task detail view (#17) — unrelated.

## Verification
- `dotnet build -c Release` (0 warn/0 err), `dotnet test -c Release` (integration skips
  without creds), `dotnet format`.
- TUI not unit-testable in CI: verify by build + reasoning; manual steps documented in PR.
