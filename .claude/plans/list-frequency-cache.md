# List frequency cache (#238)

Part of the Writing New Content epic (#208). Gives the future List selector (#239) a pool of
candidate lists so it can (a) fill its empty-state list up to N rows and (b) back type-ahead
search — exactly as the Assignees pane (#158) does with people. The pool is the **most-frequent
lists across the tasks the user has loaded**, persisted per workspace so it warms the selector on
next launch. A faithful mirror of the merged assignee-frequency cache (#155), keyed by the list's
**string** id and yielding `NamedEntity(string Id, string Name)`.

## Dependencies (all landed on `main`)

- `IStateStore` persistence seam — #120.
- `TaskItem.ListId` / `TaskItem.ListName` (`string?`) — already carried on every list item
  (`Models.cs:144-145`), so the row feed needs no API calls.
- The scheduled list-hierarchy walk (#236, `5b23d80`) — `TaskService.ResolveWorkspaceListsAsync`
  returns `WorkspaceListsResolution(IReadOnlyList<NamedEntity> Lists, bool PassComplete)` and
  accumulates into `KnownWorkspaceLists`. This is the long-tail feed source.

No spec edit / Kiota regen, no new ClickUp API surface, no UI keybinding, no render surface.

## Design

Split the classic repo way: **pure logic** (unit-tested, no I/O) separate from the **stateful
service** that owns the map and persistence. The one structural departure from the assignee cache:
this cache owns **no** fetch delegate — the long tail is pushed in from the refresh loop's walk
step, not fetched by the cache.

### 1. Pure logic — `Services/ListFrequency.cs` (static)

- `ListFrequencyEntry(string Id, string Name, IReadOnlyList<string> TaskIds)` — the tally unit
  (also the persisted shape). `Count => TaskIds.Count` (`[JsonIgnore]`) is the **distinct-task**
  ranking weight, so re-observing the same task never inflates it.
- `Accumulate(IDictionary<string, ListFrequencyEntry> acc, IEnumerable<TaskItem> tasks)` →
  `bool changed`. Adds each task's id to its home list's distinct-task set (one list per task) and
  refreshes `Name` to the latest non-blank seen; trims and skips blank list id / blank list name /
  blank task id. **Idempotent** — re-feeding a task already recorded for a list is a no-op.
- `Seed(IDictionary<…> acc, IEnumerable<NamedEntity> lists)` → `bool changed` — adds candidates
  with an empty task set (count `0`) **without** clobbering a real count or a known name. Used by
  the scheduled walk to backfill the long tail.
- `TopMostFrequent(IEnumerable<ListFrequencyEntry> entries, int n, ISet<string>? exclude)` →
  `IReadOnlyList<NamedEntity>`. Ranked by count desc, then name asc (case-insensitive), then id
  (ordinal) — deterministic. Drops excluded ids and blank names.
- `Match(IEnumerable<…> entries, string? query, ISet<string>? exclude)` →
  `IReadOnlyList<NamedEntity>`. Case-insensitive substring on `Name`; blank query returns the
  frequency-ranked pool; same ordering as above.

### 2. Stateful service — `Services/ListFrequencyCache.cs`

- Ctor: `(IStateStore store, string workspaceId)` — **no fetch delegate**. On construction loads
  `StateKeys.Lists`; adopts the entries only if the persisted document's `WorkspaceId` matches (a
  mismatch is a clean miss → empty pool, honouring per-workspace keying / #124). A `SchemaVersion`
  guards an incompatible future shape.
- `RecordFromTasks(IReadOnlyList<TaskItem>)` — the priority tier: `Accumulate` into the in-memory
  map; persists (one `store.Save`, try/catch so a failed write never breaks the refresh loop) only
  when it changed. Idempotent → a no-op on every steady-state poll.
- `SeedLists(IReadOnlyList<NamedEntity> lists)` — the long-tail intake the walk pushes each step:
  `ListFrequency.Seed` (count 0), persist only if it added a new list. Idempotent and additive, so
  re-pushing the full known-set every step stays off the hot path.
- Query pass-throughs: `TopMostFrequent(n, exclude)`, `Match(query, exclude)`.
- Persistence doc: `ListFrequencyDocument(int SchemaVersion, string WorkspaceId,
  IReadOnlyList<ListFrequencyEntry> Entries)`.
- Thread-safety: a `Lock` around the map + persist (mirrors the assignee cache).

### 3. Key — `Configuration/StateKeys.cs`

Add `public const string Lists = "lists";` (file backend → `lists.json`).

### 4. Wiring (not CI-testable; verified by build + reasoning)

- `Program.cs`: construct `new ListFrequencyCache(stateStore, config.WorkspaceId)`; pass into
  `TodoApp` (and the E2E harness's `TodoApp` construction).
- `TodoApp`: `_lists` field beside `_assignees`; `OnTasksLoaded` → `RecordFromTasks(tasks)` beside
  the assignee tally; `RunWorkspaceListWalkStepAsync` captures the resolution and calls
  `_lists.SeedLists(resolution.Lists)`. No new focusable pane, no keybinding, no render change
  (preserves #3 / #12).

## Tests (`tests/ClickUpTodo.Tests/ListFrequencyTests.cs`, `ListFrequencyCacheTests.cs`)

- Pure: accumulate tallies & ranks by occurrence; distinct-task idempotence; name refresh; blank
  list id / name / task id skipped; trims id + name; top-N excludes / respects N; deterministic
  tie-break (name then ordinal id); substring match case-insensitive; blank query → ranked pool;
  `Seed` adds count-0 candidates without clobbering real counts/names.
- Service: round-trips through a temp `JsonFileStateStore` (warm store survives a new instance);
  workspace-mismatch → clean miss; schema-version mismatch → miss; `RecordFromTasks` persists only
  on change and never inflates across warm restart; `SeedLists` persists only when it adds a new list,
  survives a warm restart, and a later task row merges into the seeded entry (no duplicate).

## Invariants

- Generated client / curated spec untouched. No second focusable pane (#3). Bare letters reserved
  for type-ahead (#12) — no new keybinding. Integration-free (no ClickUp boundary in tests).

## Deferred

- The List selector that consumes this pool is #239 (K); its New Task consumer is #240 (L) and its
  Quick Updates consumer is #242. TTL / eviction / full reset-on-workspace-change is the epic-#118
  cache-policy issue #124.
