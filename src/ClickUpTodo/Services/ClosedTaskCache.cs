using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// A warm, bounded, in-memory set of the user's recently-<c>closed</c> tasks, kept fresh on a slow
/// cadence off the refresh loop (#253, ideas #3/#4 from #191). Below the F12 <b>All</b> state the live
/// snapshot doesn't carry closed-type tasks, so cycling to All otherwise stalls on an on-demand
/// <c>include_closed=true</c> fetch; this cache lets that transition paint instantly (see
/// <c>TaskService.SupplementWithClosed</c>) while the authoritative refresh converges behind it.
/// <para>
/// The set is bounded two ways so it can't grow without limit: an <b>age window</b> on
/// <see cref="TaskItem.UpdatedMs"/> (closing a task bumps its <c>date_updated</c>, so this approximates
/// "closed within the window") and a <b>count cap</b> keeping the newest. Because it's only ever used as
/// a bridge paint that the following full fetch supersedes, staleness is self-correcting; the age window
/// is the TTL and no cross-restart persistence is needed. Thread-safe — written from the background
/// refresh loop, read on the UI thread.
/// </para>
/// </summary>
public sealed class ClosedTaskCache(TimeProvider? timeProvider = null, int maxCount = ClosedTaskCache.DefaultMaxCount, TimeSpan? maxAge = null)
{
    /// <summary>Default cap on the warm closed set — enough to make the All transition feel populated
    /// without holding an unbounded backlog of ancient closed tasks.</summary>
    public const int DefaultMaxCount = 500;

    /// <summary>Default age window: closed tasks whose <c>date_updated</c> is older than this are dropped.
    /// 30 days keeps the recently-completed set that a "show completed" glance actually cares about.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly int _maxCount = maxCount;
    private readonly TimeSpan _maxAge = maxAge ?? DefaultMaxAge;
    private readonly object _gate = new();
    private IReadOnlyList<TaskItem> _closed = [];

    /// <summary>The current warm closed set, newest first. Empty until the first prefetch completes.</summary>
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
    /// omitted" note rather than truncating silently.</summary>
    public int Update(IReadOnlyList<TaskItem> closed)
    {
        var (kept, dropped) = Bound(closed, _maxCount, _maxAge, _time.GetUtcNow());
        lock (_gate)
            _closed = kept;
        return dropped;
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
