using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the persistent status cache (#125): the warm-from-store round-trip, that the TTL still governs
/// a persisted entry (nothing stale served past expiry), and the workspace / schema / corrupt guards.
/// Uses a real temp-dir <see cref="JsonFileStateStore"/> so the JSON round-trip of the status options is
/// exercised end-to-end (mirrors <see cref="TaskCacheTests"/>).
/// </summary>
public sealed class StatusCachePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private JsonFileStateStore Store() => new(_dir);

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
    public async Task Persists_ThenWarmsSecondInstance_WithoutRefetch()
    {
        var store = Store();
        var clock = NewClock();
        var firstCalls = 0;
        var first = new StatusCache(
            (_, _) => { firstCalls++; return Task.FromResult(Statuses("to do", "done")); },
            clock, store: store, workspaceId: "ws1");
        await first.GetAsync("list-1");
        Assert.Equal(1, firstCalls);

        // A brand-new instance over the same store warms from disk — the value is fresh without a fetch.
        var secondCalls = 0;
        var second = new StatusCache(
            (_, _) => { secondCalls++; return Task.FromResult(Statuses("stale-if-fetched")); },
            clock, store: store, workspaceId: "ws1");

        Assert.True(second.TryGetFresh("list-1", out var warmed));
        Assert.Equal(["to do", "done"], warmed.Select(s => s.Name));
        var served = await second.GetAsync("list-1");
        Assert.Equal(0, secondCalls); // served from the warmed cache, never refetched
        Assert.Equal(["to do", "done"], served.Select(s => s.Name));
    }

    [Fact]
    public async Task PersistedEntry_PastTtl_IsStale_AndRefetched()
    {
        var store = Store();
        var clock = NewClock();
        var ttl = TimeSpan.FromMinutes(10);
        var first = new StatusCache(
            (_, _) => Task.FromResult(Statuses("v1")), clock, ttl, store, "ws1");
        await first.GetAsync("list-1");

        // Time passes beyond the TTL, then a fresh instance warms the (now-expired) persisted entry.
        clock.Advance(TimeSpan.FromMinutes(11));
        var refetchCalls = 0;
        var second = new StatusCache(
            (_, _) => { refetchCalls++; return Task.FromResult(Statuses("v2")); }, clock, ttl, store, "ws1");

        Assert.False(second.TryGetFresh("list-1", out _)); // persisted timestamp preserved ⇒ still stale
        var refreshed = await second.GetAsync("list-1");
        Assert.Equal(1, refetchCalls);
        Assert.Equal(["v2"], refreshed.Select(s => s.Name));
    }

    [Fact]
    public async Task WorkspaceMismatch_DoesNotWarm()
    {
        var store = Store();
        var clock = NewClock();
        var first = new StatusCache((_, _) => Task.FromResult(Statuses("to do")), clock, store: store, workspaceId: "ws1");
        await first.GetAsync("list-1");

        var other = new StatusCache((_, _) => Task.FromResult(Statuses("x")), clock, store: store, workspaceId: "ws2");
        Assert.False(other.TryGetFresh("list-1", out _));
    }

    [Fact]
    public void SchemaVersionMismatch_DoesNotWarm()
    {
        var store = Store();
        store.Save(StateKeys.Statuses, new StatusCacheDocument(
            StatusCache.CurrentSchemaVersion + 1,
            "ws1",
            [new StatusCacheEntryDto("list-1", Statuses("to do"), 0)]));

        var cache = new StatusCache((_, _) => Task.FromResult(Statuses("x")), NewClock(), store: store, workspaceId: "ws1");
        Assert.False(cache.TryGetFresh("list-1", out _));
    }

    [Fact]
    public void CorruptDocument_IsTreatedAsMiss_NotThrow()
    {
        // A truncated statuses.json (quit/crash mid-write) must degrade to a miss, not throw — the warm-up
        // runs synchronously before the UI loop, so a throw would brick every launch.
        var store = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor(StateKeys.Statuses), "{ \"entries\": [ {\"listId\": \"list-1\"");

        var cache = new StatusCache((_, _) => Task.FromResult(Statuses("x")), NewClock(), store: store, workspaceId: "ws1");
        Assert.False(cache.TryGetFresh("list-1", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
