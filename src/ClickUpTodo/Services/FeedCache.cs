using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The persisted feed document written under <see cref="StateKeys.Feed"/> (#123). It carries the last
/// aggregated feed plus the <see cref="Key"/> fingerprint it was captured for and a
/// <see cref="SchemaVersion"/>, so a load can reject a payload that belongs to a different context
/// (workspace / assignee scope) or an older shape instead of painting the wrong set.
/// </summary>
public sealed record FeedCacheDocument
{
    /// <summary>The cache-format version; bumped if the persisted shape changes incompatibly so an old
    /// document is discarded rather than deserialised into garbage.</summary>
    public int SchemaVersion { get; init; } = FeedCache.CurrentSchemaVersion;

    /// <summary>The workspace/assignee fingerprint (<see cref="FeedCache.KeyFor"/>) the payload was
    /// captured under. A load only trusts the payload when this matches the current config.</summary>
    public required string Key { get; init; }

    /// <summary>The cached feed entries (newest-first), as last aggregated.</summary>
    public required IReadOnlyList<CommentItem> Items { get; init; }
}

/// <summary>
/// Persists the last successfully-aggregated mentions/comments feed via <see cref="IStateStore"/> so
/// the feed screen paints instantly on open while the live refresh runs (#123, part of Epic #118).
/// <para>
/// The feed (<see cref="FeedService.LoadFeedAsync"/>) is scoped <b>server-side</b> by exactly two
/// things: the workspace, and the <c>Assignee IS</c> rule values (they resolve to the assignee-id set
/// the assigned-tasks fetch is filtered by). So the cache is keyed on exactly those
/// (<see cref="KeyFor"/>). Unlike <see cref="TaskCache"/> the fingerprint omits the Personal Tasks
/// list id — the feed is built from <em>assigned</em> tasks only, never the personal list — so a pure
/// personal-list change still hits the cache. Mention stamping (<see cref="CommentItem.MentionsMe"/>)
/// is against the session-stable signed-in user, so it isn't part of the key either. A mismatched
/// fingerprint is a clean miss, so a context switch can never surface the wrong feed.
/// </para>
/// <para>
/// TTL / staleness / eviction / full reset-on-token-or-workspace-change are out of scope here and
/// tracked by #124; this stores and restores one document, superseded on each save.
/// </para>
/// </summary>
public sealed class FeedCache(IStateStore store)
{
    /// <summary>The current <see cref="FeedCacheDocument.SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The cached feed for <paramref name="config"/>'s context, or <see langword="null"/> when nothing
    /// is cached, the cached payload was captured for a different context (workspace / assignee scope),
    /// or it was written by an incompatible schema version. A non-null result may be empty (an empty
    /// feed was genuinely cached).
    /// </summary>
    public IReadOnlyList<CommentItem>? Load(AppConfig config)
    {
        FeedCacheDocument? doc;
        try
        {
            doc = store.Load<FeedCacheDocument>(StateKeys.Feed);
        }
        catch (JsonException)
        {
            // A corrupt cache is a miss, never a crash: the payload is rewritten (non-atomically) on
            // every successful aggregation, so a quit/kill/power-loss mid-write can truncate it.
            // Falling back to the live load is exactly the safe degradation the cache is meant to have.
            // (A missing/older-shape doc missing the required members surfaces as a JsonException here
            // too, before the version check below.)
            return null;
        }
        if (doc is null || doc.SchemaVersion != CurrentSchemaVersion || doc.Key != KeyFor(config))
            return null;
        return doc.Items;
    }

    /// <summary>Persist <paramref name="items"/> as the feed cache for <paramref name="config"/>'s
    /// context, replacing any prior document.</summary>
    public void Save(AppConfig config, IReadOnlyList<CommentItem> items)
        => store.Save(StateKeys.Feed, new FeedCacheDocument { Key = KeyFor(config), Items = items });

    /// <summary>Forget the cached feed (used by <c>--reset</c>). A no-op when nothing is cached.</summary>
    public void Clear() => store.Delete(StateKeys.Feed);

    /// <summary>
    /// The context fingerprint that determines the aggregated feed: the workspace id, the (sorted)
    /// <c>Assignee IS</c> rule values that scope the assigned fetch the feed fans out from, and the
    /// <see cref="AppConfig.FeedShowCompleted"/> (F12) flag — which changes <b>which tasks</b> are
    /// fetched (open-only vs. open + closed), so a cache captured under one setting must not instant-paint
    /// under the other. The Personal Tasks list id and client-side sort/group/non-assignee filters are
    /// deliberately excluded — they don't change which tasks' comments are fetched — so the cache survives
    /// those between sessions. Pure and stable (order-independent in the assignee set).
    /// </summary>
    internal static string KeyFor(AppConfig config)
    {
        // AssigneeRuleValues dedupes case-insensitively but keeps each value's original casing, so
        // normalise before joining — otherwise "Ben" and "ben" (which resolve to the same server-side
        // fetch) would fingerprint differently and cause a false miss (lost instant paint, never a
        // wrong feed). Consistent with the case-insensitive matching used at the fetch layer.
        var assignees = TaskService.AssigneeRuleValues(config.View)
            .Select(v => v.ToLowerInvariant())
            .OrderBy(v => v, StringComparer.Ordinal);
        var completed = config.FeedShowCompleted ? "completed" : "open";
        return string.Join('|', new[] { config.WorkspaceId, completed }.Concat(assignees));
    }
}
