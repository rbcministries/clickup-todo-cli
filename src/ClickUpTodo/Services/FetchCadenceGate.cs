namespace ClickUpTodo.Services;

/// <summary>
/// Tracks when each named fetch group last completed, so the refresh loop can carry slow-cadence
/// work ("due every ~30 min") by piggybacking on its existing cycles instead of growing a second
/// timer or a scheduler (#246 ADR: per-group minimum-age due-gating on the single loop).
/// <para>
/// Cadences are <b>minimum ages, not exact periods</b>: <see cref="IsDue"/> answers "has at least
/// <c>minAge</c> passed since the group last completed?", so a group runs on the first refresh
/// cycle at or after its age — quantized to the loop's own interval. Stamp <see cref="MarkRan"/>
/// at <b>completion</b>, not start: a multi-cycle run (e.g. a walk spread across cycles, #236)
/// then stays due until it finishes, and runs missed while one is in flight collapse into it
/// rather than queueing. A group that has never completed is immediately due.
/// </para>
/// Thread-safe; time comes from the injected <see cref="TimeProvider"/> so tests drive the clock.
/// </summary>
public sealed class FetchCadenceGate(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _lastCompleted = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>True when <paramref name="group"/> has never completed, or completed at least
    /// <paramref name="minAge"/> ago.</summary>
    public bool IsDue(string group, TimeSpan minAge)
    {
        lock (_gate)
            return !_lastCompleted.TryGetValue(group, out var last)
                || _time.GetUtcNow() - last >= minAge;
    }

    /// <summary>Records that <paramref name="group"/> just completed a run; it stops being due
    /// until its minimum age passes again.</summary>
    public void MarkRan(string group)
    {
        lock (_gate)
            _lastCompleted[group] = _time.GetUtcNow();
    }
}
