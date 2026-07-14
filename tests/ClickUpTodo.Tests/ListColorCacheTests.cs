using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the persistent per-list color cache (#125): the warm-from-store round-trip (including a
/// resolved "no color" null), cross-session merge/accumulation, the color-TTL expiry applied on load,
/// the workspace / schema / corrupt guards, and the no-store in-memory fallback. Uses a real temp-dir
/// <see cref="JsonFileStateStore"/> so the JSON round-trip is exercised end-to-end.
/// </summary>
public sealed class ListColorCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private JsonFileStateStore Store() => new(_dir);

    /// <summary>A TimeProvider whose clock only advances when the test moves it.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static Dictionary<string, string?> Colors(params (string id, string? color)[] items)
        => items.ToDictionary(i => i.id, i => i.color, StringComparer.Ordinal);

    [Fact]
    public void RoundTrips_IncludingResolvedNullColor()
    {
        var store = Store();
        var first = new ListColorCache(store, "ws1", NewClock());
        first.Save(Colors(("L1", "#112233"), ("L2", null)));

        var second = new ListColorCache(store, "ws1", NewClock());
        var snapshot = second.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("#112233", snapshot["L1"]);
        Assert.Null(snapshot["L2"]);
        Assert.True(second.Contains("L2")); // a resolved "no color" still counts as cached (not refetched)
        Assert.False(second.Contains("L3"));
    }

    [Fact]
    public void WorkspaceMismatch_IsMiss()
    {
        var store = Store();
        new ListColorCache(store, "ws1", NewClock()).Save(Colors(("L1", "#111111")));

        var other = new ListColorCache(store, "ws2", NewClock());
        Assert.Empty(other.Snapshot());
        Assert.False(other.Contains("L1"));
    }

    [Fact]
    public void SchemaVersionMismatch_IsMiss()
    {
        var store = Store();
        store.Save(StateKeys.ListColors, new ListColorDocument(
            ListColorCache.CurrentSchemaVersion + 1, "ws1", [new ListColorEntry("L1", "#111111", 0)]));

        Assert.Empty(new ListColorCache(store, "ws1", NewClock()).Snapshot());
    }

    [Fact]
    public void CorruptDocument_IsEmpty_NotThrow()
    {
        var store = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor(StateKeys.ListColors), "{ \"entries\": [ {\"listId\": \"L1\"");

        Assert.Empty(new ListColorCache(store, "ws1", NewClock()).Snapshot());
    }

    [Fact]
    public void StaleEntry_IsDroppedOnLoad()
    {
        var store = Store();
        var clock = NewClock();
        var ttl = TimeSpan.FromMinutes(10);
        new ListColorCache(store, "ws1", clock, ttl).Save(Colors(("L1", "#111111")));

        clock.Advance(TimeSpan.FromMinutes(11)); // beyond the color TTL
        var warmed = new ListColorCache(store, "ws1", clock, ttl);

        Assert.Empty(warmed.Snapshot());      // stale entry dropped, not warmed
        Assert.False(warmed.Contains("L1"));  // ⇒ refetched on demand
    }

    [Fact]
    public void Save_MergesAcrossSessions()
    {
        var store = Store();
        new ListColorCache(store, "ws1", NewClock()).Save(Colors(("L1", "#111111")));

        var second = new ListColorCache(store, "ws1", NewClock()); // warms L1
        second.Save(Colors(("L2", "#222222")));                    // adds L2, keeping L1

        var third = new ListColorCache(store, "ws1", NewClock()).Snapshot();
        Assert.Equal(2, third.Count);
        Assert.Equal("#111111", third["L1"]);
        Assert.Equal("#222222", third["L2"]);
    }

    [Fact]
    public void Save_Empty_DoesNotThrow_NorPersist()
    {
        var store = Store();
        var cache = new ListColorCache(store, "ws1", NewClock());
        cache.Save(Colors()); // no-op

        Assert.False(store.Exists(StateKeys.ListColors));
        Assert.Empty(cache.Snapshot());
    }

    [Fact]
    public void NoStore_WorksInMemory_WithoutPersisting()
    {
        // The storeless variant is the in-memory cache TaskService uses in tests / when no store is wired.
        var cache = new ListColorCache();
        cache.Save(Colors(("L1", "#111111"), ("L2", null)));

        Assert.True(cache.Contains("L1"));
        Assert.True(cache.Contains("L2"));
        Assert.Equal("#111111", cache.Snapshot()["L1"]);

        // A fresh storeless instance shares nothing — there was no persistence.
        Assert.Empty(new ListColorCache().Snapshot());
    }

    [Fact]
    public void OutOfRangeTimestamp_IsSkipped_NotThrow()
    {
        // A tampered document with a nonsense timestamp must not crash the constructor (warm-up runs
        // before the UI loop). The bad entry is skipped, not warmed.
        var store = Store();
        store.Save(StateKeys.ListColors, new ListColorDocument(
            ListColorCache.CurrentSchemaVersion, "ws1", [new ListColorEntry("L1", "#111111", long.MaxValue)]));

        var cache = new ListColorCache(store, "ws1", NewClock());
        Assert.Empty(cache.Snapshot());
        Assert.False(cache.Contains("L1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
