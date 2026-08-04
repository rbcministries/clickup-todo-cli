# Plan — #419 (idea #2): seed the Task Tree's ancestry walk from the in-memory snapshot

Issue: [#419](https://github.com/rbcministries/clickup-todo-cli/issues/419). Follow-up to
[#417](https://github.com/rbcministries/clickup-todo-cli/issues/417) (parallel descendant BFS), which
landed idea #1 and deferred ideas #2/#3 (see
`docs/plans/completed/…` / `task-tree-parallel-bfs.md`, "Deferred"). The loader is host-agnostic, so a
win here also benefits the single-task (`--task`) mode's Task Tree tab (#374).

## What this slice does

`TaskService.GetTaskTreeAsync` assembles the tree with three fetch phases:

- **Ancestry walk** — one `GetTaskItemAsync` per level up the parent chain, capped at
  `MaxAncestorFetches = 10`. **Inherently serial**: each parent id is only known once the level below
  it resolves, so #417's batching couldn't touch it — this is the one remaining serial HTTP chain in
  the tree load.
- **Current task** — the single initial `GetTaskItemAsync(taskId)`.
- **Descendant BFS** — bounded, level-batched `GetSubtasksAsync` (parallelized by #417).

This slice seeds **the ancestry walk** from the caller's in-memory working set: when the next parent
id is already in hand (the main list's snapshot `_all`, or a visible context row `_rows`), use that
record instead of a round-trip, falling back to the API only for ids not in the snapshot. Because the
ancestry chain is serial, replacing even a few of its levels with instant lookups is the highest-value
part of idea #2.

## Why ancestry only (scope boundary)

- **Current task: left as an API fetch — deliberately.** #419 requires preserving the #417 correctness
  constraint that *"only the initial fetch of the task itself propagates its error."* Seeding it from a
  possibly-stale snapshot would both drop that error-propagation guarantee and root the tree on stale
  data. It is a single fetch, not a chain, so there is little to gain and a real invariant to lose.
- **Descendants: NOT seeded here.** `GetSubtasksAsync(parent)` returns a parent's **complete** child
  set; a snapshot-derived children list (grouping the *filtered* main-list working set by `ParentId`)
  is **not** guaranteed complete, so substituting it would silently truncate branches of the tree.
  Safe descendant seeding needs a *known-complete* children index (e.g. from the F4 rich view's
  resolved relationships) — deferred to a follow-up (see below).

## The seam

A single-task lookup delegate, defaulting to "no snapshot" so existing behaviour is byte-for-byte
unchanged:

```csharp
public async Task<IReadOnlyList<TaskTreeRow>> GetTaskTreeAsync(
    string taskId,
    Func<string, TaskItem?>? snapshotLookup = null,
    CancellationToken ct = default)
```

In the ancestry `while` loop, before the `GetTaskItemAsync` round-trip:

```csharp
var seeded = snapshotLookup?.Invoke(parentId!);
if (seeded is not null) { parent = seeded; }        // in hand — no round-trip
else { try { parent = await client.GetTaskItemAsync(parentId!, ct); } catch (…) { break; } }
```

Invariants preserved exactly (seed present or not):

- **Cycle-safety** — the `seen.Add(parentId!)` guard runs *before* resolution, unchanged, so seeding
  can't loop.
- **Cap** — a seeded ancestor still counts toward `MaxAncestorFetches`; the cap bounds total ancestry
  **depth** (seeded + fetched), keeping the rendered chain bounded regardless of snapshot size.
- **Best-effort** — a snapshot **miss** falls through to the same try/catch API path (a failed level
  still ends the walk); a seeded item whose `ParentId` points outside the snapshot simply fetches the
  next level.
- **De-dup seeding** for descendants is untouched (ancestry ids still seed `descSeen`).

### Pure helper for the caller (CI-testable)

The TUI builds the delegate from its in-memory state on the UI thread. Factor the pure part into a
static helper so it is unit-testable without a TUI and so the off-thread tree load reads a **frozen**
snapshot (never the live, mutating `_rows`):

```csharp
// TaskService — freezes primary + non-null rows into an immutable id→task map (primary wins,
// mirroring FindById's precedence). Safe for concurrent reads off the UI thread.
public static Func<string, TaskItem?> BuildSnapshotLookup(
    IReadOnlyList<TaskItem> primary, IEnumerable<TaskItem?> rows)
```

## TUI wire-in

- **`TodoApp`** (detail-screen construction, UI thread): capture
  `TaskService.BuildSnapshotLookup(_all, _rows)` once and pass it into the `loadTaskTreeAsync`
  delegate. Freezing at construction avoids racing the live `_rows` list from the off-thread tree
  load; any staleness only ever causes a snapshot **miss** → API fetch, never wrong data (the tree
  also re-fetches on F5).
- **`SingleTaskApp`** (`--task` mode): pass `snapshotLookup: null` — single-task mode holds no
  broader working set to seed ancestry from, so behaviour is unchanged.

No rendering, keypress, list-source, or driver code changes — the tab's output is identical; only the
number of ancestry round-trips differs. Per `CLAUDE.md`, the host wiring (`_all`/`_rows` reads,
`Window` code) is not CI-unit-testable; the pure helper and the seeded walk are.

## Tests (CI-verifiable through the `IClickUpClient` seam)

Extend `TaskServiceTaskTreeTests` (in-memory fake, no token):

- **All existing tests stay green unchanged** — they call `GetTaskTreeAsync(id)` with no seed, so the
  default path is byte-for-byte the old behaviour (order, de-dup, caps, best-effort, `ItemCalls`
  sequence).
- **New — seeded ancestor skips its fetch:** a snapshot supplying a parent means that parent is
  **absent** from `ItemCalls` yet still present, correctly placed, in the arranged rows.
- **New — partial seed falls back per level:** an ancestry chain where only some levels are in the
  snapshot fetches exactly the missing levels.
- **New — miss is a no-op:** a lookup that returns `null` for everything reproduces the un-seeded
  `ItemCalls` exactly.
- **New — cap counts seeded ancestors:** a fully-seeded chain longer than `MaxAncestorFetches` still
  stops at the cap (bounds depth, not just fetches).
- **New — current task and error propagation unchanged:** the initial `GetTaskItemAsync(taskId)` still
  runs (and still propagates its error) even when a seed is present.
- **New — `BuildSnapshotLookup`** (dedicated small test class): primary-wins precedence, `rows`
  fallback for context rows outside `primary`, null-row entries skipped, and miss → `null`.

## Deferred (tracked separately, linked from the PR)

- **Descendant/subtask seeding** — needs a *known-complete* children index (the F4 rich view's
  resolved relationships) so a snapshot lookup can't truncate a branch. New follow-up issue.
- **Idea #3 — progressive rendering** — render levels as they resolve / surface progress instead of
  one "Loading task tree…" placeholder. TUI-coupled, `tui-validate`-only. Remains tracked by #419's
  parent scope; noted in the PR.
