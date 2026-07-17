using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The stateful list-frequency cache (#238) — <see cref="ListFrequencyCache"/>: persistence through
/// <see cref="IStateStore"/> keyed per workspace, and the scheduled-walk (#236) seed feed. The pure
/// ranking rules are covered by <see cref="ListFrequencyTests"/>. Mirrors
/// <see cref="AssigneeFrequencyCacheTests"/>, but the cache owns no fetch delegate — the long tail is
/// pushed in via <see cref="ListFrequencyCache.SeedLists"/>.
/// </summary>
public sealed class ListFrequencyCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private static TaskItem MakeTask(string id, string? listId, string? listName) => new()
    {
        Id = id,
        Name = $"Task {id}",
        ListId = listId,
        ListName = listName,
    };

    [Fact]
    public void RecordFromTasks_ThenQuery_RanksByOccurrence()
    {
        var cache = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");

        cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha"), MakeTask("t2", "L1", "Alpha"), MakeTask("t3", "L2", "Beta")]);

        Assert.Equal(["L1", "L2"], cache.TopMostFrequent(10).Select(l => l.Id));
        Assert.Equal(["L2"], cache.Match("be").Select(l => l.Id));
    }

    [Fact]
    public void Pool_PersistsAcrossInstances_WithWarmStore()
    {
        var store = new JsonFileStateStore(_dir);
        var first = new ListFrequencyCache(store, "ws1");
        first.RecordFromTasks([MakeTask("t1", "L1", "Alpha"), MakeTask("t2", "L1", "Alpha"), MakeTask("t3", "L2", "Beta")]);

        // A brand-new instance over the same (warm) store must recover the pool + counts.
        var second = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");

        Assert.Equal(2, second.Count);
        Assert.Equal(["L1", "L2"], second.TopMostFrequent(10).Select(l => l.Id)); // Alpha (2) ahead of Beta (1)
    }

    [Fact]
    public void Load_IgnoresPoolFromDifferentWorkspace()
    {
        var store = new JsonFileStateStore(_dir);
        new ListFrequencyCache(store, "ws1").RecordFromTasks([MakeTask("t1", "L1", "Alpha")]);

        // Same store, different workspace → clean miss (empty), never the wrong lists.
        var other = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws2");

        Assert.Equal(0, other.Count);
        Assert.Empty(other.TopMostFrequent(10));
    }

    [Fact]
    public void Load_IgnoresPoolWithMismatchedSchemaVersion()
    {
        // Persist a document with a future/incompatible schema version directly.
        var store = new JsonFileStateStore(_dir);
        store.Save(StateKeys.Lists, new ListFrequencyDocument(
            ListFrequencyCache.CurrentSchemaVersion + 1, "ws1",
            [new ListFrequencyEntry("L1", "Alpha", ["t1"])]));

        var cache = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Load_WithCorruptPayload_IsCleanMiss_NotCrash()
    {
        // A torn write from a concurrent tab (or a hand-tampered file) leaves malformed JSON under the
        // key. Load runs in the constructor, so a throw would brick the selector's owner — #293 makes
        // it a clean miss (empty pool) instead.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(new JsonFileStateStore(_dir).PathFor(StateKeys.Lists), "{ not valid json");

        var cache = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");

        Assert.Equal(0, cache.Count);
        Assert.Empty(cache.TopMostFrequent(10));
    }

    [Fact]
    public void RecordFromTasks_SwallowsWriteFailure_NotCrash()
    {
        // A failed persist (read-only/full disk, or LiteDB contention under multi-tab writes #293) must
        // never crash the refresh loop that calls RecordFromTasks on the UI thread.
        var cache = new ListFrequencyCache(new ThrowOnSaveStore(), "ws1");

        var ex = Record.Exception(() => cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]));

        Assert.Null(ex);
        Assert.Equal(1, cache.Count); // the pool lives on in memory
    }

    [Fact]
    public void Persist_MergesAConcurrentTabsEntries_RatherThanClobbering()
    {
        // The concrete multi-tab clobber (#293): two tabs sharing one store both learn a different
        // list. Tab B's whole-set write used to overwrite tab A's — now B re-reads and unions first.
        var store = new JsonFileStateStore(_dir);
        var tabA = new ListFrequencyCache(store, "ws1");
        var tabB = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");

        tabA.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]); // disk: { L1 }
        tabB.RecordFromTasks([MakeTask("t2", "L2", "Beta")]);  // merges disk (L1) before writing → { L1, L2 }

        Assert.Equal(2, tabB.Count); // B also picked up L1 in-memory via the merge

        // A fresh reader sees BOTH — tab B's write did not discard tab A's L1.
        var reader = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");
        Assert.Equal(2, reader.Count);
        Assert.Equal(["L1", "L2"], reader.TopMostFrequent(10).Select(l => l.Id)); // count tie → name asc
    }

    [Fact]
    public void RecordFromTasks_PersistsOnlyOnChange()
    {
        var store = new CountingStateStore(new JsonFileStateStore(_dir));
        var cache = new ListFrequencyCache(store, "ws1");

        cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]);
        Assert.Equal(1, store.Saves);

        // Nothing tallied (no valid list) → no extra save.
        cache.RecordFromTasks([MakeTask("t2", null, "NoId"), MakeTask("t3", "L3", "  ")]);
        Assert.Equal(1, store.Saves);

        // Re-observing the SAME task on a later poll is idempotent: no inflation, no extra write —
        // the hot-path guard the distinct-task model exists to provide.
        cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]);
        Assert.Equal(1, store.Saves);
        Assert.Single(cache.TopMostFrequent(10));
    }

    [Fact]
    public void RecordFromTasks_SameWorkingSetTwice_DoesNotInflateAcrossWarmRestart()
    {
        var first = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");
        first.RecordFromTasks([MakeTask("t1", "L1", "Alpha"), MakeTask("t2", "L1", "Alpha")]);

        // A fresh instance loads the warm pool, then a first poll re-observes the same tasks — the
        // persisted distinct-task ids make that a no-op rather than doubling L1's count.
        var second = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");
        second.RecordFromTasks([MakeTask("t1", "L1", "Alpha"), MakeTask("t2", "L1", "Alpha")]);

        var l1 = second.TopMostFrequent(10).Single();
        Assert.Equal("L1", l1.Id);
        // 2 distinct tasks (t1, t2), not 4 — no cross-restart inflation.
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public void SeedLists_AddsWalkLists_AsCountZeroCandidates_BelowTallied()
    {
        var cache = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");
        cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]);

        // The scheduled walk (#236) discovers lists no task row surfaced — seed them count-0.
        cache.SeedLists([new NamedEntity("L1", "Different"), new NamedEntity("L2", "Beta"), new NamedEntity("L3", "Cid")]);

        var pool = cache.Match("").ToList();
        Assert.Equal(3, cache.Count);
        Assert.Equal("L1", pool[0].Id);          // tallied list (count 1) still ranks first
        Assert.Equal("Alpha", pool[0].Name);     // real name preserved, not clobbered by the seed
        Assert.Contains(pool, l => l is { Id: "L2", Name: "Beta" });
        Assert.Contains(pool, l => l is { Id: "L3", Name: "Cid" });
    }

    [Fact]
    public void SeedLists_PersistsOnlyWhenItAddsANewList()
    {
        var store = new CountingStateStore(new JsonFileStateStore(_dir));
        var cache = new ListFrequencyCache(store, "ws1");

        cache.SeedLists([new NamedEntity("L1", "Alpha")]);
        Assert.Equal(1, store.Saves);

        // Re-seeding an already-known list adds nothing → no extra write. The walk pushes its full
        // known-set every step, so this idempotence keeps it off the hot path.
        cache.SeedLists([new NamedEntity("L1", "Alpha")]);
        Assert.Equal(1, store.Saves);

        cache.SeedLists([new NamedEntity("L2", "Beta")]);
        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public void SeedLists_SurvivesWarmRestart_AndTaskRowMergesIntoTheSeededEntry()
    {
        var store = new JsonFileStateStore(_dir);
        new ListFrequencyCache(store, "ws1").SeedLists([new NamedEntity("L1", "Alpha"), new NamedEntity("L2", "Beta")]);

        // A later instance loads the seeded (count-0) pool, then a task row for L1 lifts it above the
        // still-count-0 L2 — merging into the seeded entry rather than creating a duplicate list.
        var next = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");
        Assert.Equal(2, next.Count);
        next.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]);

        Assert.Equal(2, next.Count); // no duplicate — the row folded into the seeded L1
        Assert.Equal(["L1", "L2"], next.TopMostFrequent(10).Select(l => l.Id)); // L1 (count 1) now first
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Wraps a store to count <see cref="IStateStore.Save{T}"/> calls, so a test can assert the
    /// cache persists only when the pool actually changed.</summary>
    private sealed class CountingStateStore(IStateStore inner) : IStateStore
    {
        public int Saves { get; private set; }

        public bool Exists(string key) => inner.Exists(key);
        public T? Load<T>(string key) where T : class => inner.Load<T>(key);
        public void Save<T>(string key, T value) where T : class { Saves++; inner.Save(key, value); }
        public void Delete(string key) => inner.Delete(key);
    }

    /// <summary>A store whose write always fails — stands in for a read-only/full disk or a LiteDB
    /// contention error, so a test can assert the cache swallows it rather than crashing.</summary>
    private sealed class ThrowOnSaveStore : IStateStore
    {
        public bool Exists(string key) => false;
        public T? Load<T>(string key) where T : class => null;
        public void Save<T>(string key, T value) where T : class => throw new IOException("disk full");
        public void Delete(string key) { }
    }
}
