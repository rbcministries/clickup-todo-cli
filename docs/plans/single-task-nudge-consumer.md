# Single-task launch mode — consume nudge markers (#377)

Part of the multi-tab epic (#292). The nudge-channel **consumer** (#295, merged)
wires the cross-process nudge-then-fetch scan into the dashboard (`TodoApp`)
only. The single-task launch mode (`SingleTaskApp`, from `--task` / #296) runs a
minimal service graph and its own 30s detail auto-refresh, and does **not** yet
consume nudge markers — so an edit to the launched task in another tab surfaces
only on that 30s tick, not promptly.

## Background (what already exists on `main`)

- **Producer (#294):** `IChangeMarkerStore` (`InstanceId`, `Record(...)`,
  `ReadAll()`), backed by `LiteDbChangeMarkerStore` over a `changes` collection
  in the shared `state.db`. `Program.cs` builds a per-process `instanceId`, a
  `LiteDbChangeMarkerStore`, and hands it to the `ClickUpClient`, which records a
  marker after every confirmed write. `ReadAll()` returns markers ordered by
  `Seq` ascending and never throws.
- **Consumer (#295):** `Services/ChangeMarkerConsumer.cs` — a **pure**, store-free
  cursor scan. `Initialize(markers)` seeds the cursor to the current max `Seq`
  (fresh-tab, no history replay); `Advance(markers, isInView, heldVersion)`
  returns the distinct task ids to re-fetch, skipping own-`InstanceId` writes,
  out-of-view tasks, and versions already held, and advancing the cursor past
  every marker seen. Already fully unit-tested (`ChangeMarkerConsumerTests`).
- **Dashboard wiring (#295):** `TodoApp.ArmMarkerPoll` seeds the cursor and
  arms an `Application.AddTimeout` on `MarkerPollInterval = 4s` (decoupled from
  the 60s API poll). `PollMarkers` reads markers off the UI thread, then on the
  UI thread runs `Advance` with `IsNudgeTaskInView` / `HeldNudgeVersion` and
  reconciles each id. A no-op store (`NullChangeMarkerStore`) has an empty
  `InstanceId`, so `ArmMarkerPoll` returns early — nothing is armed.
- **`SingleTaskApp` today:** holds exactly one task (`_taskId`, `_task`), owns a
  `RefreshTask()` that re-fetches that task's detail + comments off-thread and
  feeds them back via `_detail.UpdateData`, self-gated by `_refreshing` (in
  flight) and "detail is front-most". `TaskDetailScreen`'s own 30s tick already
  drives `RefreshTask` via `RefreshRequested`. `SingleTaskApp` is **not** handed
  the `IChangeMarkerStore` today.

## Design

The single-task tab is the trivial case of the dashboard's consumer: it holds
exactly one task, so "in view" collapses to "is the launch task" and "held
version" is that one task's `date_updated`. Reuse the merged pure
`ChangeMarkerConsumer` unchanged; add the single-task view predicates and mirror
the dashboard's arm/poll wiring onto `SingleTaskApp`'s existing per-task refresh.

### 1. Pure view policy — `Services/SingleTaskNudgePolicy.cs` (new)

A tiny pure helper so the net-new decision is unit-testable in CI without a
Terminal.Gui driver (the same reason `ChangeMarkerConsumer` is pure):

- `IsInView(id)` → `id == launchTaskId`.
- `HeldVersion(id)` → the launch task's **current** `date_updated` (via an
  injected `Func<long?>` the host updates on each refresh) for the launch task,
  else `null` (unknown → never version-suppressed) for any other id.

### 2. Thread the store into `SingleTaskApp`

- New optional ctor param `IChangeMarkerStore? changeMarkers = null`, defaulting
  to `NullChangeMarkerStore.Instance` (so existing callers/tests are unchanged).
- Construct `_markerConsumer = new ChangeMarkerConsumer(_changeMarkers.InstanceId)`
  and `_nudgePolicy = new SingleTaskNudgePolicy(_taskId, () => _task.UpdatedMs)`.
- `Program.cs` passes the real `changeMarkers` store into `new SingleTaskApp(...)`.

### 3. Arm + poll (mirrors `TodoApp`)

- `ArmMarkerPoll()`: return early on an empty `InstanceId`; else
  `_markerConsumer.Initialize(_changeMarkers.ReadAll())` (fresh-tab cursor init,
  edge case 1) and `Application.AddTimeout(MarkerPollInterval /* 4s */, …)`.
  Called from `Run()` after `Build()`, before the run loop pumps.
- `PollMarkers()`: `_pollingMarkers` in-flight guard; read markers off the UI
  thread (`ReadAll` briefly takes LiteDB's shared lock), then on the UI thread
  run `_markerConsumer.Advance(markers, _nudgePolicy.IsInView,
  _nudgePolicy.HeldVersion)` and call `RefreshTask()` for any surviving id.
  Best-effort — a read failure is swallowed (the nudge rides on an edit that
  already succeeded elsewhere).
- Reconcile reuses the existing `RefreshTask()`: the launch task is the only id
  the scan can surface, and `RefreshTask` already self-gates on in-flight and
  front-most, and folds the result back via `_detail.UpdateData`. No new
  reconcile path, no full resync, no self-echo (own markers are filtered by the
  consumer's `InstanceId` match).

## Acceptance criteria (from #377 / mirrored from #295)

- An edit to the launched task in another tab surfaces in the single-task detail
  within the marker-check window (4s) via a **per-task** re-fetch, not only on
  the 30s auto-refresh, and never a self-echo.
- A freshly launched single-task tab does not replay historical markers (cursor
  initialised to max `Seq`).
- A held-version-≥-marker case suppresses the redundant fetch; a comment marker
  (no server time) always fetches.
- The no-op store disarms the poll (empty `InstanceId`) — no behaviour change
  when the cross-process channel is absent.

## Tests

- `SingleTaskNudgePolicyTests` (new, pure/CI): `IsInView` true only for the
  launch task; `HeldVersion` returns the live supplier value for the launch task
  and `null` for any other id; reflects a version change after a simulated
  refresh.
- Consumer-integration through the policy (new, pure/CI): drive
  `ChangeMarkerConsumer.Advance` with the `SingleTaskNudgePolicy` predicates and
  markers for the launch task + a foreign task + an own-`InstanceId` marker —
  assert only the launch task is fetched, the foreign/own markers are skipped
  (cursor still advances), and a server-time marker at/below the held version is
  suppressed. This pins the exact wiring the poll uses.
- The arm/poll timer and `RefreshTask` reconcile are Terminal.Gui host code (not
  CI-unit-testable per `CLAUDE.md`); the wiring mirrors the proven `TodoApp`
  path exactly. Verified by building and by reasoning; manual verification in the
  PR (two `--task` tabs on a shared `state.db`; edit in one surfaces in the other
  within ~4s).

## Not in scope / notes

- No rendering, keypress, list-source, or driver change — the tab's visuals and
  input model (single sectioned `ListView`, #3/#38) are untouched — so
  `tui-validate` is not required for this slice (same rationale as #420).
- Quick Updates in single-task mode stays deferred (#297), unchanged here.
