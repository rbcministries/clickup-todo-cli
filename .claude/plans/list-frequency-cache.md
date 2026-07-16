# List frequency cache (#238) — Writing New Content (J)

Part of the #208 epic. A per-workspace pool of the user's most-frequently-used
lists so the future List selector's empty state pre-populates with likely lists,
exactly as the Assignees pane does with people (#155). Built as a faithful
mirror of the merged assignee-frequency cache, keyed by **string list id** and
returning `NamedEntity(string Id, string Name)`.

## Feeds

1. **Task rows (priority tier, free):** every `TaskItem` carries `ListId`+`ListName`
   (`Models.cs:144-145`). `RecordFromTasks` tallies the lists the user actually
   sees on each refresh — with names, no API calls.
2. **Scheduled walk (long-tail backfill):** the incremental hierarchy walk
   (#236, merged) seeds the lists the task feed doesn't surface at count 0, via a
   `Seed`-style intake. The cache does **not** own a fetch delegate (unlike the
   assignee cache's `TopUpAsync`) — the walk is scheduled on the refresh loop and
   calls the intake with its enumerated `NamedEntity` lists.

## Pattern mirrored

- Pure rules: `Services/AssigneeFrequency.cs`
- Stateful glue: `Services/AssigneeFrequencyCache.cs`
- State key: `Configuration/StateKeys.cs`
- Wiring: `Program.cs` (construct), `TodoApp` ctor + `OnTasksLoaded`
- Tests: `AssigneeFrequencyTests.cs` + `AssigneeFrequencyCacheTests.cs`

## Deltas vs. the assignee cache

- Key type `string` (list id, `StringComparer.Ordinal`), not `long`.
- One list per task (not many assignees) → `Accumulate` records at most one
  `(ListId, ListName)` per task, skipping blank id / blank name / blank task id.
- No self-fetching `TopUpAsync(fetchMembers)`; instead a `SeedLists(IReadOnlyList<NamedEntity>)`
  intake the walk calls. So the cache ctor is `(IStateStore, workspaceId)` — no
  fetch delegate.
- Returns `NamedEntity` from `TopMostFrequent`/`Match`; `exclude` sets are `ISet<string>`.

## Phases

1. **Pure + persisted service + tests** — `ListFrequency.cs`, `ListFrequencyCache.cs`,
   `StateKeys.Lists`, `ListFrequencyTests.cs`, `ListFrequencyCacheTests.cs`.
   Full quality gate; commit; open draft PR.
2. **Wiring** — construct in `Program.cs` (+ E2E harness `Program.cs`), inject via
   `TodoApp` ctor into a `_lists` field, `RecordFromTasks` in `OnTasksLoaded`
   beside the assignee call, and `SeedLists(_tasks.KnownWorkspaceLists)` after a
   walk step in `RunWorkspaceListWalkStepAsync`. Quality gate; commit; push.

## Acceptance criteria (from the issue)

- Lists tally by distinct task id from the loaded working set (idempotent across
  polls); the pool persists per-workspace and is discarded on workspace/schema
  mismatch.
- The seen tier is available with no API calls; the scheduled walk (#236) seeds
  the long tail via the `Seed`-style intake.
- `TopMostFrequent`/`Match` rank as the assignee cache does; `dotnet test` green.

## Invariants preserved

- No `Generated/` hand edits, no spec change, no new API (pure/persistence only).
- Personal-token raw `Authorization` header untouched.
- Single sectioned `ListView`; no second focusable pane; no bare-letter shortcut
  (this slice adds no TUI-render surface — wiring only).
