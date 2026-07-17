# Plan: Background-prefetch the completed (closed) task set (#253)

Follow-up to #191 (tri-state F12 "Show Completed"). Today reaching the **All** state
triggers an on-demand full `include_closed=true` refresh, so the first cycle to All
stalls until that fetch returns. This makes the transition feel instant by keeping a
warm, bounded **closed**-task set cached on a slower cadence and painting it the
moment F12 lands on All.

## Constraints resolved up front (the issue's "resolve first" list)

1. **Coordination with the refresh loop.** No second timer / no parallel loop. The
   prefetch rides the single refresh loop as a new `FetchCadenceGate` group
   ("closed-prefetch"), exactly like the #236 workspace-list walk (#246 ADR).
2. **Reconcile with the delta path.** The warm set is used **only as a one-time
   bridge paint** at the F12→All transition, never as an ongoing overlay. The
   authoritative on-demand `include_closed=true` refresh still fires and replaces the
   snapshot; it returns a *superset* of the warm closed set (same query, fresher), so
   no task flips present↔absent. The delta path (`MergeDelta`) is untouched — the
   prefetch never writes `_lastSnapshot` / `_watermarkMs`.
3. **Unboundedness.** The closed set is bounded by an **age window** (on
   `date_updated`, which closing bumps) **and a count cap** (newest-first), with a
   `Debug.WriteLine` / status-line note when truncated — never a silent cap.
4. **Cache invalidation / TTL.** In-memory, per-service (dies with the workspace/
   token like the other in-session caches); the age window *is* the staleness bound.
   Cross-restart persistence is out of scope (tracked as possible follow-up).

## Scope of this PR

Only the **closed** set is prefetched — per #191, `done`-type tasks return regardless
of `include_closed`, so they're already in every snapshot; only `closed`-type is
missing below All.

### Phase 1 — service + pure logic (unit-tested)
- `Services/ClosedTaskCache.cs`: thread-safe holder of a bounded closed-task list.
  - static pure `Bound(IEnumerable<TaskItem> closed, int maxCount, TimeSpan maxAge,
    DateTimeOffset now)` -> `(IReadOnlyList<TaskItem> Kept, int Dropped)`: drop tasks
    older than `maxAge` by `UpdatedMs` (null `UpdatedMs` never aged out), order newest
    first, then cap to `maxCount` counting the overflow as dropped.
  - `Update(IReadOnlyList<TaskItem>)` bounds + stores under a gate, returns `Dropped`.
  - `Snapshot` getter (thread-safe), `Count`.
- `TaskService`:
  - extract `FetchMergedAsync(bool includeClosed, ct)` from `LoadAsync` (the dual
    fetch + dedup + order) -- **without** the delta re-baseline, which stays in
    `LoadAsync`.
  - `PrefetchClosedTasksAsync(ct)`: `FetchMergedAsync(includeClosed:true)`, keep only
    `IsClosed`, `Update` the cache; returns the dropped count. Never touches delta
    state.
  - `WarmClosedTasks` getter; `SupplementWithClosed(IReadOnlyList<TaskItem> snapshot)`
    pure merge (snapshot wins id collisions) -> `TaskOrder`-sorted union.
- Tests: `ClosedTaskCacheTests.cs` (bounding: age drop, count cap, ordering, null
  `UpdatedMs`), `TaskServiceClosedPrefetchTests.cs` (prefetch fetches include_closed,
  keeps only closed-type, doesn't disturb the delta baseline; supplement merges/dedups).

### Phase 2 — TUI wiring (build-verified; manual steps in PR)
- New cadence group `closed-prefetch` with a `ClosedPrefetchMinAge` (~3 min). In
  `FetchAsync`, when `!_config.View.IncludesClosedTasks` **and** due (poll) -- or forced
  on Initial -- run `PrefetchClosedTasksAsync` best-effort in the existing `WhenAll`
  batch, `MarkRan` on completion (mirrors `RunWorkspaceListWalkStepAsync`). Skipped
  entirely in All (the live snapshot already carries closed tasks there).
- `CycleShowCompleted`: on transition to All, if `WarmClosedTasks` is non-empty,
  `_all = _tasks.SupplementWithClosed(_all)` and render **before** `RequestRefresh()`,
  so closed rows appear instantly; the authoritative refresh converges normally.
- Surface truncation on the status line (like the #87 "some subtasks omitted" note).

## Out of scope / deferred (linked follow-ups)
- Cross-restart persistence of the warm closed set (fold into the #124 store).
- Per-list scoping of the closed fetch for very large workspaces (count cap suffices
  now).
- A `tui-validate` scenario asserting the instant bridge paint (needs a fake-backend
  closed-set seed) -- file a follow-up if not covered here.

## Invariants
- Generated client / curated spec untouched (no new API surface -- `include_closed`
  already exists).
- No second focusable pane (#3); no bare-letter shortcuts (#12) -- no new keybinding,
  F12 already owns the cycle.
