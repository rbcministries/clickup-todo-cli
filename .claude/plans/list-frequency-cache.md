# List frequency cache (#238 — Writing New Content J)

## Goal

A per-workspace pool of the user's most-frequently-used lists so the future List
selector (#239/K) empty state can pre-populate with likely lists — exactly as the
Assignees pane does with people (#155). No new ClickUp API surface, no Kiota regen,
no TUI-render change in this slice.

## Design — a faithful mirror of the merged assignee-frequency cache (#155)

Two feeds, per the issue:

1. **Task rows (primary tier, free):** every `TaskItem` carries `ListId`+`ListName`, so
   `RecordFromTasks` on each refresh tallies the lists the user actually sees, with names,
   and **no API calls**.
2. **Scheduled walk (long-tail backfill):** the merged list-hierarchy enrichment #236 (I)
   discovers workspace lists incrementally (`TaskService.ResolveWorkspaceListsAsync` →
   `WorkspaceListsResolution.Lists`). Those get `Seed`ed as count-0 candidates so lists the
   task feed never surfaces are still searchable/selectable.

Difference from the assignee cache: the list cache **owns no fetch delegate**. The assignee
cache self-fetches workspace members in `TopUpAsync`; here the #236 walk already runs on the
refresh loop and simply hands its discovered lists to `Seed`.

### Files

- **`Services/ListFrequency.cs`** (pure, mirrors `AssigneeFrequency.cs`): `ListFrequencyEntry`
  record + `Accumulate` / `Seed` / `TopMostFrequent` / `Match`, keyed by string id, ranked by
  count desc then name (OrdinalIgnoreCase) then id (Ordinal), returning `NamedEntity`.
- **`Services/ListFrequencyCache.cs`** (stateful, mirrors `AssigneeFrequencyCache.cs`):
  `ListFrequencyDocument` + per-workspace/schema-guarded load, `_gate`-serialised best-effort
  persist, `RecordFromTasks` (UI thread) + `Seed` (walk thread).
- **`Configuration/StateKeys.cs`**: add `Lists = "lists"`.

### Wiring

- `Program.cs`: construct beside the assignee cache; pass to `TodoApp`.
- `TodoApp` ctor `ListFrequencyCache lists` + `_lists` field; `OnTasksLoaded` calls
  `_lists.RecordFromTasks(tasks)`; `RunWorkspaceListWalkStepAsync` seeds `resolution.Lists`.

## Tests

`ListFrequencyTests.cs` + `ListFrequencyCacheTests.cs` mirroring the assignee test files.

## Invariants

No `Generated/` edit, no spec change, no new API surface; no TUI-render/input surface (no
#3/#12 impact); personal-token raw `Authorization` header untouched.

## Deferred (already tracked)

The `ListSelectorView`/`ListSelectorModel` consumer → #239 (K), blocked on the shared selector
base #243.
