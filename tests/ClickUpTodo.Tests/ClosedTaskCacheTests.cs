using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The warm closed-task cache's bounding (#253): the age window drops stale-by-<c>date_updated</c>
/// tasks (nulls kept), the count cap keeps the newest, and both surface a non-silent dropped count.
/// </summary>
public sealed class ClosedTaskCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    private static TaskItem Closed(string id, DateTimeOffset? updated)
        => new() { Id = id, Name = id, StatusType = "closed", UpdatedMs = updated?.ToUnixTimeMilliseconds() };

    // ── Bound (pure) ─────────────────────────────────────────────────────────

    [Fact]
    public void Bound_KeepsTasksWithinAgeWindow_DropsOlder()
    {
        var tasks = new[]
        {
            Closed("fresh", Now.AddDays(-1)),
            Closed("edge", Now.AddDays(-30)),   // exactly at the window — kept (inclusive)
            Closed("stale", Now.AddDays(-31)),  // just past — dropped
        };

        var (kept, dropped) = ClosedTaskCache.Bound(tasks, maxCount: 100, maxAge: TimeSpan.FromDays(30), now: Now);

        Assert.Equal(["fresh", "edge"], kept.Select(t => t.Id));
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void Bound_OrdersNewestFirst()
    {
        var tasks = new[]
        {
            Closed("old", Now.AddDays(-5)),
            Closed("new", Now.AddHours(-1)),
            Closed("mid", Now.AddDays(-2)),
        };

        var (kept, _) = ClosedTaskCache.Bound(tasks, maxCount: 100, maxAge: TimeSpan.FromDays(30), now: Now);

        Assert.Equal(["new", "mid", "old"], kept.Select(t => t.Id));
    }

    [Fact]
    public void Bound_CountCap_KeepsNewest_AndReportsDropped()
    {
        var tasks = new[]
        {
            Closed("a", Now.AddDays(-1)),
            Closed("b", Now.AddDays(-2)),
            Closed("c", Now.AddDays(-3)),
        };

        var (kept, dropped) = ClosedTaskCache.Bound(tasks, maxCount: 2, maxAge: TimeSpan.FromDays(30), now: Now);

        Assert.Equal(["a", "b"], kept.Select(t => t.Id));
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void Bound_NullUpdatedMs_NeverAgedOut_ButSortsLast()
    {
        var tasks = new[]
        {
            Closed("dated", Now.AddDays(-1)),
            Closed("undated", updated: null),
        };

        var (kept, dropped) = ClosedTaskCache.Bound(tasks, maxCount: 100, maxAge: TimeSpan.FromDays(30), now: Now);

        Assert.Equal(["dated", "undated"], kept.Select(t => t.Id));
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Bound_NullUpdatedMs_ShedFirstByCountCap()
    {
        var tasks = new[]
        {
            Closed("dated", Now.AddDays(-2)),
            Closed("undated", updated: null),
        };

        var (kept, dropped) = ClosedTaskCache.Bound(tasks, maxCount: 1, maxAge: TimeSpan.FromDays(30), now: Now);

        Assert.Equal(["dated"], kept.Select(t => t.Id));
        Assert.Equal(1, dropped);
    }

    // ── Update / Snapshot (stateful) ─────────────────────────────────────────

    [Fact]
    public void Update_StoresBoundedSet_AndReturnsDropped()
    {
        var cache = new ClosedTaskCache(new FakeClock(Now), maxCount: 2, maxAge: TimeSpan.FromDays(30));

        var dropped = cache.Update([
            Closed("a", Now.AddDays(-1)),
            Closed("b", Now.AddDays(-2)),
            Closed("c", Now.AddDays(-3)),
        ]);

        Assert.Equal(1, dropped);
        Assert.Equal(2, cache.Count);
        Assert.Equal(["a", "b"], cache.Snapshot.Select(t => t.Id));
    }

    [Fact]
    public void Update_Replaces_PreviousContents()
    {
        var cache = new ClosedTaskCache(new FakeClock(Now));

        cache.Update([Closed("a", Now.AddDays(-1))]);
        cache.Update([Closed("b", Now.AddDays(-1))]);

        Assert.Equal(["b"], cache.Snapshot.Select(t => t.Id));
    }

    [Fact]
    public void Snapshot_EmptyUntilFirstUpdate()
    {
        var cache = new ClosedTaskCache(new FakeClock(Now));

        Assert.Empty(cache.Snapshot);
        Assert.Equal(0, cache.Count);
    }
}
