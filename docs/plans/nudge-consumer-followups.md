# Plan — #376: Nudge consumer (#295) follow-ups

Two items were scoped out of the nudge-**consumer** PR (#295/#375) and tracked on #376:

1. **Two-instance `tui-validate` end-to-end** — a real cross-tab propagation assert (two PTY app
   processes sharing an on-disk `state.db`).
2. **Full-fidelity nudged list-row reconcile** — replace the lossy status+priority overlay with a
   wholesale per-task `TaskItem` fetch now that `IClickUpClient.GetTaskItemAsync` (#291/#373) is on
   `main`.

Both are enhancements to an already-working, unit-tested consumer — not correctness bugs.

## Delivery status / sequencing

- **Phase A (item 2 — full-fidelity reconcile): this session.** A small, fully **CI-verifiable**
  slice: two pure, unit-tested helpers plus the `RefreshNudgedRow` rewiring. No TUI surface changes
  (the row render path is unchanged), so no `tui-validate` is needed for it.
- **Phase B (item 1 — two-instance E2E): attempted after A; may defer.** The PTY harness
  (`tests/ClickUpTodo.Tui.E2E/`) boots a **single** app instance against an in-process fake
  `HttpMessageHandler` and constructs `TodoApp` with the **Null** change-marker store (the marker
  poll is disarmed there). A genuine cross-tab scenario needs two PTY-driven processes sharing a
  real on-disk `LiteDbChangeMarkerStore` wired to **both** the producer (`ClickUpClient`) and the
  consumer (`TodoApp`) — a substantial harness extension (cross-process LiteDB, two processes,
  timing). If it can't land cleanly and green this session it is split into its own follow-up issue,
  linked from the PR; Phase A ships regardless.

## Phase A — full-fidelity nudged list-row reconcile (item 2)

### The gap today

`TodoApp.RefreshNudgedRow` fetches a `TaskDetail` and overlays **only status + priority** onto the
live `_all` row (via `existing with { … }`), because a `TaskDetail` is **lossy** relative to a
`TaskItem` — its assignees are name-only (`TaskAssignee(0, name)` — see `TaskItemProjection.FromDetail`)
and it has no `ParentId`/`StatusType`. So a cross-tab **assignee-by-id / name / due-date** change
isn't reflected by the nudge itself; it surfaces on the next authoritative delta poll.

### The fix

`IClickUpClient.GetTaskItemAsync` (#291/#373, now on `main`) returns a **full** `TaskItem` via the
same `Map` the list rows use — real structured assignees (with ids), `ParentId`, `StatusType`,
`ListName`, colours. `RefreshNudgedRow` fetches that instead and applies it **wholesale**, so the
row reflects every field a cross-tab edit could have changed.

### Testable pieces (pure — mirror the existing `ApplyStatusChange`/`ApplyFieldChanges` unit tests)

1. **`TaskService.GetTaskItemAsync` passthrough** — the single-task facade read the nudge path
   fetches through (sibling of `GetTaskDetailAsync`). `TaskService` already calls
   `client.GetTaskItemAsync` internally (Task Tree, #291); this exposes it to the host.

2. **`TaskService.ReplaceTaskItem(tasks, fresh)`** — a new pure snapshot helper: returns a new list
   with the matching-id task **replaced wholesale** by `fresh`, order and count preserved, input not
   mutated, a no-op when the id is absent (the row may live only in the context rows, not `_all`).
   The full-fidelity sibling of the per-field `ApplyFieldChanges` (which folds only
   status/priority/assignees). `ApplyFieldChanges` stays as-is for the Quick Updates commit path
   (that record only carries the changed field's new value); the nudge path uses the authoritative
   wholesale replace.

3. **`NudgedRowReconciler.Reconcile(existing, fresh)` → `TaskItem?`** — the pure ordering decision
   currently inlined in `RefreshNudgedRow`, extracted so it's unit-testable:
   - Drop a **stale out-of-order** fetch: when both carry `UpdatedMs` and `fresh < existing`, return
     `null` (an older in-flight fetch resolving last must not clobber a newer version already on the
     row — the row-path analogue of the detail path's `_refreshingDetail` guard). Missing a version
     on either side ⇒ can't order ⇒ apply (best-effort).
   - Never let the row's activity stamp **regress to null**: if `fresh.UpdatedMs` is null, carry
     `existing.UpdatedMs` forward on the returned record so a later stale fetch can still be ordered
     out (preserves today's `UpdatedMs = fresh.UpdatedMs ?? existing.UpdatedMs`).
   - Otherwise return `fresh` unchanged (wholesale).

### Wiring (TUI — no visible/behavioural change beyond fuller fidelity)

- `UpdateTaskRow(TaskItem updated, bool sending, bool wholesale = false)` — the only difference for
  the nudge path is folding `_all` via `ReplaceTaskItem` instead of `ApplyFieldChanges`; the rest of
  the row-render bookkeeping (badges, marker spans, fold marker, not-mine classification) is shared
  unchanged. Quick Updates keeps the default `wholesale: false`.
- `RefreshNudgedRow` becomes: fetch `GetTaskItemAsync`; on the UI thread find the `existing` row,
  run `NudgedRowReconciler.Reconcile(existing, fresh)`, and — when non-null —
  `UpdateTaskRow(reconciled, sending: false, wholesale: true)`. Still off-thread, still best-effort
  (a fetch failure is swallowed — the edit already succeeded in the other instance), still
  re-checks membership on the way back in.

### Tests (xUnit, `dotnet test` green)

- `ReplaceTaskItemTests` — replaces only the matching id; preserves order/count; input not mutated;
  no-match returns an equivalent snapshot; a wholesale replace carries a **new** assignee-id set /
  `ParentId` / `DueDateMs` (the fields the old overlay dropped).
- `NudgedRowReconcilerTests` — stale (`fresh<existing`) ⇒ null; newer ⇒ fresh; equal ⇒ apply;
  either version missing ⇒ apply; `fresh.UpdatedMs` null ⇒ result carries `existing.UpdatedMs`.

## Phase B — two-instance `tui-validate` end-to-end (item 1)

Extend `tests/ClickUpTodo.Tui.E2E/` so a driver can boot **two** app processes against the fake
backend, both wired to a shared on-disk `LiteDbChangeMarkerStore`:

- The producer instance's `ClickUpClient` writes a change marker on a Quick Update (status/priority);
  the consumer instance's `TodoApp` marker poll (armed with the real store, not the Null store)
  picks it up and reconciles the row within the poll window (~4s), via a per-task fetch — no
  self-echo, no full resync.
- Drive: Quick Update in instance A → assert instance B's list row (and open detail) reflects it.

This is the harness extension #295's acceptance criteria named. It touches only test/harness code
and is gated behind an env knob so it doesn't perturb the existing single-instance scenarios. If it
proves too large/timing-flaky for a clean green landing this session, it is deferred to a dedicated
follow-up issue (linked from the PR) rather than shipped half-done.

### Phase B — as built (#376 item 1)

The whole extension is **additive and env-gated**, so every single-instance scenario and CI (which
never runs the PTY harness) is untouched. Both `TodoApp` and `ClickUpClient` already accept an optional
`IChangeMarkerStore`, so no production code changed — only `Program.cs`, `FakeClickUp`, and a new driver.

Env knobs (a `_2`-suffixed process id keeps the two apps' markers distinguishable):

- **`E2E_MARKER_DB=<path>`** — when set, `Program.cs` opens a shared `LiteDbStateStore(path)` (LiteDB
  *shared* connection = cross-process mutex) and wires `CreateChangeMarkerStore(instanceId)` into **both**
  the producer `ClickUpClient` and the consumer `TodoApp`. Absent ⇒ both null ⇒ the facade's Null store
  and a disarmed marker poll, exactly as before.
- **`E2E_INSTANCE_ID=<id>`** — the per-process marker instance id (defaults to a random id). The two
  processes get distinct ids so the consumer skips its own writes by id (`ChangeMarkerConsumer`).
- **`E2E_NUDGE=1` + `E2E_SHARED_STATE=<path>`** — `FakeClickUp` keeps a tiny **cross-process task-status
  overlay** in a shared JSON file (read-modify-write with an atomic POSIX rename). A status PUT persists
  the committed status there and **bumps `date_updated` past the seed** (`1700000000000` →
  `1800000000000`) so the marker it records is strictly newer than the version the other instance holds —
  otherwise the consumer's redundant-fetch guard (`held >= server`) would suppress the fetch. Every GET
  (`/task/{id}` and the team list) reflects the overlay, so the reader's nudge re-fetch — and any later
  resync — sees the committed status. No sockets: it stays an in-process `HttpMessageHandler`, just
  file-backed for the one mutated field.

Driver: `nudge_two_instance_check.py` boots two PTY app processes sharing one `E2E_MARKER_DB` +
`E2E_SHARED_STATE` (distinct instance ids), waits for both to render, commits a status change on `t0` in
instance A (`Ctrl+U` → Status pane `Down` → `Enter`, per `foreign_quickupdates_check.py`), and polls
instance B's list row until its `t0` status chip flips from the seed (`(TD)` "to do") to the committed
value (`(IP)` "in progress") within a generous multiple of the ~4s marker-poll window — proving
cross-process nudge-then-fetch end-to-end. A control assertion confirms B's own row was `(TD)` before the
edit, so the flip is the nudge, not a coincidence.

## Hard rules honored

- **No `Generated/` hand-edit, no curated-spec change / no regen** — `GetTaskItemAsync` already
  exists on the facade; this only adds a passthrough + pure helpers + reconcile rewiring.
- **No second focusable pane (#3/#38)** — the row render path is untouched; the reconcile is a
  background fetch folded onto the existing single sectioned `ListView`.
- **Bare letters reserved for type-ahead (#12)** — no keybindings touched.
- Integration/E2E stay env-gated (`SkippableFact` / `tui-validate` harness); the reconcile logic is
  carried by pure unit tests. Personal-token raw `Authorization` header untouched.

## Sources (repo)

- `src/ClickUpTodo/Tui/TodoApp.cs` — `RefreshNudgedRow` / `ReconcileNudgedTask` / `UpdateTaskRow`.
- `src/ClickUpTodo/Services/TaskService.cs` — `ApplyStatusChange` / `ApplyFieldChanges` (the pure
  sibling helpers this plan extends) and the internal `GetTaskItemAsync` use in the Task Tree walk.
- `src/ClickUpTodo/ClickUp/ClickUpClient.cs` — `GetTaskItemAsync` → `Map` (full `TaskItem`).
- `tests/ClickUpTodo.Tui.E2E/Program.cs` — the single-instance PTY harness Phase B extends.
