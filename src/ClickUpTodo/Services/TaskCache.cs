using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The persisted snapshot document written under <see cref="StateKeys.Tasks"/> (#122). It carries the
/// task working set plus the <see cref="Key"/> fingerprint it was captured for, a
/// <see cref="SchemaVersion"/>, and the <see cref="CapturedAtMs"/> capture time (#124), so a load can
/// reject a payload that belongs to a different context (workspace / list / assignee scope), an older
/// shape, or a too-old snapshot instead of painting the wrong or stale set.
/// </summary>
public sealed record TaskCacheDocument
{
    /// <summary>The cache-format version; bumped if the persisted shape changes incompatibly so an old
    /// document is discarded rather than deserialised into garbage.</summary>
    public int SchemaVersion { get; init; } = TaskCache.CurrentSchemaVersion;

    /// <summary>The workspace/list/assignee fingerprint (<see cref="TaskCache.KeyFor"/>) the payload was
    /// captured under. A load only trusts the payload when this matches the current config.</summary>
    public required string Key { get; init; }

    /// <summary>Epoch-ms UTC time the snapshot was persisted (#124). Backs the staleness marker on the
    /// instant paint and the max-age eviction on load. Absent (0) in pre-#124 v1 documents, which the
    /// <see cref="SchemaVersion"/> bump discards before this is read.</summary>
    public long CapturedAtMs { get; init; }

    /// <summary>The cached task working set (assigned-to-me ∪ Personal Tasks list), as last loaded.</summary>
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
}

/// <summary>
/// Persists the last successfully-loaded task working set via <see cref="IStateStore"/> so the app can
/// paint instantly on launch while the live refresh runs (#122, part of Epic #118).
/// <para>
/// The working set the UI renders (<c>TodoApp._all</c>) is the merged snapshot from
/// <see cref="TaskService.LoadAsync"/> — assigned-to-me ∪ Personal Tasks list. Everything the F3 view
/// does (filter / sort / group, <c>Status IS NOT</c>, subtask nesting) is applied <b>client-side at
/// render time</b>, not in the fetch; only the workspace, the Personal Tasks list, and the
/// <c>Assignee IS</c> rules scope the server-side fetch and therefore change the set. So the cache is
/// keyed on exactly those (<see cref="KeyFor"/>): the cached superset stays valid across a pure
/// sort/group/filter change, and the caller re-applies the current view to it — an instant, still-correct
/// paint. It can never surface the wrong <em>set</em> after a context switch, because a mismatched
/// fingerprint is a clean miss.
/// </para>
/// <para>
/// The store holds exactly one task document at a time (each <see cref="Save"/> supersedes the prior),
/// so it is inherently bounded in count. Age-based self-pruning (#124) discards a snapshot older than
/// <see cref="_maxAge"/> on load, and <see cref="CachedSnapshot{T}.CapturedAt"/> lets the caller mark
/// how stale the instant paint is. Full reset-on-logout is handled at the composition root
/// (<c>Program.cs</c> <c>--reset</c>); a workspace/list/assignee switch is already a clean miss via the
/// <see cref="KeyFor"/> fingerprint, so it can never surface the wrong set.
/// </para>
/// </summary>
public sealed class TaskCache
{
    /// <summary>The current <see cref="TaskCacheDocument.SchemaVersion"/>. Bumped to 2 in #124 when the
    /// capture timestamp was added, so any pre-#124 v1 document is discarded (a one-time miss → live
    /// load) rather than painted with a fabricated age.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Default <see cref="_maxAge"/>: a snapshot older than this is treated as a miss and
    /// pruned. Generous — the instant paint is only ever a brief bridge to the live refresh, so this
    /// just stops a snapshot from a workspace untouched for weeks being flashed before the fresh load.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);

    private readonly IStateStore _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxAge;

    /// <param name="store">The persistence backend the snapshot is read from / written to.</param>
    /// <param name="timeProvider">Clock for capture stamping and age comparisons (defaults to the
    /// system clock).</param>
    /// <param name="maxAge">How old a snapshot may be before a load treats it as a miss and prunes it
    /// (defaults to <see cref="DefaultMaxAge"/>).</param>
    public TaskCache(IStateStore store, TimeProvider? timeProvider = null, TimeSpan? maxAge = null)
    {
        _store = store;
        _clock = timeProvider ?? TimeProvider.System;
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    /// <summary>
    /// The cached working set for <paramref name="config"/>'s context, or <see langword="null"/> on a
    /// miss (see <see cref="LoadSnapshot"/> for the exact miss conditions). A non-null result may be
    /// empty (an empty set was genuinely cached).
    /// </summary>
    public IReadOnlyList<TaskItem>? Load(AppConfig config) => LoadSnapshot(config)?.Items;

    /// <summary>
    /// The cached working set for <paramref name="config"/>'s context <b>with its capture time</b>, or
    /// <see langword="null"/> when nothing is cached, the cached payload was captured for a different
    /// context (workspace / list / assignee scope), it was written by an incompatible schema version,
    /// or it is older than the max age (in which case the stale document is also pruned). A non-null
    /// result's <see cref="CachedSnapshot{T}.Items"/> may be empty (an empty set was genuinely cached).
    /// </summary>
    public CachedSnapshot<TaskItem>? LoadSnapshot(AppConfig config)
    {
        TaskCacheDocument? doc;
        try
        {
            doc = _store.Load<TaskCacheDocument>(StateKeys.Tasks);
        }
        catch (JsonException)
        {
            // A corrupt cache is a miss, never a crash: the payload is rewritten (non-atomically) on
            // every changed poll, so a quit/kill/power-loss mid-write can truncate it — and this load
            // runs synchronously in Run() before the UI loop, so a throw here would brick every launch
            // until the file was hand-deleted. Falling back to the live load is exactly the safe
            // degradation the cache is meant to have. (A missing/older-shape doc missing the required
            // members surfaces as a JsonException here too, before the version check below.)
            return null;
        }
        if (doc is null || doc.SchemaVersion != CurrentSchemaVersion || doc.Key != KeyFor(config))
            return null;

        // Age-based eviction (#124): a structurally-invalid timestamp (hand-tampered file) or a snapshot
        // at or beyond the max age is a miss, and the stale document is pruned so it doesn't linger. The
        // boundary is exclusive (age == maxAge is stale), matching StatusCache's freshness check
        // (age < ttl). Our own writes are always in range and, in practice, minutes old.
        DateTimeOffset capturedAt;
        try { capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(doc.CapturedAtMs); }
        catch (ArgumentOutOfRangeException) { _store.Delete(StateKeys.Tasks); return null; }
        if (_clock.GetUtcNow() - capturedAt >= _maxAge)
        {
            _store.Delete(StateKeys.Tasks);
            return null;
        }
        return new CachedSnapshot<TaskItem>(doc.Tasks, capturedAt);
    }

    /// <summary>Persist <paramref name="tasks"/> as the cache for <paramref name="config"/>'s context,
    /// stamped with the current time, replacing any prior document.</summary>
    public void Save(AppConfig config, IReadOnlyList<TaskItem> tasks)
        => _store.Save(StateKeys.Tasks, new TaskCacheDocument
        {
            Key = KeyFor(config),
            CapturedAtMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Tasks = tasks,
        });

    /// <summary>Forget the cached working set (used by <c>--reset</c>). A no-op when nothing is cached.</summary>
    public void Clear() => _store.Delete(StateKeys.Tasks);

    /// <summary>
    /// The context fingerprint that determines the fetched working set: the workspace id, the Personal
    /// Tasks list id, and the (sorted) <c>Assignee IS</c> rule values that scope the assigned fetch
    /// server-side (#68). Sort/group and non-assignee filters are deliberately excluded — they only
    /// affect client-side rendering, not the set that is fetched — so the cache survives a pure view
    /// tweak between sessions. Pure and stable (order-independent in the assignee set).
    /// </summary>
    internal static string KeyFor(AppConfig config)
    {
        // AssigneeRuleValues dedupes case-insensitively but keeps each value's original casing, so
        // normalise before joining — otherwise "Ben" and "ben" (which resolve to the same server-side
        // fetch) would fingerprint differently and cause a false miss (lost instant paint, never a
        // wrong set). Consistent with the case-insensitive matching used at the fetch layer.
        var assignees = TaskService.AssigneeRuleValues(config.View)
            .Select(v => v.ToLowerInvariant())
            .OrderBy(v => v, StringComparer.Ordinal);
        return string.Join('|', new[] { config.WorkspaceId, config.PersonalTasksListId }.Concat(assignees));
    }
}
