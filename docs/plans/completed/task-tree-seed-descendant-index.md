# Plan — #450: seed the Task Tree descendant BFS from a known-complete children index

Issue: [#450](https://github.com/rbcministries/clickup-todo-cli/issues/450). The **descendant** half of
[#419](https://github.com/rbcministries/clickup-todo-cli/issues/419) idea #2, deliberately deferred there.
#419 seeded only the **ancestry walk** from the in-memory snapshot (`TaskService.BuildSnapshotLookup` →
`GetTaskTreeAsync(..., snapshotLookup, ...)`); this slice does the same for the descendant BFS, but with a
stricter completeness discipline because the two phases consume their seed differently.

## Why descendants need a different seam than ancestry (#419's scope boundary)

The ancestry walk needs exactly **one** `TaskItem` per level (`GetTaskItemAsync(parentId)`), so a snapshot
hit is a safe drop-in — a stale hit can at worst mis-place/mis-label a level, never truncate the tree.

The descendant BFS is different: `GetSubtasksAsync(parent)` returns a parent's **complete** child set, and
`TaskTreeArranger` relies on that completeness. A snapshot-derived children list (grouping the *filtered*
main-list working set `_all`/`_rows` by `ParentId`) is **not** guaranteed complete — the snapshot only holds
the user's working set, not every child of an arbitrary node — so substituting it would silently **truncate**
branches. That is strictly worse than a slightly slower fetch, which is why #419 left the BFS authoritative.

So the seam here must accept children **only from a source that can vouch its set is complete**, and fall back
to `GetSubtasksAsync` on any doubt.

## The seam

A per-parent children index, defaulting to "no index" so existing behaviour is byte-for-byte unchanged:

```csharp
public async Task<IReadOnlyList<TaskTreeRow>> GetTaskTreeAsync(
    string taskId,
    Func<string, TaskItem?>? snapshotLookup = null,
    Func<string, IReadOnlyList<TaskItem>?>? childrenIndex = null,   // NEW (#450)
    CancellationToken ct = default)
```

`childrenIndex(parentId)` returns the parent's children **only when the entry is known complete**, else
`null` → the BFS fetches that parent via `GetSubtasksAsync`. Completeness is enforced at the boundary: the
index is *only ever populated* with sets a trusted source resolved in full, so a hit is safe to trust and a
miss safely fetches.

### BFS changes (invariants #417/#419 preserve)

The BFS drains the FIFO `queue` one bounded batch at a time. Each dequeued parent is resolved either from the
index (**free** — no round-trip, no budget) or by a fetch (**costs** one `MaxTreeSubtaskFetches` slot):

- **FIFO breadth-first order** — parents are dequeued strictly front-to-back; a batch's results are folded
  back in dequeue order, so first-occurrence de-dup and sibling order are unchanged whether a parent hit the
  index or was fetched.
- **De-dup** — `descSeen` (seeded with the ancestry ids) still gates every child, so an indexed parent whose
  children echo an ancestor can't loop the tree.
- **`MaxTreeSubtaskFetches` budget** — counts **fetches only**. An index hit spends no budget (it is not a
  round-trip): "only fetches the rest." The batch assembly admits a *miss* only while the fetch budget allows
  one; once the next FIFO parent is an unaffordable miss the walk stops, exactly as today's `fetches < budget`
  guard stops it. A fully-indexed frontier drains for free.
- **Best-effort per branch** — a fetch that throws still returns `null` (skips that branch) and still spends
  its budget slot, matching the serial walk. An index hit never throws.
- **Concurrency** — the misses in a batch are fetched concurrently under `MaxTreeSubtaskConcurrency` (#417);
  index hits resolve synchronously (`Task.FromResult`) and never occupy a concurrency slot.

**Never-truncate guarantee:** a completeness miss is the *only* thing the index can express besides a full
set, and it fetches. There is no path where an incomplete set is trusted, so seeding can never drop a real
child (acceptance criterion #2). Like #419, a *stale* complete hit (a child added after the source resolved)
is accepted — F5 re-fetches the tree fresh — but a stale hit is still a **complete-as-of-resolution** set, not
a truncated one.

## The trusted source (step 1 of the issue)

The one source in the app that resolves a parent's **complete** child set and can vouch for it is
`GetSubtasksAsync(parent)` itself (`GET /task/{id}?include_subtasks=true` — every child regardless of
assignee). The **F4 foreign-subtask resolution** (`TaskService.ResolveForeignSubtasksAsync`, #70/#87) already
calls it per-parent for in-view parents while the subtasks view is on. Each such call's result is a complete
direct-children set for that parent — this is exactly the "F4 rich view's already-resolved relationships" the
issue names.

- **Only the per-parent branch is recorded.** The whole-list branch (`GetListTasksAsync`) pulls a *list*, not
  a parent's children, and a parent's children can span lists — it cannot vouch per-parent, so it is **not**
  recorded.
- **Only successful fetches are recorded.** A best-effort fetch that threw returns `[]`; recording that would
  falsely claim "no children" → truncation. Failures are skipped, so their parents miss the index and fetch.
- Truncation of the *foreign* resolution (a budget cap that drops **deeper** parents) never makes an
  already-fetched parent's own direct-children set incomplete, so recorded entries stay trustworthy.

`ResolveForeignSubtasksAsync` gains a `CompleteChildren` map on its result
(`ForeignSubtaskResolution`); `TodoApp` caches it (like `_foreignSubtasks`) and, at detail-open, builds the
index via a new pure `TaskService.BuildChildrenIndex(map)` and threads it into `GetTaskTreeAsync`.
`SingleTaskApp` passes `null` (it holds no foreign resolution) — its tree fetches exactly as today.

## Phases

1. **Seam + pure builder + tests.** Add the `childrenIndex` param and BFS logic; add
   `TaskService.BuildChildrenIndex`. Unit-test through the existing `IClickUpClient` fake
   (`TaskServiceTaskTreeTests`) and a small `BuildChildrenIndex` test class. No behaviour change when the
   index is `null`.
2. **Trusted source + TUI wire-in.** Surface `CompleteChildren` from `ResolveForeignSubtasksAsync` (recording
   per-parent-branch successes only); unit-test it in `ResolveForeignSubtasksTests`. Cache it in `TodoApp` and
   build + pass the index at detail-open. `SingleTaskApp` stays `null`.

## Tests (CI-verifiable through the `IClickUpClient` seam)

`TaskServiceTaskTreeTests` (in-memory fake, no token):

- **All existing tests stay green unchanged** — they pass no `childrenIndex`, so the default path is
  byte-for-byte the old BFS (order, de-dup, caps, best-effort, concurrency).
- **Indexed parent skips its fetch** — a parent supplied by the index is absent from `SubtaskCalls` yet its
  children are present and correctly nested.
- **Indexed grandchild** — an index hit's children are themselves BFS'd (fetched or indexed) so the subtree
  continues past a seeded level.
- **Miss falls back** — a parent the index returns `null` for is fetched exactly as un-indexed.
- **Index hit spends no budget** — a fully-indexed wide/deep tree resolves with **zero** `GetSubtasksAsync`
  calls; a mix fetches only the missed parents, and a tree of misses larger than the budget still caps at
  `MaxTreeSubtaskFetches` fetches.
- **Empty complete set is trusted** — an index entry of `[]` (a parent known to have *no* children) skips the
  fetch and adds no children (distinct from a `null` miss, which fetches).
- **De-dup still holds** — an indexed parent whose children echo an ancestor doesn't loop.
- **FIFO order preserved** — index hits and fetches interleaved still fold back in dequeue order.

`BuildChildrenIndex` (dedicated small class): hit returns the stored list; miss returns `null`; an empty-map
builder is an all-miss lookup; a stored empty list returns `[]` (not `null`).

`ResolveForeignSubtasksTests`: the returned `CompleteChildren` records each per-parent-branch fetch's children;
a whole-list-branch parent is **absent**; a failed (throwing) per-parent fetch is **absent**.

## Out of scope / not changed

- **Ancestry seeding (#419)** — unchanged; `snapshotLookup` keeps its exact behaviour.
- **The initial task fetch** — still a round-trip so its error propagates (a snapshot/index can't seed it).
- **Idea #3 (progressive rendering)** — remains tracked under #419's parent scope, TUI-coupled.
- No rendering, keypress, list-source, or driver change — the tab's output is identical; only the number of
  descendant round-trips differs. Per `CLAUDE.md` the host wiring is not CI-unit-testable; the pure builder,
  the seeded BFS, and the `ResolveForeignSubtasksAsync` recording are.
