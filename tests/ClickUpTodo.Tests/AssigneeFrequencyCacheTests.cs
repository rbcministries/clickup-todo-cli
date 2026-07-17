using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The stateful assignee-frequency cache (#155) — <see cref="AssigneeFrequencyCache"/>: persistence
/// through <see cref="IStateStore"/> keyed per workspace, and the deferred workspace-members top-up.
/// The pure ranking rules are covered by <see cref="AssigneeFrequencyTests"/>.
/// </summary>
public sealed class AssigneeFrequencyCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private static TaskItem MakeTask(string id, params (long Id, string Name)[] assignees) => new()
    {
        Id = id,
        Name = $"Task {id}",
        Assignees = assignees.Select(a => new TaskAssignee(a.Id, a.Name)).ToList(),
    };

    private static Task<IReadOnlyList<WorkspaceMember>> NoMembers(CancellationToken _)
        => Task.FromResult<IReadOnlyList<WorkspaceMember>>([]);

    [Fact]
    public void RecordFromTasks_ThenQuery_RanksByOccurrence()
    {
        var cache = new AssigneeFrequencyCache(new JsonFileStateStore(_dir), "ws1", NoMembers);

        cache.RecordFromTasks([MakeTask("t1", (1, "Ada"), (2, "Bo")), MakeTask("t2", (2, "Bo"))]);

        Assert.Equal([2, 1], cache.TopMostFrequent(10).Select(a => a.Id));
        Assert.Equal([1], cache.Match("ad").Select(a => a.Id));
    }

    [Fact]
    public void Pool_PersistsAcrossInstances_WithWarmStore()
    {
        var store = new JsonFileStateStore(_dir);
        var first = new AssigneeFrequencyCache(store, "ws1", NoMembers);
        first.RecordFromTasks([MakeTask("t1", (1, "Ada"), (2, "Bo")), MakeTask("t2", (1, "Ada"))]);

        // A brand-new instance over the same (warm) store must recover the pool + counts.
        var second = new AssigneeFrequencyCache(new JsonFileStateStore(_dir), "ws1", NoMembers);

        Assert.Equal(2, second.Count);
        Assert.Equal([1, 2], second.TopMostFrequent(10).Select(a => a.Id)); // Ada (2) ahead of Bo (1)
    }

    [Fact]
    public void Load_IgnoresPoolFromDifferentWorkspace()
    {
        var store = new JsonFileStateStore(_dir);
        new AssigneeFrequencyCache(store, "ws1", NoMembers)
            .RecordFromTasks([MakeTask("t1", (1, "Ada"))]);

        // Same store, different workspace → clean miss (empty), never the wrong people.
        var other = new AssigneeFrequencyCache(new JsonFileStateStore(_dir), "ws2", NoMembers);

        Assert.Equal(0, other.Count);
        Assert.Empty(other.TopMostFrequent(10));
    }

    [Fact]
    public void Load_IgnoresPoolWithMismatchedSchemaVersion()
    {
        // Persist a document with a future/incompatible schema version directly.
        var store = new JsonFileStateStore(_dir);
        store.Save(StateKeys.Assignees, new AssigneeFrequencyDocument(
            AssigneeFrequencyCache.CurrentSchemaVersion + 1, "ws1",
            [new AssigneeFrequencyEntry(1, "Ada", ["t1"])]));

        var cache = new AssigneeFrequencyCache(new JsonFileStateStore(_dir), "ws1", NoMembers);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Load_WithCorruptPayload_IsCleanMiss_NotCrash()
    {
        // A torn write from a concurrent tab (or a hand-tampered file) leaves malformed JSON under the
        // key. Load runs in the constructor, so a throw would brick the pane's owner — #293 makes it a
        // clean miss (empty pool) instead.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(new JsonFileStateStore(_dir).PathFor(StateKeys.Assignees), "{ not valid json");

        var cache = new AssigneeFrequencyCache(new JsonFileStateStore(_dir), "ws1", NoMembers);

        Assert.Equal(0, cache.Count);
        Assert.Empty(cache.TopMostFrequent(10));
    }

    [Fact]
    public void RecordFromTasks_SwallowsWriteFailure_NotCrash()
    {
        // A failed persist (read-only/full disk, or LiteDB contention under multi-tab writes #293) must
        // never crash the refresh loop that calls RecordFromTasks on the UI thread.
        var cache = new AssigneeFrequencyCache(new ThrowOnSaveStore(), "ws1", NoMembers);

        var ex = Record.Exception(() => cache.RecordFromTasks([MakeTask("t1", (1, "Ada"))]));

        Assert.Null(ex);
        Assert.Equal(1, cache.Count); // the pool lives on in memory
    }

    [Fact]
    public void RecordFromTasks_PersistsOnlyOnChange()
    {
        var store = new CountingStateStore(new JsonFileStateStore(_dir));
        var cache = new AssigneeFrequencyCache(store, "ws1", NoMembers);

        cache.RecordFromTasks([MakeTask("t1", (1, "Ada"))]);
        Assert.Equal(1, store.Saves);

        // Nothing tallied (no valid assignees) → no extra save.
        cache.RecordFromTasks([MakeTask("t2"), MakeTask("t3", (0, "Zero"))]);
        Assert.Equal(1, store.Saves);

        // Re-observing the SAME task on a later poll is idempotent: no inflation, no extra write —
        // the hot-path guard the distinct-task model exists to provide.
        cache.RecordFromTasks([MakeTask("t1", (1, "Ada"))]);
        Assert.Equal(1, store.Saves);
        Assert.Single(cache.TopMostFrequent(10));
    }

    [Fact]
    public void RecordFromTasks_SameWorkingSetTwice_DoesNotInflateAcrossWarmRestart()
    {
        var first = new AssigneeFrequencyCache(new JsonFileStateStore(_dir), "ws1", NoMembers);
        first.RecordFromTasks([MakeTask("t1", (1, "Ada")), MakeTask("t2", (1, "Ada"))]);

        // A fresh instance loads the warm pool, then a first poll re-observes the same tasks — the
        // persisted distinct-task ids make that a no-op rather than doubling Ada's count.
        var second = new AssigneeFrequencyCache(new JsonFileStateStore(_dir), "ws1", NoMembers);
        second.RecordFromTasks([MakeTask("t1", (1, "Ada")), MakeTask("t2", (1, "Ada"))]);

        var ada = second.TopMostFrequent(10).Single();
        Assert.Equal(1, ada.Id);
        // 2 distinct tasks (t1, t2), not 4 — no cross-restart inflation.
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public async Task TopUpAsync_SeedsWorkspaceMembers_WhenPoolIsThin()
    {
        var members = new List<WorkspaceMember>
        {
            new(10, "carol", "carol@example.com"),
            new(11, null, "dave@example.com"), // no username → email local part
            new(12, "   ", "  "),              // no usable name → skipped
        };
        var cache = new AssigneeFrequencyCache(
            new JsonFileStateStore(_dir), "ws1",
            _ => Task.FromResult<IReadOnlyList<WorkspaceMember>>(members));
        cache.RecordFromTasks([MakeTask("t1", (1, "Ada"))]);

        await cache.TopUpAsync(minCandidates: 10);

        var pool = cache.Match("").ToList();
        Assert.Equal(3, cache.Count); // Ada + carol + dave (member 12 skipped)
        Assert.Contains(pool, a => a is { Id: 10, Name: "carol" });
        Assert.Contains(pool, a => a is { Id: 11, Name: "dave" });
        Assert.DoesNotContain(pool, a => a.Id == 12);
        Assert.Equal(1, pool[0].Id); // Ada (count 1) still ranks first
    }

    [Fact]
    public async Task TopUpAsync_IsNoOp_WhenPoolAlreadyMeetsTarget()
    {
        var fetched = false;
        var cache = new AssigneeFrequencyCache(
            new JsonFileStateStore(_dir), "ws1",
            _ => { fetched = true; return NoMembers(default); });
        cache.RecordFromTasks([MakeTask("t1", (1, "Ada"), (2, "Bo"))]);

        await cache.TopUpAsync(minCandidates: 2); // pool already has 2

        Assert.False(fetched);
    }

    [Fact]
    public async Task TopUpAsync_RunsAtMostOnce()
    {
        var calls = 0;
        var cache = new AssigneeFrequencyCache(
            new JsonFileStateStore(_dir), "ws1",
            _ => { calls++; return NoMembers(default); });

        await cache.TopUpAsync(minCandidates: 10);
        await cache.TopUpAsync(minCandidates: 10);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TopUpAsync_SwallowsFetchFailure()
    {
        var cache = new AssigneeFrequencyCache(
            new JsonFileStateStore(_dir), "ws1",
            _ => Task.FromException<IReadOnlyList<WorkspaceMember>>(new HttpRequestException("boom")));
        cache.RecordFromTasks([MakeTask("t1", (1, "Ada"))]);

        // Must not throw; the pool is simply left as-is.
        await cache.TopUpAsync(minCandidates: 10);

        Assert.Equal(1, cache.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Wraps a store to count <see cref="IStateStore.Save{T}"/> calls, so a test can assert
    /// the cache persists only when the pool actually changed.</summary>
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
