# Persistent feed cache + longer refresh cadence (#123)

Part of Epic #118. Depends on the `IStateStore` seam (#120/#121 — landed) and the persistent task
cache (#122 — landed, `TaskCache`); cross-links the feed screen (#114) and its background refresh
wiring (#116). Mirrors the just-landed #122 pattern closely.

## Acceptance criteria (from the issue)

- Opening the feed with a warm cache shows items on the first frame; fresh results swap in without
  disrupting selection/scroll.
- The feed refreshes on its **own, longer cadence**, independent of the task-list `RefreshSeconds`
  (configurable; sensible default longer than the list).
- Update the feed cache after each successful aggregation.
- `dotnet test` green; `tui-validate` confirms cached-then-live feed render.

## Where the cache seam sits

The feed screen (`NotificationsFeedScreen`) renders an `IReadOnlyList<CommentItem>` produced by
`FeedService.LoadFeedAsync`. That fetch is scoped **server-side** by exactly two things:

- `config.WorkspaceId`
- the `Assignee IS` rule values (they resolve to the assignee-id set the assigned-tasks fetch is
  filtered by — `ResolveAssigneeIdsAsync(config.View)`).

It does **not** depend on `PersonalTasksListId` (the feed is built from *assigned* tasks only, never
the personal list), so — unlike `TaskCache` — the feed fingerprint omits the list id. Mention
stamping (`MentionsMe`) is against the session-stable signed-in user, so it isn't part of the key.

## Design

### 1. `StateKeys.Feed = "feed"`

One document (single key), overwritten on each successful aggregation, superseding the prior one.

### 2. `Services/FeedCache.cs` (testable, mirrors `TaskCache`)

```csharp
public sealed record FeedCacheDocument
{
    public int SchemaVersion { get; init; } = FeedCache.CurrentSchemaVersion;
    public required string Key { get; init; }                 // workspace|assignee fingerprint
    public required IReadOnlyList<CommentItem> Items { get; init; }
}

public sealed class FeedCache(IStateStore store)
{
    public const int CurrentSchemaVersion = 1;
    public IReadOnlyList<CommentItem>? Load(AppConfig config);   // null on miss/version/key mismatch/corrupt
    public void Save(AppConfig config, IReadOnlyList<CommentItem> items);
    public void Clear();
    internal static string KeyFor(AppConfig config);            // workspace + sorted lowercased assignee values
}
```

`Load` swallows `JsonException` → miss (a truncated cache from a mid-write quit must never brick a
launch), and returns null on schema-version or fingerprint mismatch (a context switch is a clean
miss, never a stale paint). A non-null result may be empty.

TTL / staleness / eviction / token-or-workspace-change reset stay out of scope here (owned by #124),
exactly as #122 scoped them out.

### 3. `AppConfig.FeedRefreshSeconds` (default 300)

A plain field defaulting to `300` — longer than the default list cadence (60s) — so an absent key on
an existing config loads the default with no migration. Clamped to the existing
`[MinRefreshSeconds, MaxRefreshSeconds]` = `[10, 3600]` range on edit.

### 4. TUI wiring (`TodoApp`)

- Inject `FeedCache` (constructor param, alongside `TaskCache`), built in `Program.cs`; cleared on
  `--reset` next to `taskCache.Clear()`.
- `OpenNotificationsFeed`: on a warm cache (`Load` returns a non-empty list), construct + show the
  screen **immediately** with the cached items, then kick a background `RefreshFeed` to swap in live
  data. On a cold cache, keep today's flow (flash "Loading feed…" → off-thread fetch → construct).
  Factor screen construction into a `CreateFeedScreen` helper so both paths share the wiring.
- The feed screen's auto-refresh cadence now comes from `_config.FeedRefreshSeconds`, not
  `RefreshSeconds`.
- Save the freshly-aggregated feed to the cache after every successful load (both the cold-path fetch
  and `RefreshFeed`), on the UI thread inside `Application.Invoke` (single-threaded access, matching
  `TaskCache.Save`).

### 5. F2 settings (`SettingsScreen` / `SettingsForm` / `SettingsResult`)

Add a "Feed refresh interval (seconds)" field beneath the task refresh field. Parse/clamp reuses
`SettingsForm.ParseRefreshSeconds` (same range). `SettingsResult` gains `FeedRefreshSeconds`; the
host applies it to `_config.FeedRefreshSeconds` on Save (a reopened feed picks up the new cadence).

## Tests

- `FeedCacheTests` (real temp-dir `JsonFileStateStore`): round-trip incl. `MentionsMe` /
  `MentionedUserIds` / `TaskId`; empty-set ≠ miss; workspace & assignee-scope keying; corrupt-doc →
  null-not-throw; schema-version mismatch → null; supersede; clear; `KeyFor` stability, case-
  insensitivity, independence from sort/group/non-assignee filters and from `PersonalTasksListId`.
- `SettingsForm` parse/clamp is already covered; the feed field reuses the same parser.

## Out of scope / deferred

- TTL / staleness marker / eviction / token-or-workspace reset → **#124** (already tracked).
- Recent-activity (`date_updated`) feed source → **#117** (deferred pending a maintainer signal).

## Manual TUI verification (can't run in CI)

Open the feed (Ctrl+E) twice: the second open paints the last feed instantly (status flashes
"cached … refreshing"), then live results swap in with the selected row preserved. F5 forces a
refresh; the auto-refresh tick fires on the feed cadence, not the list's. Confirm via `tui-validate`
after `dotnet test` is green.
