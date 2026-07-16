using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The stateful list-frequency cache (#238) — <see cref="ListFrequencyCache"/>: persistence through
/// <see cref="IStateStore"/> keyed per workspace, and the scheduled-walk <see cref="ListFrequencyCache.Seed"/>
/// backfill. The pure ranking rules are covered by <see cref="ListFrequencyTests"/>. Mirrors
/// <see cref="AssigneeFrequencyCacheTests"/>.
/// </summary>
public sealed class ListFrequencyCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private static TaskItem MakeTask(string id, string listId, string listName) => new()
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

        cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha"), MakeTask("t2", "L2", "Beta"), MakeTask("t3", "L2", "Beta")]);

        Assert.Equal(["L2", "L1"], cache.TopMostFrequent(10).Select(l => l.Id));
        Assert.Equal(["L1"], cache.Match("alph").Select(l => l.Id));
    }

    [Fact]
    public void Pool_PersistsAcrossInstances_WithWarmStore()
    {
        var store = new JsonFileStateStore(_dir);
        var first = new ListFrequencyCache(store, "ws1");
        first.RecordFromTasks([MakeTask("t1", "L1", "Alpha"), MakeTask("t2", "L2", "Beta"), MakeTask("t3", "L1", "Alpha")]);

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
    public void RecordFromTasks_PersistsOnlyOnChange()
    {
        var store = new CountingStateStore(new JsonFileStateStore(_dir));
        var cache = new ListFrequencyCache(store, "ws1");

        cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]);
        Assert.Equal(1, store.Saves);

        // Nothing tallied (no valid list) → no extra save.
        cache.RecordFromTasks([MakeTask("t2", "", "NoId"), MakeTask("t3", "L3", "  ")]);
        Assert.Equal(1, store.Saves);

        // Re-observing the SAME task on a later poll is idempotent: no inflation, no extra write.
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
        Assert.Equal(1, second.Count); // one list, 2 distinct tasks — no cross-restart inflation
    }

    [Fact]
    public void Seed_AddsWalkLists_AtCountZero_BelowTallied()
    {
        var cache = new ListFrequencyCache(new JsonFileStateStore(_dir), "ws1");
        cache.RecordFromTasks([MakeTask("t1", "L1", "Alpha")]);

        cache.Seed([new NamedEntity("L1", "Alpha (walk name)"), new NamedEntity("L9", "Archive")]);

        var pool = cache.Match("").ToList();
        Assert.Equal(2, cache.Count);           // Alpha + Archive
        Assert.Equal("L1", pool[0].Id);         // tallied Alpha (count 1) still ranks first
        Assert.Equal("Alpha", pool[0].Name);    // seed must not clobber the tallied name
        Assert.Contains(pool, l => l is { Id: "L9", Name: "Archive" });
    }

    [Fact]
    public void Seed_PersistsOnlyWhenItAddsANewList()
    {
        var store = new CountingStateStore(new JsonFileStateStore(_dir));
        var cache = new ListFrequencyCache(store, "ws1");

        cache.Seed([new NamedEntity("L9", "Archive")]);
        Assert.Equal(1, store.Saves);

        // Re-seeding the same list adds nothing → no extra write.
        cache.Seed([new NamedEntity("L9", "Archive")]);
        Assert.Equal(1, store.Saves);
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
}
