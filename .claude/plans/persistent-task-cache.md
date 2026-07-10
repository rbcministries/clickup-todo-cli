# Persistent task cache — instant first paint (#122)

Part of Epic #118. Depends on `#120` (the `IStateStore` seam) — **landed** (`189feb0`).

## Goal

Give the app an instant first paint: persist the last successfully-loaded task working set and
render it immediately on launch while the live refresh loads, then swap in fresh results
(stale-while-revalidate) without losing selection/scroll.

## Acceptance criteria (from the issue)

- With a warm cache, the task list is visible on the first frame before any network round-trip.
- Cache is per workspace/view so switching context never shows the wrong set.
- `dotnet test` green; `tui-validate` confirms cached-then-live render with no flicker/selection loss.

## Where the cache seam sits

The working set the UI renders is `TodoApp._all` — the merged, de-duplicated snapshot produced by
`TaskService.LoadAsync` / `LoadSnapshotAsync` (assigned-to-me ∪ Personal Tasks list). Everything the
F3 view does (filter / sort / group, `Status IS NOT`, subtask nesting) is applied **client-side at
render time** in `TaskView.Apply` / `Render`, *not* in the fetch. Only three things scope the
server-side fetch and therefore change `_all`:

- `WorkspaceId`
- `PersonalTasksListId`
- the `Assignee IS` rule values (they scope the assigned fetch server-side, #68)

So the cache is keyed on exactly those. Sort/group and non-assignee filters are deliberately **not**
part of the key: the cached superset stays valid across a pure sort/group/filter change, and `Render`
re-applies the current view to it — an instant *correct* paint even if the view changed between
sessions. This is a stronger reading of "per workspace/view" than invalidating on client-side view
tweaks, and it can never show the wrong *set* (the only real hazard the AC guards against).

## Design

### 1. New key + document (`StateKeys.Tasks`)

Add `StateKeys.Tasks = "tasks"`. One document (single key), overwritten on each save, so it naturally
supersedes and there are no orphan files per old workspace.

```csharp
public sealed record TaskCacheDocument
{
    public int SchemaVersion { get; init; } = TaskCache.CurrentSchemaVersion;
    public required string Key { get; init; }              // fingerprint the payload was stored under
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
}
```

The document stores the fingerprint it was written for. `Load` returns the tasks **only** when
`doc.Key` matches the current fingerprint *and* `doc.SchemaVersion` matches — otherwise `null`
(cache miss → normal loading path). That satisfies "switching context shows nothing stale" without
encoding the fingerprint into the store key.

### 2. New service `Services/TaskCache.cs` (testable)

```csharp
public sealed class TaskCache(IStateStore store)
{
    public const int CurrentSchemaVersion = 1;
    public IReadOnlyList<TaskItem>? Load(AppConfig config);   // null on miss / key- or version-mismatch
    public void Save(AppConfig config, IReadOnlyList<TaskItem> tasks);
    public void Clear();                                      // store.Delete(StateKeys.Tasks)
    internal static string KeyFor(AppConfig config);          // pure
}
```

`KeyFor` = `WorkspaceId | PersonalTasksListId | <sorted Assignee-IS rule values>`, reusing
`TaskService.AssigneeRuleValues(view)`. Pure and unit-testable.

`TaskItem` is a plain record of JSON-friendly scalars + `IReadOnlyList<TaskAssignee>`; it round-trips
through the existing `JsonFileStateStore` serializer (camelCase, enums-as-strings) unchanged.

### 3. Wire into `Program.cs`

- Build a `TaskCache` over the same `IStateStore` and pass it to `TodoApp`.
- `--reset`: also `taskCache.Clear()` alongside `configStore.Delete()` so a reset leaves no stale
  cache doc behind. (Full token/workspace-change invalidation is #124; this is just the reset path.)

### 4. Wire into `TodoApp`

- **First paint:** in `Run()`, right after `Build()`, `TryPaintCachedTasks()`:
  load the cache for the current config; if non-empty, set `_all`, set a distinct status
  (`"Showing cached tasks · refreshing…"`), set `_signature = CurrentSignature(_all)`, and `Render`.
  This happens synchronously on the UI thread before `Application.Run` pumps, so the first live
  `OnTasksLoaded` (marshalled via `Application.Invoke`) always arrives after it. No race.
- **No-flicker swap:** because `_signature` is seeded from the cached set, if the first live load is
  identical the existing `OnTasksLoaded` signature-equality fast-path skips the re-render (only the
  status line updates). If it differs, `Render(keepTaskId: CurrentTask()?.Id)` keeps the cursor.
- **Write-back:** in `OnTasksLoaded`, after `_signature = signature; Render(...)` (i.e. only when the
  set actually changed — the fast-path early-returns otherwise), `_taskCache.Save(_config, tasks)`.
  Bounded payload, consistent with config saves happening on the UI thread. The optimistic
  status-update path (`UpdateTaskRow`) is intentionally *not* hooked: the next poll saves the
  authoritative set, and caching an as-yet-unconfirmed optimistic value would risk persisting a value
  the server later rejects.

## Tests (`tests/ClickUpTodo.Tests/TaskCacheTests.cs`)

Use the in-memory `IStateStore` double already in `StateStoreTests.cs` (or a temp-dir
`JsonFileStateStore`).

- Round-trip: `Save` then `Load` returns an equal task list (include a task with assignees).
- `Load` → null when nothing is cached.
- `Load` → null when the config key differs (different workspace / list / assignee rule) — the
  "switching context shows nothing stale" guarantee.
- `Load` → null on schema-version mismatch.
- `Save` overwrites the prior document (supersede).
- `KeyFor` is pure & stable: same config → same key; workspace/list/assignee changes → different key;
  sort/group/non-assignee-filter changes → **same** key (locks in the design decision above).
- `Clear` removes the document.

TUI first-paint wiring is verified by build + reasoning + a `tui-validate` run (pre-seed a cache doc
in the fake home) — the UI itself isn't unit-testable in CI.

## Invariants preserved

- No generated-client or curated-spec change (no new ClickUp API surface).
- No second focusable pane (#3): the cache only feeds the existing single `ListView` render path.
- Bare letters stay reserved for type-ahead (#12): no new key bindings.

## Deferred (tracked by remaining Epic #118 sub-issues)

- TTL / staleness indicator / size eviction / full reset-on-token-or-workspace-change → **#124**.
- Feed cache → **#123**; statuses/list-colors cache → **#125**.
