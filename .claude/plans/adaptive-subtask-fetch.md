# Adaptive subtask fetch strategy (#87)

Follow-up to #70 / PR #84. Make the foreign-subtask fetch pick between a
**per-parent** and a **whole-list** fetch based on the shape of the snapshot,
instead of always doing one per-parent round-trip per in-view task.

## Background

`TaskService.ResolveForeignSubtasksAsync` pulls in a parent's teammate-owned
subtasks (regardless of assignee, #70) so they can nest under it. Today it does
a BFS: for **every** in-view task it calls `GetSubtasksAsync(id)`
(`GET /task/{id}?include_subtasks=true`) and recurses into pulled-in children.
The pure `ForeignDescendants(snapshot, pool)` selector then dedupes, excludes
present tasks, and keeps only pool tasks whose parent-chain reaches a snapshot
task.

- **Per-parent** (today): minimal payload (only the needed subtrees), works
  across lists, but **N round-trips** for N expandable parents.
- **Whole-list** (pre-#84): `GET /list/{id}/task?subtasks=true` — one round-trip
  per distinct list, but pulls **every task in the list** (large payloads).

## Cost model (rough)

Let `P` = distinct expandable parents in the snapshot, `L` = distinct lists they
span, `n_l` = parents in list `l`, `S_l` = size of list `l`.

| Strategy   | Round-trips        | Bytes (approx)             |
| ---------- | ------------------ | -------------------------- |
| Per-parent | `P` (+ recursion)  | sum of subtree sizes (small) |
| Whole-list | `L`                | sum of `S_l` (whole lists) |

Whole-list wins on round-trips exactly when a list holds several in-view parents
(`n_l` large): one fetch replaces `n_l` per-parent calls. It loses when a list
holds one parent but thousands of tasks. So the signal that makes whole-list pay
off is **clustering**: many parents concentrated in a few lists.

## Design — a pure planner + unchanged executor

Keep the selection pure and testable, per the issue: "the pure selection stays
in `ForeignDescendants`; only the fetch source/shape varies." Add a second pure
piece that decides the **fetch plan**, then execute it and feed the pool through
the existing `ForeignDescendants`.

### `Services/SubtaskFetchStrategy.cs` (pure, unit-tested)

```
record SubtaskFetchOptions(
    PerParentThreshold = 8,   // <= this many parents total -> all per-parent
    WholeListMinParents = 4,  // a list with >= this many in-view parents -> whole-list
    MaxWholeListFetches = 20, // cap distinct whole-list round-trips
    MaxPerParentFetches = 60) // cap distinct per-parent round-trips

record SubtaskFetchPlan(
    IReadOnlyList<string> WholeListIds,   // fetch each whole (dedupes parents in it)
    IReadOnlyList<string> PerParentIds,   // fetch each individually (BFS, recurses)
    bool Truncated)                       // a cap dropped work -> caller logs it

static SubtaskFetchPlan Plan(IReadOnlyList<TaskItem> snapshot, SubtaskFetchOptions? = null)
```

Heuristic:
1. `P` = distinct non-empty snapshot task ids (candidate parents). Empty -> empty plan.
2. `P <= PerParentThreshold` -> **all per-parent** (WholeListIds empty). Identical
   to today's behaviour for the common small-workspace case -> no regression, and
   preserves cross-list capture where it matters most.
3. Otherwise group parents by `ListId`. A list with `>= WholeListMinParents`
   in-view parents -> route the **list** to `WholeListIds`; its parents are then
   covered by the one list fetch. All other parents (sparse lists, or null
   `ListId`) -> `PerParentIds`.
4. Caps: `WholeListIds` ordered by (parent-count desc, listId asc) and truncated
   to `MaxWholeListFetches`; `PerParentIds` in stable snapshot order, truncated
   to `MaxPerParentFetches`. Any truncation sets `Truncated = true`.

Deterministic ordering so the heuristic is unit-testable.

### `TaskService.ResolveForeignSubtasksAsync` (executor, reasoning-verified)

- `Plan(snapshot)`.
- Whole-list: for each `WholeListIds` entry, `GetListTasksAsync(listId)` -> pool.
  A whole list contains intra-list chains at any depth, so `ForeignDescendants`
  captures deep same-list descendants without recursion.
- Per-parent: the existing BFS, but **seeded only from `PerParentIds`** (not the
  whole snapshot), still recursing into pulled-in children so cross-list /
  deeper descendants are reached for that branch. Best-effort per-parent skip on
  error is unchanged.
- Merge both pools (dedupe by id) -> `ForeignDescendants(snapshot, pool)`.
- If `plan.Truncated`, `Debug.WriteLine` a one-line notice (no silent
  truncation), consistent with the codebase's logging.

## Known tradeoff (documented, not silent)

The whole-list branch captures a routed parent's **same-list** descendants (the
overwhelmingly common case - ClickUp subtasks inherit their parent's list). A
subtask living in a *different* list than a whole-list-routed parent is not
recovered by that branch (the pre-#84 limitation), whereas per-parent recovers
it. This only applies to heavily-clustered lists (`>= WholeListMinParents`
parents), where the round-trip/payload win is large; sparse and small-workspace
cases stay per-parent and fully cross-list-correct. A future hybrid (issue's
"not-yet-considered strategy") could close the gap.

## Tests

`SubtaskFetchStrategyTests` (pure):
- Empty snapshot -> empty plan.
- `P <= PerParentThreshold` -> all per-parent, no whole-list (common case unchanged).
- Clustered: many parents in one/few lists (`>= WholeListMinParents`) -> those
  lists whole-list, others per-parent.
- Mixed: one dense list + several sparse -> dense list whole, sparse per-parent.
- Null / empty `ListId` parents -> always per-parent, never whole-list.
- Ordering determinism (parent-count desc, listId asc; per-parent stable).
- Caps: `> MaxWholeListFetches` / `> MaxPerParentFetches` -> truncated + flag set;
  under caps -> flag clear.
- Distinct-id dedup (a repeated snapshot id counted once).

`ForeignDescendants` tests stay green (selector untouched).

## Out of scope / deferred

- Bounded-concurrency fan-out for the per-parent branch, cross-refresh caching,
  and a bulk filtered-team-tasks query are noted in #87 as further options; not
  in this slice. If pursued, track under #87 or a new issue.
