using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The persisted warm-closed-set document written under <see cref="StateKeys.Closed"/> (#280). Mirrors
/// <see cref="AssigneeFrequencyDocument"/>: a <see cref="SchemaVersion"/> guarding against an
/// incompatible future shape, the context <see cref="Key"/> the set was captured under (a mismatch on
/// load is a clean miss, so a workspace/list/assignee switch never bridge-paints a foreign closed set),
/// and the bounded <see cref="Tasks"/> payload. No capture timestamp is stored: the per-task age window
/// on <see cref="TaskItem.UpdatedMs"/> is the staleness bound and is re-applied on load against the new
/// launch time, so a set from a long-untouched workspace self-prunes rather than needing a whole-doc TTL.
/// </summary>
public sealed record ClosedTaskCacheDocument
{
    /// <summary>The persisted-shape version; bump when this document changes incompatibly so an old
    /// document is discarded rather than mis-read.</summary>
    public int SchemaVersion { get; init; } = ClosedTaskCache.CurrentSchemaVersion;

    /// <summary>The workspace/list/assignee fingerprint (<see cref="TaskCache.KeyFor"/>) the closed set
    /// was fetched under. A load only trusts the payload when this matches the current context.</summary>
    public required string Key { get; init; }

    /// <summary>The bounded warm closed set, newest first, as last persisted.</summary>
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
}

/// <summary>
/// A warm, bounded set of the user's recently-<c>closed</c> tasks, kept fresh on a slow
/// cadence off the refresh loop (#253, ideas #3/#4 from #191). Below the F12 <b>All</b> state the live
/// snapshot doesn't carry closed-type tasks, so cycling to All otherwise stalls on an on-demand
/// <c>include_closed=true</c> fetch; this cache lets that transition paint instantly (see
/// <c>TaskService.SupplementWithClosed</c>) while the authoritative refresh converges behind it.
/// <para>
/// The set is bounded two ways so it can't grow without limit: an <b>age window</b> on
/// <see cref="TaskItem.UpdatedMs"/> (closing a task bumps its <c>date_updated</c>, so this approximates
/// "closed within the window") and a <b>count cap</b> keeping the newest. Because it's only ever used as
/// a bridge paint that the following full fetch supersedes, staleness is self-correcting; the age window
/// is the TTL. Thread-safe — written from the background refresh loop, read on the UI thread.
/// </para>
/// <para>
/// When constructed with an <see cref="IStateStore"/> and a context-key provider (#280) it also
/// <b>persists across restarts</b> — the bounded set is warmed from <see cref="StateKeys.Closed"/> on
/// construction (with the age window re-applied against the launch time) and re-written on every
/// <see cref="Update"/>, so the very first F12→All after a fresh launch is instant too instead of
/// stalling one poll interval until the background prefetch warms an empty in-memory set. Persistence
/// is best-effort: a failed load or save is a clean miss, never a crash or a broken refresh loop.
/// </para>
/// </summary>
public sealed class ClosedTaskCache
{
    /// <summary>Default cap on the warm closed set — enough to make the All transition feel populated
    /// without holding an unbounded backlog of ancient closed tasks.</summary>
    public const int DefaultMaxCount = 500;

    /// <summary>The current <see cref="ClosedTaskCacheDocument.SchemaVersion"/>; bump if the persisted
    /// shape changes incompatibly so an old document is discarded rather than deserialised into garbage.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Default age window: closed tasks whose <c>date_updated</c> is older than this are dropped.
    /// 30 days keeps the recently-completed set that a "show completed" glance actually cares about.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private readonly TimeProvider _time;
    private readonly int _maxCount;
    private readonly TimeSpan _maxAge;
    private readonly IStateStore? _store;
    private readonly Func<string>? _contextKey;
    private readonly object _gate = new();
    private IReadOnlyList<TaskItem> _closed = [];

    /// <param name="timeProvider">Clock for the age-window comparison (defaults to the system clock).</param>
    /// <param name="maxCount">Count cap on the held set (newest kept).</param>
    /// <param name="maxAge">Age window on <see cref="TaskItem.UpdatedMs"/> beyond which a task is dropped.</param>
    /// <param name="store">Optional persistence backend. When supplied together with
    /// <paramref name="contextKey"/>, the bounded set is warmed from <see cref="StateKeys.Closed"/> on
    /// construction and re-persisted on every <see cref="Update"/>. Omit for a purely in-memory cache.</param>
    /// <param name="contextKey">Provider of the current fetch-scope fingerprint (workspace/list/assignee,
    /// via <see cref="TaskCache.KeyFor"/>). Called live so a mid-session context switch is reflected; a
    /// persisted document whose key differs is a clean miss. Required for persistence.</param>
    public ClosedTaskCache(
        TimeProvider? timeProvider = null,
        int maxCount = DefaultMaxCount,
        TimeSpan? maxAge = null,
        IStateStore? store = null,
        Func<string>? contextKey = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _maxCount = maxCount;
        _maxAge = maxAge ?? DefaultMaxAge;
        _store = store;
        _contextKey = contextKey;
        Load();
    }

    /// <summary>Whether cross-restart persistence is active (both a store and a context-key provider
    /// were supplied).</summary>
    private bool Persistent => _store is not null && _contextKey is not null;

    /// <summary>The current warm closed set, newest first. Empty until the first prefetch completes
    /// (or, when persistent, until warmed from the store on construction).</summary>
    public IReadOnlyList<TaskItem> Snapshot
    {
        get { lock (_gate) return _closed; }
    }

    /// <summary>Count of tasks currently held.</summary>
    public int Count
    {
        get { lock (_gate) return _closed.Count; }
    }

    /// <summary>Bounds <paramref name="closed"/> and replaces the held set with it, returning how many
    /// tasks the bounds dropped (0 when everything fit) so the caller can surface a "some completed
    /// omitted" note rather than truncating silently. When persistent, the bounded set is also written
    /// back to the store (best-effort).</summary>
    public int Update(IReadOnlyList<TaskItem> closed)
    {
        var (kept, dropped) = Bound(closed, _maxCount, _maxAge, _time.GetUtcNow());
        lock (_gate)
        {
            _closed = kept;
            Persist(kept);
        }
        return dropped;
    }

    // Warm the in-memory set from the persisted document on construction. A missing document, a
    // different context, an incompatible schema, or a corrupt payload all mean "no warm set" — start
    // empty rather than surface a foreign/garbled set (this runs synchronously at startup, so a throw
    // here would brick every launch until the file was hand-deleted). The age window is re-applied
    // against the current launch time so a persisted set from a long-untouched workspace self-prunes.
    private void Load()
    {
        if (!Persistent)
            return;

        ClosedTaskCacheDocument? doc;
        try
        {
            doc = _store!.Load<ClosedTaskCacheDocument>(StateKeys.Closed);
        }
        catch (JsonException)
        {
            return;
        }
        if (doc is null
            || doc.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(doc.Key, _contextKey!(), StringComparison.Ordinal))
            return;

        var (kept, _) = Bound(doc.Tasks, _maxCount, _maxAge, _time.GetUtcNow());
        _closed = kept;
    }

    // Caller holds _gate. Serialising the write under the lock satisfies IStateStore's "caller must
    // serialise concurrent access to a key" contract. Best-effort: a failed write (read-only / full
    // disk) must never break the background refresh loop that drives Update — the set lives on in memory
    // and the next prefetch retries.
    private void Persist(IReadOnlyList<TaskItem> kept)
    {
        if (!Persistent)
            return;
        try
        {
            _store!.Save(StateKeys.Closed, new ClosedTaskCacheDocument
            {
                Key = _contextKey!(),
                Tasks = kept,
            });
        }
        catch
        {
            // Swallowed — persistence is a warm-cache optimisation, not a correctness requirement.
        }
    }

    /// <summary>
    /// Pure bounding: drop tasks whose <see cref="TaskItem.UpdatedMs"/> is older than
    /// <paramref name="maxAge"/> before <paramref name="now"/> (a null <c>UpdatedMs</c> is never aged out
    /// — we can't prove it's stale), order the survivors newest-<c>UpdatedMs</c>-first (nulls last), then
    /// keep at most <paramref name="maxCount"/>. Returns the kept set and the number dropped by either
    /// bound. Unit-testable.
    /// </summary>
    public static (IReadOnlyList<TaskItem> Kept, int Dropped) Bound(
        IEnumerable<TaskItem> closed, int maxCount, TimeSpan maxAge, DateTimeOffset now)
    {
        var all = closed as IReadOnlyCollection<TaskItem> ?? closed.ToList();
        var total = all.Count;
        var cutoffMs = now.Add(-maxAge).ToUnixTimeMilliseconds();

        var withinWindow = all
            .Where(t => t.UpdatedMs is not { } ms || ms >= cutoffMs)
            // Newest first; a null UpdatedMs sorts last so the count cap sheds the least-dated tasks first.
            .OrderByDescending(t => t.UpdatedMs ?? long.MinValue)
            .ToList();

        var kept = withinWindow.Count > maxCount
            ? withinWindow.Take(maxCount).ToList()
            : withinWindow;

        return (kept, total - kept.Count);
    }
}
