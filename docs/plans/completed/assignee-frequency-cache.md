# Assignee frequency cache (#155)

Part of the Quick Updates epic (#153). Gives the future Assignees pane (#158) a pool of
candidate people so it can (a) fill its empty-state list up to N rows and (b) back type-ahead
search. The pool is the **most-frequent assignees across the task lists the user has loaded**,
persisted per workspace so it warms the pane on next launch.

## Dependencies (all landed on `main`)

- `IStateStore` persistence seam — #120, `189feb0`.
- `GetWorkspaceMembersAsync(workspaceId)` on the facade — #154, PR #166. Returns
  `IReadOnlyList<WorkspaceMember>` (`Id`, `Username?`, `Email?`).
- `TaskItem.Assignees : IReadOnlyList<TaskAssignee>` (`TaskAssignee(long Id, string Name)`) —
  already carried on the list item (#68).

No spec edit / Kiota regen, no new ClickUp API surface, no UI keybinding.

## Design

Split the classic repo way: **pure logic** (unit-tested, no I/O) separate from the **stateful
service** that owns the map, persistence, and the deferred top-up (mirrors `SubtaskArranger`
pure vs `TaskService` glue, and `DispatchWorkingDirectoryCache` pure vs `AppConfig` glue).

### 1. Pure logic — `Services/AssigneeFrequency.cs` (static)

- `AssigneeFrequencyEntry(long Id, string Name, IReadOnlyList<string> TaskIds)` — the tally unit
  (also the persisted shape). `Count => TaskIds.Count` (`[JsonIgnore]`) is the **distinct-task**
  ranking weight, so re-observing the same task never inflates it.
- `Accumulate(IDictionary<long, AssigneeFrequencyEntry> acc, IEnumerable<TaskItem> tasks)` →
  `bool changed`. Adds each task's id to that assignee's distinct-task set and refreshes `Name`
  to the latest non-blank seen; ignores id `0` / blank names / blank task ids. **Idempotent** —
  re-feeding a task already recorded for a person is a no-op — so calling it on every poll with
  the same working set neither inflates counts nor reports a change (the caller then skips the
  persist). Returns whether anything changed so the caller persists exactly when needed.
- `Seed(IDictionary<…> acc, IEnumerable<TaskAssignee> people)` → `bool changed` — adds
  candidates with an empty task set (count `0`) **without** clobbering a real count or a known
  name. Used by the deferred workspace-members top-up.
- `TopMostFrequent(IEnumerable<AssigneeFrequencyEntry> entries, int n, ISet<long>? exclude)` →
  `IReadOnlyList<TaskAssignee>`. Ranked by count desc, then name asc (case-insensitive), then id
  — deterministic. Excludes the given ids (the task's current assignees). Drops blank names.
- `Match(IEnumerable<…> entries, string query)` → `IReadOnlyList<TaskAssignee>`. Case-insensitive
  substring on `Name`; blank query returns the frequency-ranked pool; same ordering as above.

### 2. Stateful service — `Services/AssigneeFrequencyCache.cs`

- Ctor: `(IStateStore store, string workspaceId,
  Func<CancellationToken, Task<IReadOnlyList<WorkspaceMember>>> fetchMembers, TimeProvider?)`.
  On construction, loads `StateKeys.Assignees`; adopts the entries **only if** the persisted
  document's `WorkspaceId` matches (a mismatch is a clean miss → empty pool, honouring the
  per-workspace keying and #124 reset-on-workspace-change). A `SchemaVersion` guards an
  incompatible future shape (mismatch → miss).
- `RecordFromTasks(IReadOnlyList<TaskItem>)` — `Accumulate` into the in-memory map; persists (one
  `store.Save`, wrapped in try/catch so a failed write never breaks the refresh loop) only when
  it changed. Idempotent, so calling it on every `OnTasksLoaded` poll is a no-op in steady state
  (no inflation, no hot-path write).
- `TopUpAsync(int minCandidates, CancellationToken)` — if the pool is thinner than
  `minCandidates`, fetch workspace members off-thread and `Seed` them (count 0); persist if
  changed. Failures are swallowed (non-fatal, best-effort). Runs once, after first paint.
- Query pass-throughs: `TopMostFrequent(n, exclude)`, `Match(query)`.
- Persistence doc: `AssigneeFrequencyDocument(int SchemaVersion, string WorkspaceId,
  IReadOnlyList<AssigneeFrequencyEntry> Entries)`.
- Thread-safety: a `Lock` around the map + persist — `OnTasksLoaded` runs on the UI thread but
  `TopUpAsync` completes on a background thread (`IStateStore` impls aren't required to be
  thread-safe, per its doc).

### 3. Key — `Configuration/StateKeys.cs`

Add `public const string Assignees = "assignees";` (file backend → `assignees.json`).

### 4. Wiring (not CI-testable; verified by build + reasoning)

- `Program.cs`: construct the cache over the existing `stateStore` + `config.WorkspaceId` +
  `ct => client.GetWorkspaceMembersAsync(config.WorkspaceId, ct)`; pass into `TodoApp`.
- `TodoApp`: field + `OnTasksLoaded` → `RecordFromTasks`; a one-shot deferred `Task.Run` top-up
  after the first load paints. No new focusable pane, no keybinding (keeps #3 / #12 invariants).

## Tests (`tests/ClickUpTodo.Tests/AssigneeFrequencyTests.cs`, `AssigneeFrequencyCacheTests.cs`)

- Pure: accumulate tallies & ranks by occurrence; name refresh; id 0 / blank ignored; top-N
  excludes the task's current assignees and respects N; deterministic tie-break; substring match
  case-insensitive; blank query → ranked pool; `Seed` doesn't clobber real counts/names.
- Service: round-trips through a temp `JsonFileStateStore` (warm store survives a new instance);
  workspace-mismatch → clean miss (empty); schema-version mismatch → miss; `RecordFromTasks`
  persists only on change; `TopUpAsync` seeds members when thin, is a no-op when the pool is
  already full, and swallows a throwing fetch.

## Invariants

- Generated client / curated spec untouched. No second focusable pane (#3). Bare letters reserved
  for type-ahead (#12) — no new keybinding. Integration-free (no ClickUp boundary in tests).

## Deferred

- The Assignees pane that consumes this is #158; the screen shell is #156. TTL / eviction /
  full reset-on-workspace-change is the epic-#118 cache-policy issue #124.
