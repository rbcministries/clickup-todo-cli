# Persist StatusCache + list colors across restarts (#125)

Part of Epic #118. Optional stretch. Depends on the persistent task cache (#122) and the
`IStateStore` seam — both merged on `main`.

## Goal

`StatusCache` (per-list status options, ~10-min TTL) and `TaskService._listColors` (per-list
color chips) are in-memory only today and rebuilt from scratch every launch, costing first-load
API round-trips. Persist both through `IStateStore`, warm the in-memory caches from the store on
startup, and honor TTL so nothing stale is shown past expiry.

## Acceptance criteria (from the issue)

- Status/color caches survive restart and honor TTL; no stale colors/statuses shown past expiry.
- `dotnet test` green.

## Design

Mirror the existing persistence pattern (`TaskCache` #122, `AssigneeFrequencyCache` #155):
one logical document per `StateKeys` key, workspace-fingerprinted (a mismatch is a clean miss),
`SchemaVersion`-guarded, and corrupt -> clean miss, never a throw (warm-up runs before the UI
loop). Two new keys, since statuses and colors persist on independent cadences:

- `StateKeys.Statuses = "statuses"` — per-list status options with per-entry capture timestamps.
- `StateKeys.ListColors = "listColors"` — per-list color chips with per-entry capture timestamps.

### Statuses — persistence built into `StatusCache`

`StatusCache` gains optional `IStateStore? store` + `workspaceId` params (no store => today's pure
in-memory behavior, so existing tests and any storeless caller are unchanged):

- Warm on construct: load the document (workspace + schema guarded; JsonException => skip), seeding
  each list's entry with its persisted `FetchedAt`. TTL is honored for free — the existing
  `TryGetFreshLocked` compares `now - FetchedAt` against the TTL, so a persisted entry older than
  the TTL is a miss and gets refetched on demand.
- Persist on store: after `FetchAndStoreAsync` writes an entry (already under `_gate`), rewrite the
  whole document best-effort (swallow write failures). Writing under the lock satisfies IStateStore's
  "serialise access per key" contract (same rationale as `AssigneeFrequencyCache.Persist`).

Document: `StatusCacheDocument(int SchemaVersion, string WorkspaceId, IReadOnlyList<StatusCacheEntryDto> Entries)`,
`StatusCacheEntryDto(string ListId, IReadOnlyList<StatusOption> Statuses, long FetchedAtMs)`.

### Colors — new `ListColorCache`, layered onto the existing dict

`TaskService` keeps its in-memory `_listColors` `ConcurrentDictionary` as the hot read path
(unchanged). An optional `ListColorCache` (present only when a store is supplied) sits alongside it:

- Warm on construct: seed `_listColors` from the cache's fresh entries (within a color TTL of
  7 days — colors change even more rarely than statuses; long enough to be worthwhile, short enough
  that a recolored list self-corrects within a week). Expiry is applied on load; within a session
  colors stay for the process lifetime (today's behavior).
- Persist after a fetch: `ResolveListColorsAsync` already fetches only not-yet-cached lists; after
  that batch it hands the newly-resolved `(listId -> color)` to `ListColorCache.Save`, which stamps
  them "now", merges into the persisted set (accumulating across sessions), and rewrites once.

Document: `ListColorDocument(int SchemaVersion, string WorkspaceId, IReadOnlyList<ListColorEntry> Entries)`,
`ListColorEntry(string ListId, string? Color, long FetchedAtMs)`.

### Wiring

- `TaskService` gains an optional `IStateStore? stateStore = null` param; it builds the persistent
  `StatusCache` and `ListColorCache` when supplied. `Program.cs` passes the app's `stateStore`.
- `--reset` deletes both new documents (by key, workspace-agnostic) alongside `taskCache.Clear()`.

## Non-goals / invariants

- No curated-spec / Kiota / `Generated/` change; no new ClickUp API surface.
- No TUI change — no second focusable pane (#3), no bare-letter keybindings (#12).
- Independent of the in-flight feed cache (#123/PR #214) — different keys, no shared state.
- Broader TTL/staleness-marker/eviction/reset-on-token-change policy stays with #124.

## Tests

- `StatusCacheTests` (extend): persisted round-trip warms a second instance without refetching;
  a persisted entry past its TTL is refetched; workspace/schema mismatch and corrupt doc => no warm
  pool, no throw; no-store path unchanged.
- `ListColorCacheTests` (new): round-trip incl. a null entry; workspace-scope miss; schema mismatch;
  corrupt => empty; TTL expiry filters stale entries on load; `Save` merges across sessions; `Clear`.
- Real temp-dir `JsonFileStateStore` for round-trips (mirrors `TaskCacheTests`); fake `IStateStore`
  where a store isn't under test.
