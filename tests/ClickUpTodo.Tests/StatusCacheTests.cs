using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

public sealed class StatusCacheTests
{
    private static IReadOnlyList<StatusOption> Statuses(params string[] names)
        => names.Select(n => new StatusOption(n, null)).ToList();

    /// <summary>A TimeProvider whose clock only advances when the test moves it.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task GetAsync_CachesAfterFirstFetch()
    {
        var calls = 0;
        var cache = new StatusCache((_, _) => { calls++; return Task.FromResult(Statuses("to do", "done")); }, NewClock());

        var first = await cache.GetAsync("list-1");
        var second = await cache.GetAsync("list-1");

        Assert.Equal(1, calls);
        Assert.Equal(["to do", "done"], second.Select(s => s.Name));
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetAsync_RefetchesAfterTtlExpires()
    {
        var calls = 0;
        var clock = NewClock();
        var cache = new StatusCache(
            (_, _) => { calls++; return Task.FromResult(Statuses($"v{calls}")); },
            clock,
            ttl: TimeSpan.FromMinutes(10));

        await cache.GetAsync("list-1");
        clock.Advance(TimeSpan.FromMinutes(9));
        await cache.GetAsync("list-1"); // still fresh
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(2)); // now 11 min > 10 min TTL
        var refreshed = await cache.GetAsync("list-1");

        Assert.Equal(2, calls);
        Assert.Equal(["v2"], refreshed.Select(s => s.Name));
    }

    [Fact]
    public async Task TryGetFresh_FalseBeforeFetch_TrueAfter_FalseWhenStale()
    {
        var clock = NewClock();
        var cache = new StatusCache((_, _) => Task.FromResult(Statuses("to do")), clock, ttl: TimeSpan.FromMinutes(10));

        Assert.False(cache.TryGetFresh("list-1", out _));

        await cache.GetAsync("list-1");
        Assert.True(cache.TryGetFresh("list-1", out var fresh));
        Assert.Equal(["to do"], fresh.Select(s => s.Name));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(cache.TryGetFresh("list-1", out _));
    }

    [Fact]
    public async Task GetAsync_DeDupesConcurrentFetchesForSameList()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<IReadOnlyList<StatusOption>>();
        var cache = new StatusCache((_, _) => { Interlocked.Increment(ref calls); return gate.Task; }, NewClock());

        var a = cache.GetAsync("list-1");
        var b = cache.GetAsync("list-1");
        // Both callers await the single in-flight fetch.
        gate.SetResult(Statuses("done"));
        var resultA = await a;
        var resultB = await b;

        Assert.Equal(1, calls);
        Assert.Equal(["done"], resultA.Select(s => s.Name));
        Assert.Same(resultA, resultB);
    }

    [Fact]
    public async Task GetAsync_FailedFetch_IsNotCached_AndRetries()
    {
        var calls = 0;
        var cache = new StatusCache(
            (_, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<IReadOnlyList<StatusOption>>(new InvalidOperationException("boom"))
                    : Task.FromResult(Statuses("to do"));
            },
            NewClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("list-1"));
        Assert.False(cache.TryGetFresh("list-1", out _)); // nothing cached after a failure

        var retry = await cache.GetAsync("list-1");
        Assert.Equal(2, calls);
        Assert.Equal(["to do"], retry.Select(s => s.Name));
    }

    [Fact]
    public async Task PrefetchAsync_WarmsMissingLists_WithoutLaterFetch()
    {
        var calls = 0;
        var cache = new StatusCache((listId, _) => { calls++; return Task.FromResult(Statuses($"status-{listId}")); }, NewClock());

        await cache.PrefetchAsync(["a", "b", "a", "", "  "]); // dups and blanks ignored

        Assert.Equal(2, calls);
        Assert.True(cache.TryGetFresh("a", out _));
        Assert.True(cache.TryGetFresh("b", out _));

        await cache.GetAsync("a"); // served from the warmed cache
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PrefetchAsync_SwallowsPerListFailures()
    {
        var cache = new StatusCache(
            (listId, _) => listId == "bad"
                ? Task.FromException<IReadOnlyList<StatusOption>>(new InvalidOperationException("boom"))
                : Task.FromResult(Statuses("ok")),
            NewClock());

        await cache.PrefetchAsync(["good", "bad"]); // must not throw

        Assert.True(cache.TryGetFresh("good", out _));
        Assert.False(cache.TryGetFresh("bad", out _)); // failed list cached nothing
    }
}
