using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The persisted feed document written under <see cref="StateKeys.Feed"/> (#123). It carries the last
/// aggregated feed plus the <see cref="Key"/> fingerprint it was captured for, a
/// <see cref="SchemaVersion"/>, and the <see cref="CapturedAtMs"/> capture time (#124), so a load can
/// reject a payload that belongs to a different context (workspace / assignee scope), an older shape,
/// or a too-old snapshot instead of painting the wrong or stale set.
/// </summary>
public sealed record FeedCacheDocument
{
    /// <summary>The cache-format version; bumped if the persisted shape changes incompatibly so an old
    /// document is discarded rather than deserialised into garbage.</summary>
    public int SchemaVersion { get; init; } = FeedCache.CurrentSchemaVersion;

    /// <summary>The workspace/assignee fingerprint (<see cref="FeedCache.KeyFor"/>) the payload was
    /// captured under. A load only trusts the payload when this matches the current config.</summary>
    public required string Key { get; init; }

    /// <summary>Epoch-ms UTC time the feed was persisted (#124). Backs the staleness marker on the
    /// instant paint and the max-age eviction on load. Absent (0) in pre-#124 v1 documents, which the
    /// <see cref="SchemaVersion"/> bump discards before this is read.</summary>
    public long CapturedAtMs { get; init; }

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
/// The store holds exactly one feed document at a time (each <see cref="Save(AppConfig,IReadOnlyList{CommentItem})"/>
/// supersedes the prior), so it is inherently bounded in count. Age-based self-pruning (#124) discards a
/// snapshot older than <see cref="_maxAge"/> on load, and <see cref="CachedSnapshot{T}.CapturedAt"/> lets
/// the caller mark how stale the instant paint is. Full reset-on-logout is handled at the composition
/// root (<c>Program.cs</c> <c>--reset</c>); a workspace/assignee switch is already a clean miss via the
/// <see cref="KeyFor"/> fingerprint, so it can never surface the wrong feed.
/// </para>
/// </summary>
public sealed class FeedCache
{
    /// <summary>The current <see cref="FeedCacheDocument.SchemaVersion"/>. Bumped to 2 in #124 when the
    /// capture timestamp was added, so any pre-#124 v1 document is discarded (a one-time miss → live
    /// load) rather than painted with a fabricated age.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Default <see cref="_maxAge"/>: a feed older than this is treated as a miss and pruned.
    /// Generous — the instant paint is only ever a brief bridge to the live refresh, so this just stops
    /// a feed from a workspace untouched for weeks being flashed before the fresh load.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);

    private readonly IStateStore _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxAge;

    /// <param name="store">The persistence backend the feed is read from / written to.</param>
    /// <param name="timeProvider">Clock for capture stamping and age comparisons (defaults to the
    /// system clock).</param>
    /// <param name="maxAge">How old a feed may be before a load treats it as a miss and prunes it
    /// (defaults to <see cref="DefaultMaxAge"/>).</param>
    public FeedCache(IStateStore store, TimeProvider? timeProvider = null, TimeSpan? maxAge = null)
    {
        _store = store;
        _clock = timeProvider ?? TimeProvider.System;
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    /// <summary>
    /// The cached feed for <paramref name="config"/>'s context, or <see langword="null"/> on a miss
    /// (see <see cref="LoadSnapshot"/> for the exact miss conditions). A non-null result may be empty
    /// (an empty feed was genuinely cached).
    /// </summary>
    public IReadOnlyList<CommentItem>? Load(AppConfig config) => LoadSnapshot(config)?.Items;

    /// <summary>
    /// The cached feed for <paramref name="config"/>'s context <b>with its capture time</b>, or
    /// <see langword="null"/> when nothing is cached, the cached payload was captured for a different
    /// context (workspace / assignee scope), it was written by an incompatible schema version, or it is
    /// older than the max age (in which case the stale document is also pruned). A non-null result's
    /// <see cref="CachedSnapshot{T}.Items"/> may be empty (an empty feed was genuinely cached).
    /// </summary>
    public CachedSnapshot<CommentItem>? LoadSnapshot(AppConfig config)
    {
        FeedCacheDocument? doc;
        try
        {
            doc = _store.Load<FeedCacheDocument>(StateKeys.Feed);
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

        // Age-based eviction (#124): a structurally-invalid timestamp (hand-tampered file) or a feed
        // older than the max age is a miss, and the stale document is pruned so it doesn't linger. Our
        // own writes are always in range and, in practice, minutes old.
        DateTimeOffset capturedAt;
        try { capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(doc.CapturedAtMs); }
        catch (ArgumentOutOfRangeException) { _store.Delete(StateKeys.Feed); return null; }
        if (_clock.GetUtcNow() - capturedAt > _maxAge)
        {
            _store.Delete(StateKeys.Feed);
            return null;
        }
        return new CachedSnapshot<CommentItem>(doc.Items, capturedAt);
    }

    /// <summary>Persist <paramref name="items"/> as the feed cache for <paramref name="config"/>'s
    /// context, replacing any prior document.</summary>
    public void Save(AppConfig config, IReadOnlyList<CommentItem> items)
        => Save(KeyFor(config), items);

    /// <summary>
    /// Persist <paramref name="items"/> under an explicit context <paramref name="key"/> (from
    /// <see cref="KeyFor"/>), stamped with the current time, replacing any prior document. Used when the
    /// key must be captured at fetch-start rather than at save-time — the feed's F12 toggle can flip a
    /// <see cref="KeyFor"/>-relevant flag mid-fetch, so saving under the live config's key could file the
    /// just-fetched data under the wrong fingerprint.
    /// </summary>
    public void Save(string key, IReadOnlyList<CommentItem> items)
        => _store.Save(StateKeys.Feed, new FeedCacheDocument
        {
            Key = key,
            CapturedAtMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Items = items,
        });

    /// <summary>Forget the cached feed (used by <c>--reset</c>). A no-op when nothing is cached.</summary>
    public void Clear() => _store.Delete(StateKeys.Feed);

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
