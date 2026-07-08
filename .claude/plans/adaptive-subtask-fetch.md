# Plan — Adaptive "smart" fetch strategy for foreign subtasks (#87)

## Goal

`TaskService.ResolveForeignSubtasksAsync` (#70) currently uses a **fixed per-parent** fetch:
one `GET /task/{id}?include_subtasks=true` per in-view task, recursing into pulled-in children.
That's minimal data volume but **N round-trips** for N in-view parents. #87 asks for a
**smart/adaptive** selector that picks the fetch shape from the situation (primarily the number of
in-view parents and how they cluster across lists), with the pure selection staying in
`ForeignDescendants` and only the *fetch source/shape* varying, plus bounded worst cases.

## The two no-regen strategies

Both feed the same pure `ForeignDescendants(snapshot, fetched)` selector — only the pool differs.
Neither touches `Generated/` or the curated spec (both endpoints already exist on the facade):

- **Per-parent** — `GetSubtasksAsync` (`GET /task/{id}?include_subtasks=true`) per in-view task,
  BFS-recursing into pulled-in children.
  - Round-trips ≈ **P** (in-view task count), + recursion when foreign children exist.
  - Bytes: minimal — only the relevant subtrees.
  - Correctness: **pulls cross-list subtasks** (a child moved to a different list is still found).
- **Whole-list** — `GetListTasksAsync` (`GET /list/{id}/task?subtasks=true`) per distinct in-view
  list, which already returns every task (any assignee, subtasks included).
  - Round-trips ≈ **L** (distinct list count), × paging for big lists.
  - Bytes: every task in each list (Σ Tₗ), most discarded.
  - Correctness gap: **misses a subtask relocated to a list no in-view task occupies** (same
    limitation the original #70 list-scoped plan already accepted). Grandchildren in the same list
    are included for free (the list fetch carries `subtasks=true`).

## Cost model & selection heuristic

Round-trip latency (~100–300 ms each) dominates perceived refresh cost on typical workspaces far
more than payload parse time, so the selector **minimises round-trips subject to a payload/clustering
guard**, defaulting to the safer per-parent strategy:

- Choose **whole-list** only when it *materially* cuts round-trips: `P ≥ MinParentsForWholeList`
  **and** `P ≥ L × ClusterRatio` (real clustering — several in-view tasks share a list).
- Otherwise **per-parent** (default; also the only correct choice for cross-list subtasks and for
  small fetches where P round-trips is already cheap).

Default tuning (in `SubtaskFetchTuning.Default`, all documented at the constant):

| Knob | Value | Why |
| --- | --- | --- |
| `MinParentsForWholeList` | 8 | Below ~8 roots, P round-trips is already cheap and per-parent's small payloads + cross-list correctness clearly win. |
| `ClusterRatio` | 3 | Whole-list only pays off at ≥3 in-view tasks per list; below that L ≈ P and it just adds payload for no round-trip win. |
| `MaxRoundTrips` | 200 | Hard ceiling on either strategy's fetches so a pathological workspace can't fan out unbounded. |

**No silent truncation:** when `MaxRoundTrips` caps the plan (or the per-parent BFS hits it mid-walk),
the fetch invokes an `onCapped` diagnostic the TUI surfaces via `Flash`, so a truncated pull is always
reported (issue requirement).

## Phases

### Phase 1 — Pure planner + tests
- `SubtaskFetchStrategy` enum (`PerParent` / `WholeList`), `SubtaskFetchTuning` record (+ `Default`),
  `SubtaskFetchPlan` record (strategy, capped id list, `Capped` flag).
- Pure `TaskService.ChooseSubtaskFetchStrategy(parentCount, listCount, tuning)` and
  `TaskService.PlanSubtaskFetch(snapshot, tuning)` (distinct roots / distinct non-blank lists,
  first-appearance order; applies the `MaxRoundTrips` cap).
- `SubtaskFetchPlanTests`: strategy boundaries (below/at `MinParentsForWholeList`; cluster ratio
  exactly met / just under; L==0 / P==0 degenerate → per-parent); plan id selection & dedup order;
  cap trips `Capped` and truncates for both strategies.

### Phase 2 — Fetch wiring
- `ResolveForeignSubtasksAsync(snapshot, ct, onCapped?)` builds the plan and dispatches:
  per-parent BFS (existing walk, now bounded by `MaxRoundTrips` with `onCapped` on hit) or whole-list
  (`GetListTasksAsync` per planned list, best-effort, union → `ForeignDescendants`). Both stay thin /
  best-effort like `ResolveContextParentsAsync` (no direct unit test — logic lives in the pure planner
  + `ForeignDescendants`, which are tested).
- `TodoApp.FetchAsync` passes an `onCapped` that flashes the truncation notice.

### Phase 3 — Finalize
- `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format`.
- PR body carries the written cost evaluation above.

## Non-goals / deferred (file follow-up issue, link from PR)
- **Bulk filtered team-tasks query scoped to parent ids** and **bounded-concurrency fan-out** — the
  bulk query needs a `parent`/`task_ids` filter the curated spec doesn't expose (spec + Kiota regen);
  concurrency is orthogonal. Track in a new follow-up issue.
- **Cross-refresh caching** of fetched subtrees — belongs to the persistent-cache epic (#118), not here.
- No F3/config UI for the tuning knobs — sensible unit-tested defaults; exposing them is out of scope.
