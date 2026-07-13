# Foreign-subtask fetch: bounded-concurrency fan-out (#144, Part 2)

Follow-up to #87. Issue #144 bundles two deferred optimizations for
`TaskService.ResolveForeignSubtasksAsync`:

1. **Bulk parent-scoped team-tasks query** — needs a spec edit + Kiota regen.
2. **Bounded-concurrency fan-out** for the per-parent BFS — pure logic, no regen.

This slice delivers **Part 2**. Part 1 is **deferred** (see "Part 1 feasibility"
below) and stays tracked by #144.

## Background

`ResolveForeignSubtasksAsync` resolves teammate-owned subtasks of in-view
parents (#70) via the adaptive plan from #87 (`SubtaskFetchStrategy.Plan`):
some lists are pulled whole, the sparse remainder is fetched per-parent. The
per-parent branch is a **BFS**: seed from `plan.PerParentIds`, call
`GetSubtasksAsync(id)` per parent, and recurse into pulled-in children (their
own subtasks may be foreign too). A total-round-trip budget
(`MaxPerParentFetches`) bounds the whole BFS and flags `Truncated` when hit.

Today that BFS is **sequential** — one `await client.GetSubtasksAsync(id)` at a
time — so its wall-clock scales linearly with the number of foreign parents
(and depth). The sibling fan-outs `ResolveContextParentsAsync` and
`ResolveListColorsAsync` already run bounded-concurrent via
`Parallel.ForEachAsync(MaxDegreeOfParallelism = MaxFanOutConcurrency)` (#192).
This brings the per-parent branch to parity.

## Design — level-synchronized bounded-concurrency BFS

Keep the exact fetched-set, dedup, budget, and truncation semantics; only change
*how many* round-trips run at once. The current FIFO queue already processes the
tree in **level order** (all seeds first, then their children), so a
level-synchronized BFS is behaviourally equivalent in *which* ids are fetched
and *in what round-trip order across levels* — it only overlaps the calls
*within* a level.

Per level:
1. Drop ids already expanded (a child a whole-list fetch already pooled, or a
   duplicate) — no budget spent on them, matching today's `expanded.Add` guard.
2. Apply the budget: fetch at most `MaxPerParentFetches - spent` ids this level,
   in stable order; if that truncates the level, set `Truncated` (the dropped
   ids are pending work we won't reach — exactly today's `spent >= budget` break).
3. Fetch the level concurrently, bounded by `MaxFanOutConcurrency` (= 4, the
   existing shared cap — polite to ClickUp's per-token rate limit even when the
   three refresh fan-outs overlap; a process-wide budget is #193).
4. Merge children **single-threaded, in level order** into the shared `fetched`
   dictionary (so no concurrent writes and a deterministic next frontier), and
   enqueue newly-added ids as the next frontier.

Concurrency is bounded with a `SemaphoreSlim(MaxFanOutConcurrency)` around
`Task.WhenAll` over the level (results kept in order so the merge is
deterministic). Best-effort error handling is unchanged: a parent whose fetch
throws contributes no children (its exception is swallowed, non-cancellation).

The whole-list branch stays sequential — it's capped small
(`MaxWholeListFetches`), typically 0–few lists, and runs before the per-parent
phase; #144's Part 2 names the *per-parent BFS* specifically. Out of scope here.

`ResolveForeignSubtasksAsync`'s result is stored into a dictionary keyed by id
in `TodoApp` (`_foreignSubtasks`), so intra-list ordering of the returned list
never mattered — the concurrency introduces no observable ordering change.

## Tests

Extend `ResolveForeignSubtasksTests`:
- **Regression:** all existing tests stay green unchanged in intent — same
  fetched sets, same `Truncated`, same budget behaviour, same best-effort skips.
  The exact-order budget test (`["P", "c"]`) holds because those are separate
  single-item levels.
- **Thread-safety of the fake:** the call-recording lists become lock-guarded so
  concurrent same-level fetches can't race them (test-infra only, not a
  weakening).
- **New — bounded overlap:** mirror `TaskServiceParallelFetchTests`'
  rendezvous + peak-in-flight idiom. A level of `MaxFanOutConcurrency` sparse
  parents must overlap (rendezvous of `MaxFanOutConcurrency` only completes if
  they run together — sequential code times out) and the peak in-flight must
  never exceed the cap. Prove the cap bound with `> cap` parents in one level.

## Part 1 feasibility (deferred, kept on #144)

The bulk parent-scoped team-tasks query needs `GET /v2/team/{team_id}/task`
(`GetFilteredTeamTasks`) to accept a `parent` / `task_ids[]` filter. The curated
spec today exposes only `assignees[]`, `page`, `include_closed`, `subtasks`,
`date_updated_gt` on that endpoint, and ClickUp's v2 "Get Filtered Team Tasks"
reference documents no parent-id / task-id scoping parameter (it filters by
space/folder/list ids, assignees, statuses, tags, dates — not by parent). So the
"filtered to the matched parent ids" shape #144 describes is **not supported as
specified**, which is exactly the "confirm ClickUp actually supports scoping by
parent id before committing" caveat #144 flagged. Deferred rather than guessing
at a spec edit + Kiota regen for a parameter the API may reject. #144 stays open
to track it (and any alternative, e.g. list-scoped bulk with client-side parent
filtering).

## Out of scope / deferred

- Part 1 (bulk parent-scoped query) — tracked by #144.
- Whole-list branch concurrency — small, capped; not the named target.
- Cross-refresh caching of fetched subtrees — persistent-cache epic #118.
