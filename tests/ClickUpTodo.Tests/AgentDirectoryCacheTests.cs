using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The local agent registry (#494): the config seed served when discovery is unavailable, the TTL'd /
/// evictable discovered layer, seed-wins precedence, and persistence of the discovered layer across
/// restarts. The discovery <em>source</em> is #493's deferred work; here it's a fake so the caching,
/// staleness, refresh and eviction behaviour can be pinned without the network.
/// </summary>
public sealed class AgentDirectoryCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A discovery source whose response is a function of the call number, so a test can vary
    /// (or throw) per call.</summary>
    private sealed class FuncSource(Func<int, IReadOnlyList<AgentDirectoryEntry>> fn) : IAgentDiscoverySource
    {
        public int Calls;
        public Task<IReadOnlyList<AgentDirectoryEntry>> DiscoverAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult(fn(++Calls));
    }

    private static FuncSource Source(params AgentDirectoryEntry[] entries) => new(_ => entries);

    private static AgentSeedEntry Seed(long id, string name, string? purpose = null)
        => new() { Id = id, Name = name, Purpose = purpose };

    private static AgentDirectoryEntry Discovered(long id, string name, string? purpose = null)
        => new(id, name, purpose, AgentEntrySource.Discovered);

    // ---- seed ----------------------------------------------------------------------------------------

    [Fact]
    public void Seed_ServedWithoutDiscovery()
    {
        var cache = new AgentDirectoryCache([Seed(-1, "Alpha"), Seed(-2, "Bravo")]);

        Assert.Equal([-1, -2], cache.Entries.Select(e => e.Id));
        Assert.All(cache.Entries, e => Assert.Equal(AgentEntrySource.Seeded, e.Source));
        Assert.False(cache.NeedsRefresh); // no discovery source ⇒ nothing to refresh
    }

    [Fact]
    public void Seed_DropsNonAgentAndBlankEntries()
    {
        var cache = new AgentDirectoryCache([
            Seed(-1, "Alpha"),
            Seed(5, "A human"),   // positive id ⇒ not an agent
            Seed(0, "Zero"),      // zero ⇒ not an agent
            Seed(-2, "   "),      // blank name
        ]);

        Assert.Equal([-1], cache.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Seed_DedupsById_FirstWins()
    {
        var cache = new AgentDirectoryCache([Seed(-1, "First"), Seed(-1, "Second")]);

        var only = Assert.Single(cache.Entries);
        Assert.Equal("First", only.Name);
    }

    [Fact]
    public void Seed_NullOrEmpty_IsEmptyRegistry()
    {
        Assert.Empty(new AgentDirectoryCache(null).Entries);
        Assert.Empty(new AgentDirectoryCache([]).Entries);
    }

    // ---- refresh / discovery -------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_NoSource_IsNoOp()
    {
        var cache = new AgentDirectoryCache([Seed(-1, "Alpha")]);
        await cache.RefreshAsync();
        Assert.Equal([-1], cache.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Refresh_PopulatesDiscoveredLayer()
    {
        var cache = new AgentDirectoryCache(discovery: Source(Discovered(-3, "Charlie"), Discovered(-4, "Delta")), timeProvider: NewClock());
        Assert.True(cache.NeedsRefresh); // source present, nothing fetched yet

        await cache.RefreshAsync();

        // Discovered entries are served ordered by name: "Charlie" (-3) then "Delta" (-4).
        Assert.Equal([-3, -4], cache.Entries.Select(e => e.Id));
        Assert.Equal(["Charlie", "Delta"], cache.Entries.Select(e => e.Name));
        Assert.All(cache.Entries, e => Assert.Equal(AgentEntrySource.Discovered, e.Source));
        Assert.False(cache.NeedsRefresh);
    }

    [Fact]
    public async Task Refresh_SeedWinsOnIdCollision()
    {
        var cache = new AgentDirectoryCache(
            seed: [Seed(-1, "Pinned")],
            discovery: Source(Discovered(-1, "Discovered"), Discovered(-2, "Other")),
            timeProvider: NewClock());

        await cache.RefreshAsync();

        var pinned = cache.Entries.Single(e => e.Id == -1);
        Assert.Equal("Pinned", pinned.Name);
        Assert.Equal(AgentEntrySource.Seeded, pinned.Source);
        Assert.Equal(new[] { -1L, -2L }.OrderBy(x => x), cache.Entries.Select(e => e.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Refresh_DropsInvalidDiscoveredEntries()
    {
        var cache = new AgentDirectoryCache(
            discovery: Source(Discovered(-3, "Charlie"), Discovered(5, "Human"), Discovered(-4, "")),
            timeProvider: NewClock());

        await cache.RefreshAsync();

        Assert.Equal([-3], cache.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Discovered_ExpiresAfterTtl_ThenNeedsRefresh()
    {
        var clock = NewClock();
        var cache = new AgentDirectoryCache(
            seed: [Seed(-1, "Alpha")],
            discovery: Source(Discovered(-3, "Charlie")),
            timeProvider: clock,
            ttl: TimeSpan.FromHours(12));

        await cache.RefreshAsync();
        Assert.Contains(cache.Entries, e => e.Id == -3);
        Assert.False(cache.NeedsRefresh);

        clock.Advance(TimeSpan.FromHours(13)); // past the 12h TTL

        Assert.DoesNotContain(cache.Entries, e => e.Id == -3); // stale discovered excluded
        Assert.Equal([-1], cache.Entries.Select(e => e.Id));   // seed still served
        Assert.True(cache.NeedsRefresh);                       // stale ⇒ a refresh would help
    }

    [Fact]
    public async Task Refresh_SourceThrows_LeavesExistingLayerIntact()
    {
        var source = new FuncSource(n => n == 1
            ? [Discovered(-3, "Charlie")]
            : throw new InvalidOperationException("boom"));
        var cache = new AgentDirectoryCache(discovery: source, timeProvider: NewClock());

        await cache.RefreshAsync();
        Assert.Contains(cache.Entries, e => e.Id == -3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.RefreshAsync());
        Assert.Contains(cache.Entries, e => e.Id == -3); // untouched by the failed refresh
    }

    // ---- eviction ------------------------------------------------------------------------------------

    [Fact]
    public async Task Evict_RemovesDiscovered_ReturnsTrue()
    {
        var cache = new AgentDirectoryCache(discovery: Source(Discovered(-3, "Charlie")), timeProvider: NewClock());
        await cache.RefreshAsync();

        Assert.True(cache.Evict(-3));
        Assert.DoesNotContain(cache.Entries, e => e.Id == -3);
        Assert.False(cache.Evict(-3)); // already gone
    }

    [Fact]
    public void Evict_SeededId_DoesNotUnpin()
    {
        var cache = new AgentDirectoryCache([Seed(-1, "Alpha")]);

        Assert.False(cache.Evict(-1));                 // a seed is not in the discovered layer
        Assert.Contains(cache.Entries, e => e.Id == -1); // still served
    }

    // ---- lookups -------------------------------------------------------------------------------------

    [Fact]
    public async Task Find_ById_SeedWins_StaleExcluded()
    {
        var clock = NewClock();
        var cache = new AgentDirectoryCache(
            seed: [Seed(-1, "Pinned")],
            discovery: Source(Discovered(-1, "Discovered"), Discovered(-2, "Other")),
            timeProvider: clock,
            ttl: TimeSpan.FromHours(12));
        await cache.RefreshAsync();

        Assert.Equal("Pinned", cache.Find(-1)!.Name); // seed wins
        Assert.Equal(-2, cache.Find(-2)!.Id);
        Assert.Null(cache.Find(-999));                // unknown

        clock.Advance(TimeSpan.FromHours(13));
        Assert.Null(cache.Find(-2));                  // stale discovered no longer resolves
        Assert.Equal("Pinned", cache.Find(-1)!.Name); // seed never goes stale
    }

    [Fact]
    public void FindByName_CaseInsensitive_SeedFirst()
    {
        var cache = new AgentDirectoryCache([Seed(-1, "Recap Rio")]);

        Assert.Equal(-1, cache.FindByName("recap rio")!.Id);
        Assert.Equal(-1, cache.FindByName("  Recap Rio  ")!.Id);
        Assert.Null(cache.FindByName("nobody"));
        Assert.Null(cache.FindByName("  "));
    }

    // ---- persistence ---------------------------------------------------------------------------------

    [Fact]
    public async Task Discovered_PersistsAcrossInstances()
    {
        var clock = NewClock();
        var first = new AgentDirectoryCache(
            discovery: Source(Discovered(-3, "Charlie", "recaps")),
            timeProvider: clock,
            store: new JsonFileStateStore(_dir),
            workspaceId: "ws1");
        await first.RefreshAsync();

        // A fresh instance over the same store warms the discovered layer without re-fetching.
        var second = new AgentDirectoryCache(
            timeProvider: clock,
            store: new JsonFileStateStore(_dir),
            workspaceId: "ws1");

        var entry = Assert.Single(second.Entries);
        Assert.Equal(-3, entry.Id);
        Assert.Equal("Charlie", entry.Name);
        Assert.Equal("recaps", entry.Purpose);
        Assert.Equal(AgentEntrySource.Discovered, entry.Source);
    }

    [Fact]
    public async Task Persistence_WorkspaceMismatch_IsCleanMiss()
    {
        var store = new JsonFileStateStore(_dir);
        var first = new AgentDirectoryCache(discovery: Source(Discovered(-3, "Charlie")), timeProvider: NewClock(), store: store, workspaceId: "ws1");
        await first.RefreshAsync();

        var other = new AgentDirectoryCache(timeProvider: NewClock(), store: store, workspaceId: "ws2");
        Assert.Empty(other.Entries); // a foreign workspace's agents never warm
    }

    [Fact]
    public async Task Persistence_SchemaMismatch_IsIgnored()
    {
        var store = new JsonFileStateStore(_dir);
        await new AgentDirectoryCache(discovery: Source(Discovered(-3, "Charlie")), timeProvider: NewClock(), store: store, workspaceId: "ws1")
            .RefreshAsync();

        // Overwrite with a future schema version; it must be discarded, not mis-read.
        store.Save(StateKeys.AgentDirectories, new AgentDirectoryDocument(
            AgentDirectoryCache.CurrentSchemaVersion + 1, "ws1",
            [new AgentDirectoryEntryDto(-3, "Charlie", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds())]));

        Assert.Empty(new AgentDirectoryCache(timeProvider: NewClock(), store: store, workspaceId: "ws1").Entries);
    }

    [Fact]
    public void Persistence_StaleEntry_DroppedOnLoad()
    {
        var clock = NewClock();
        var store = new JsonFileStateStore(_dir);
        // A persisted entry captured 13h ago; the 12h TTL makes it stale on load.
        var capturedAt = clock.GetUtcNow() - TimeSpan.FromHours(13);
        store.Save(StateKeys.AgentDirectories, new AgentDirectoryDocument(
            AgentDirectoryCache.CurrentSchemaVersion, "ws1",
            [new AgentDirectoryEntryDto(-3, "Charlie", null, capturedAt.ToUnixTimeMilliseconds())]));

        var cache = new AgentDirectoryCache(timeProvider: clock, ttl: TimeSpan.FromHours(12), store: store, workspaceId: "ws1");
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public void Persistence_CorruptFile_IsSwallowed_SeedStillServed()
    {
        var store = new JsonFileStateStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor(StateKeys.AgentDirectories), "{ not valid json");

        // Construction must not throw; the seed is unaffected by a corrupt discovered-layer snapshot.
        var cache = new AgentDirectoryCache([Seed(-1, "Alpha")], store: store, workspaceId: "ws1");
        Assert.Equal([-1], cache.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Evict_Persists_SoNextInstanceSeesRemoval()
    {
        var clock = NewClock();
        var store = new JsonFileStateStore(_dir);
        var cache = new AgentDirectoryCache(
            discovery: Source(Discovered(-3, "Charlie"), Discovered(-4, "Delta")),
            timeProvider: clock, store: store, workspaceId: "ws1");
        await cache.RefreshAsync();

        Assert.True(cache.Evict(-3));

        var reloaded = new AgentDirectoryCache(timeProvider: clock, store: store, workspaceId: "ws1");
        Assert.Equal([-4], reloaded.Entries.Select(e => e.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
