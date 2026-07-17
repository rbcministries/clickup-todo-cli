using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the cross-restart persistence of the warm closed-task set (#280): the bounded set round-trips
/// through <see cref="IStateStore"/> and warms on construction, a context/schema mismatch or a corrupt
/// payload is a clean miss (never a crash), the per-task age window is re-applied on load so a stale set
/// self-prunes, and a purely in-memory cache (no store) persists nothing. Uses a real temp-dir
/// <see cref="JsonFileStateStore"/> so the <see cref="TaskItem"/> JSON round-trip is exercised end-to-end,
/// mirroring <c>TaskCacheTests</c>.
/// </summary>
public sealed class ClosedTaskCachePersistenceTests : IDisposable
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

    private static readonly DateTimeOffset Now = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static TaskItem Closed(string id, DateTimeOffset? updated)
        => new() { Id = id, Name = id, StatusType = "closed", UpdatedMs = updated?.ToUnixTimeMilliseconds() };

    private static ClosedTaskCache Persisted(
        IStateStore store, string key = "ctx-1", TimeProvider? clock = null,
        int maxCount = ClosedTaskCache.DefaultMaxCount, TimeSpan? maxAge = null)
        => new(clock ?? new FakeClock(Now), maxCount, maxAge, store, () => key);

    // --- round-trip ------------------------------------------------------------------------------

    [Fact]
    public void Update_Persists_AndFreshCacheWarmsFromStore()
    {
        var store = Store();
        var writer = Persisted(store);
        writer.Update([Closed("a", Now.AddDays(-1)), Closed("b", Now.AddDays(-2))]);

        var reader = Persisted(store);

        Assert.Equal(["a", "b"], reader.Snapshot.Select(t => t.Id));
        Assert.Equal(2, reader.Count);
    }

    [Fact]
    public void Persisted_Update_StoresBoundedSet_NotTheRawInput()
    {
        var store = Store();
        // Count cap of 2 drops the oldest before persisting, so the reload sees only the bounded set.
        Persisted(store, maxCount: 2).Update([
            Closed("a", Now.AddDays(-1)),
            Closed("b", Now.AddDays(-2)),
            Closed("c", Now.AddDays(-3)),
        ]);

        var reader = Persisted(store, maxCount: 2);

        Assert.Equal(["a", "b"], reader.Snapshot.Select(t => t.Id));
    }

    [Fact]
    public void Load_KeyMismatch_IsCleanMiss()
    {
        var store = Store();
        Persisted(store, key: "ctx-1").Update([Closed("a", Now.AddDays(-1))]);

        var reader = Persisted(store, key: "ctx-2"); // e.g. a workspace/list/assignee switch

        Assert.Empty(reader.Snapshot);
    }

    [Fact]
    public void Load_SchemaMismatch_IsCleanMiss()
    {
        var store = Store();
        // A document written by an incompatible future schema must be discarded, not mis-read.
        store.Save(StateKeys.Closed, new ClosedTaskCacheDocument
        {
            SchemaVersion = ClosedTaskCache.CurrentSchemaVersion + 1,
            Key = "ctx-1",
            Tasks = [Closed("a", Now.AddDays(-1))],
        });

        Assert.Empty(Persisted(store, key: "ctx-1").Snapshot);
    }

    [Fact]
    public void Load_NullTasksPayload_IsCleanMiss_NoThrow()
    {
        var store = Store();
        // `required` guards presence, not non-null — a structurally-valid document with tasks:null must
        // degrade to an empty set, not throw out of the constructor and brick launch.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, $"{StateKeys.Closed}.json"),
            $$"""{"SchemaVersion":{{ClosedTaskCache.CurrentSchemaVersion}},"Key":"ctx-1","Tasks":null}""");

        Assert.Empty(Persisted(store, key: "ctx-1").Snapshot);
    }

    [Fact]
    public void ContextKey_IsEvaluatedLive_OnEachPersist()
    {
        var store = Store();
        var key = "ctx-1";
        var cache = new ClosedTaskCache(new FakeClock(Now), store: store, contextKey: () => key);

        cache.Update([Closed("a", Now.AddDays(-1))]);
        Assert.Equal(["a"], Persisted(store, key: "ctx-1").Snapshot.Select(t => t.Id));

        // The provider now reports a switched context (e.g. an F3 assignee-scope change); the next
        // persist must key the set under the *current* value, not the one captured at construction.
        key = "ctx-2";
        cache.Update([Closed("b", Now.AddDays(-1))]);

        Assert.Equal(["b"], Persisted(store, key: "ctx-2").Snapshot.Select(t => t.Id));
        Assert.Empty(Persisted(store, key: "ctx-1").Snapshot); // the ctx-1 document was overwritten
    }

    [Fact]
    public void Load_CorruptDocument_IsCleanMiss_NoThrow()
    {
        var store = Store();
        // Truncated/garbage payload (e.g. a quit mid-write) must degrade to an empty warm set.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, $"{StateKeys.Closed}.json"), "{ not valid json ");

        var reader = Persisted(store, key: "ctx-1");

        Assert.Empty(reader.Snapshot);
    }

    [Fact]
    public void Load_ReAppliesAgeWindow_AgainstLaunchTime()
    {
        var store = Store();
        // Persist a set that is fresh "now"...
        var writer = Persisted(store, clock: new FakeClock(Now), maxAge: TimeSpan.FromDays(30));
        writer.Update([Closed("fresh", Now.AddDays(-1)), Closed("older", Now.AddDays(-20))]);

        // ...then relaunch 25 days later: "older" (now 45 days old) has aged past the 30-day window and
        // must be pruned on load, while "fresh" (26 days) survives.
        var laterClock = new FakeClock(Now.AddDays(25));
        var reader = Persisted(store, clock: laterClock, maxAge: TimeSpan.FromDays(30));

        Assert.Equal(["fresh"], reader.Snapshot.Select(t => t.Id));
    }

    [Fact]
    public void NonPersistent_Cache_WritesNothing()
    {
        var store = Store();
        // No context-key provider ⇒ persistence inactive even with a store present.
        var inMemory = new ClosedTaskCache(new FakeClock(Now), store: store);
        inMemory.Update([Closed("a", Now.AddDays(-1))]);

        Assert.False(store.Exists(StateKeys.Closed));
        // And a persistent cache over the same store finds nothing to warm from.
        Assert.Empty(Persisted(store).Snapshot);
    }

    [Fact]
    public void Update_OverwritesPreviousPersistedSet()
    {
        var store = Store();
        Persisted(store).Update([Closed("a", Now.AddDays(-1))]);
        Persisted(store).Update([Closed("b", Now.AddDays(-1))]);

        Assert.Equal(["b"], Persisted(store).Snapshot.Select(t => t.Id));
    }

    [Fact]
    public void Update_RoundTrips_TaskFields()
    {
        var store = Store();
        var task = new TaskItem
        {
            Id = "t1",
            Name = "Ship it",
            StatusType = "closed",
            StatusName = "Complete",
            UpdatedMs = Now.AddDays(-1).ToUnixTimeMilliseconds(),
            Assignees = [new TaskAssignee(42, "Ben")],
        };
        Persisted(store).Update([task]);

        var loaded = Persisted(store).Snapshot.Single();

        Assert.Equal("t1", loaded.Id);
        Assert.Equal("Ship it", loaded.Name);
        Assert.Equal("closed", loaded.StatusType);
        Assert.Equal("Complete", loaded.StatusName);
        Assert.Equal(new TaskAssignee(42, "Ben"), loaded.Assignees.Single());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
