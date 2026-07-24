# Multi-tab nudge channel — consumer (#295)

Part of the multi-tab epic (#292). Consumer half of nudge-then-fetch; the
producer/writer half (#294) is merged. Makes a running instance notice another
instance's confirmed edit and refresh **just that task** — a per-task fetch, not
a full working-set resync, and never a self-echo.

## Background (what already exists on `main`)

- **Producer (#294):** `IChangeMarkerStore` (`InstanceId`, `Record(...)`,
  `ReadAll()`), backed by `LiteDbChangeMarkerStore` over a `changes` collection
  in the shared `state.db`. Each marker is keyed by task (upsert; a re-edit
  supersedes with a higher `Seq`), carries the writer's `InstanceId`, an
  optional server-confirmed `ServerDateUpdatedMs`, and a monotonic `Seq`.
  `ReadAll()` returns markers ordered by `Seq` ascending; it never throws.
- `Program.cs` already builds a per-process `instanceId` and a
  `LiteDbChangeMarkerStore`, and hands it to the `ClickUpClient` which records a
  marker after every confirmed write. The store is **not** yet handed to
  `TodoApp` — nothing reads the markers today (`state.db` is read once at
  startup only).
- **Reconcile primitives already on `main`:**
  - `TaskService.GetTaskDetailAsync(id)` → `TaskDetail` (single-task fetch).
  - `TaskItemProjection.FromDetail(detail)` → `TaskItem` (list-row shape).
  - `TodoApp.UpdateTaskRow(TaskItem, sending:false)` — folds status/priority/
    assignees into `_all` + the visible row **in place** (no `SetSource`, cursor
    and scroll preserved). This is the per-task list reconcile.
  - `TodoApp.RefreshDetail(screen, id)` — off-thread re-fetch of an open
    `TaskDetailScreen`'s detail+comments, reconciled in place.
  - `TaskDetailScreen.Task` exposes the open task (`.Id`, `.UpdatedMs`).

## Design

### 1. Pure consumer service — `Services/ChangeMarkerConsumer.cs`

A cursor, **not a list** (per the issue): per-process state is a single
monotonic `long _cursor` plus this process's `InstanceId`. A monotonic cursor
subsumes per-item tracking; a re-edited task upserts with a higher `Seq` above
the cursor and is naturally re-picked-up, so there is no in-memory change-list
to age out. Kept pure and store-free (markers passed in) so it is trivially
unit-testable and its two callers (startup init, periodic scan) share one core.

```csharp
public sealed class ChangeMarkerConsumer(string instanceId)
{
    public long Cursor { get; }

    // Fresh-tab init (edge case 1): set the cursor to the current max Seq so a
    // freshly launched tab — which just did a full load and already has
    // everything — does NOT replay the whole changes table as "new".
    void Initialize(IReadOnlyList<ChangeMarker> markers);

    // The scan: for every marker with Seq > cursor (ascending), advance the
    // cursor past it, then decide whether to fetch its task:
    //   - skip our own InstanceId (self-echo)                 — cursor still advances
    //   - skip a task not in view (edge case 2)               — cursor still advances
    //   - suppress when a held version is already >= the
    //     marker's ServerDateUpdatedMs (redundant fetch)      — cursor still advances
    //   - otherwise emit the task id (coalesced, first-seen order)
    // Returns the distinct task ids to fetch; mutates the cursor.
    IReadOnlyList<string> Advance(
        IReadOnlyList<ChangeMarker> markers,
        Func<string, bool> isInView,
        Func<string, long?> heldVersion);
}
```

Notes on the suppression rule: only suppress when the marker carries a server
time **and** the held version is `>=` it. A null `ServerDateUpdatedMs` (e.g. a
comment post, whose response carries no task time) always fetches — safe. A
task in view whose held version is unknown (null) always fetches.

### 2. Wiring into `TodoApp`

- Thread the `IChangeMarkerStore` in via a new **optional** constructor param
  (`changeMarkers = null` → `NullChangeMarkerStore.Instance`), so every other
  caller/test is unchanged. Build a `ChangeMarkerConsumer` from its `InstanceId`.
- In `Run`, **before** starting the refresh loop, `Initialize` the cursor from a
  one-time `ReadAll()` (edge case 1). Then arm a repeating **marker poll** via
  `Application.AddTimeout` — but only when the store is a real one
  (`InstanceId` non-empty); the `Null` store gets no timer.
- **Marker poll cadence — decoupled from the API poll.** A `changes` read is a
  cheap, bounded DB-only op, so the marker check runs on its own short cadence
  (`MarkerPollSeconds`, chosen = **4s**) independent of the 60s-default API poll,
  so cross-tab updates feel snappy while API fetches stay targeted. The
  `ReadAll()` runs **off** the UI thread (it briefly takes LiteDB's shared-mode
  cross-process lock), then the pure `Advance` + dispatch run **on** the UI
  thread (they read `_all` / the open detail). A single in-flight guard prevents
  ticks piling up.
- **In-view / held-version** are evaluated on the UI thread: a task is in view
  when it is in `_all`, in `_rows`, or is an open `TaskDetailScreen.Task.Id`;
  the held version is that surface's `UpdatedMs`.
- **Reconcile** for each surviving task id:
  - **list row** (if in `_all`/`_rows`): off-thread `GetTaskDetailAsync` →
    `FromDetail` → `UpdateTaskRow(.., sending:false)` on the UI thread. Per-task,
    in place — no full resync.
  - **open detail(s)** for the task: reuse `RefreshDetail(screen, id)`.
  Both paths are best-effort: a nudge fetch failure is swallowed (it rides on an
  edit that already succeeded elsewhere).
- Pass the store into `TodoApp` from `Program.cs`.

`SingleTaskApp` (the `--task` launch mode) is intentionally **out of scope** here
— it runs the minimal service graph and its own 30s detail auto-refresh already
gives cross-tab detail freshness. Noted as deferred.

## Testing

- **`ChangeMarkerConsumerTests` (unit, the bulk of the acceptance criteria):**
  cursor advancement (monotonic, past every scanned row); self-`InstanceId`
  filtering; fresh-tab init to max `Seq` (no historical replay); out-of-view
  markers advance the cursor without emitting; held-version `>=` suppression
  (and the null-server-time / null-held always-fetch cases); coalescing a
  re-picked-up (higher-`Seq`) task; empty-store init to 0; unordered-input
  defensiveness.
- TUI wiring can't run in CI — verified by build + reasoning; a two-instance
  `tui-validate` scenario (edit in instance A surfaces in instance B) is the
  end-to-end check, attempted after `dotnet test` is green. If the PTY harness
  can't cleanly drive two concurrent app instances against the fake backend in
  this session, the two-instance scenario is deferred to a tracked follow-up and
  the unit-tested consumer + wiring still ship.

## Out of scope / deferred (tracked)

- Nudge consumption in `SingleTaskApp` (`--task` launch mode).
- Any change to the producer (#294) or the marker schema.
</content>
</invoke>
