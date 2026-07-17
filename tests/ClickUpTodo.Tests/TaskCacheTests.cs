using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the persistent task cache (#122): the working-set round-trip through <see cref="IStateStore"/>,
/// the context-fingerprint keying that keeps a switched context from painting a stale set, and the
/// schema-version guard. Uses a real temp-dir <see cref="JsonFileStateStore"/> so the JSON round-trip of
/// <see cref="TaskItem"/> (including assignees) is exercised end-to-end.
/// </summary>
public sealed class TaskCacheTests : IDisposable
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

    private static AppConfig Config(string workspace = "ws-1", string list = "list-1", ViewSettings? view = null)
        => new()
        {
            WorkspaceId = workspace,
            PersonalTasksListId = list,
            View = view ?? new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] },
        };

    private static TaskItem Task(string id, string name, params TaskAssignee[] assignees)
        => new() { Id = id, Name = name, StatusName = "open", Assignees = assignees };

    // --- round-trip ------------------------------------------------------------------------------

    [Fact]
    public void Save_ThenLoad_RoundTripsWorkingSet_IncludingAssignees()
    {
        var cache = new TaskCache(Store());
        var config = Config();
        var tasks = new[]
        {
            Task("t1", "First", new TaskAssignee(42, "Ben")),
            Task("t2", "Second"),
        };

        cache.Save(config, tasks);
        var loaded = cache.Load(config);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Equal("t1", loaded[0].Id);
        Assert.Equal("First", loaded[0].Name);
        Assert.Equal(new TaskAssignee(42, "Ben"), loaded[0].Assignees.Single());
        Assert.Empty(loaded[1].Assignees);
    }

    [Fact]
    public void Load_WhenNothingCached_ReturnsNull()
        => Assert.Null(new TaskCache(Store()).Load(Config()));

    [Fact]
    public void Load_WhenEmptySetWasCached_ReturnsEmptyNotNull()
    {
        // A genuinely-empty working set is distinct from a miss: the caller shouldn't fall back to a
        // stale paint just because the last load had zero tasks.
        var cache = new TaskCache(Store());
        var config = Config();

        cache.Save(config, []);
        var loaded = cache.Load(config);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!);
    }

    // --- context keying (the "never show the wrong set" guarantee) -------------------------------

    [Fact]
    public void Load_WhenWorkspaceDiffers_ReturnsNull()
    {
        var cache = new TaskCache(Store());
        cache.Save(Config(workspace: "ws-A"), [Task("t1", "First")]);

        Assert.Null(cache.Load(Config(workspace: "ws-B")));
    }

    [Fact]
    public void Load_WhenListDiffers_ReturnsNull()
    {
        var cache = new TaskCache(Store());
        cache.Save(Config(list: "list-A"), [Task("t1", "First")]);

        Assert.Null(cache.Load(Config(list: "list-B")));
    }

    [Fact]
    public void Load_WhenAssigneeScopeDiffers_ReturnsNull()
    {
        var cache = new TaskCache(Store());
        var mine = new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] };
        var teammate = new ViewSettings
        {
            Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "99" }],
        };

        cache.Save(Config(view: mine), [Task("t1", "First")]);

        Assert.Null(cache.Load(Config(view: teammate)));
    }

    [Fact]
    public void Load_WhenDocumentIsCorrupt_ReturnsNullNotThrow()
    {
        // A truncated / garbage tasks.json (e.g. a quit or crash mid-write of the frequently-rewritten
        // cache) must degrade to a miss, not throw — the load runs before the UI loop, so a throw would
        // brick every launch.
        var store = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor(StateKeys.Tasks), "{ \"tasks\": [ {\"id\": \"t1\""); // truncated

        Assert.Null(new TaskCache(store).Load(Config()));
    }

    [Fact]
    public void Load_WhenSchemaVersionMismatch_ReturnsNull()
    {
        // A document written by an incompatible (future) schema is discarded rather than trusted.
        var store = Store();
        var config = Config();
        store.Save(StateKeys.Tasks, new TaskCacheDocument
        {
            SchemaVersion = TaskCache.CurrentSchemaVersion + 1,
            Key = TaskCache.KeyFor(config),
            Tasks = [Task("t1", "First")],
        });

        Assert.Null(new TaskCache(store).Load(config));
    }

    [Fact]
    public void Load_WhenPriorSchemaVersion_ReturnsNull()
    {
        // The #124 bump (1 → 2, adding the capture timestamp) must discard a pre-#124 v1 document rather
        // than paint it with a fabricated (zero) age.
        var store = Store();
        var config = Config();
        store.Save(StateKeys.Tasks, new TaskCacheDocument
        {
            SchemaVersion = 1,
            Key = TaskCache.KeyFor(config),
            Tasks = [Task("t1", "First")],
        });

        Assert.Null(new TaskCache(store).Load(config));
    }

    // --- staleness / TTL / eviction (#124) -------------------------------------------------------

    [Fact]
    public void LoadSnapshot_ReturnsTheCaptureTime()
    {
        var clock = NewClock();
        var store = Store();
        var config = Config();
        new TaskCache(store, clock).Save(config, [Task("t1", "First")]);

        clock.Advance(TimeSpan.FromMinutes(5));
        var snapshot = new TaskCache(store, clock).LoadSnapshot(config);

        Assert.NotNull(snapshot);
        Assert.Equal("t1", snapshot!.Items.Single().Id);
        // Captured at the clock's start; five minutes have since elapsed.
        Assert.Equal(TimeSpan.FromMinutes(5), clock.GetUtcNow() - snapshot.CapturedAt);
    }

    [Fact]
    public void Load_WhenWithinMaxAge_Hits()
    {
        var clock = NewClock();
        var store = Store();
        var config = Config();
        var maxAge = TimeSpan.FromDays(14);
        new TaskCache(store, clock, maxAge).Save(config, [Task("t1", "First")]);

        clock.Advance(maxAge - TimeSpan.FromSeconds(1)); // just inside the window
        var loaded = new TaskCache(store, clock, maxAge).Load(config);

        Assert.NotNull(loaded);
        Assert.Equal("t1", loaded!.Single().Id);
    }

    [Fact]
    public void Load_WhenExactlyAtMaxAge_IsStale()
    {
        // The boundary is exclusive (age == maxAge is a miss), matching StatusCache's age < ttl.
        var clock = NewClock();
        var store = Store();
        var config = Config();
        var maxAge = TimeSpan.FromDays(14);
        new TaskCache(store, clock, maxAge).Save(config, [Task("t1", "First")]);

        clock.Advance(maxAge); // exactly on the boundary
        Assert.Null(new TaskCache(store, clock, maxAge).Load(config));
    }

    [Fact]
    public void Load_WhenOlderThanMaxAge_ReturnsNullAndPrunesTheStaleDocument()
    {
        var clock = NewClock();
        var store = Store();
        var config = Config();
        var maxAge = TimeSpan.FromDays(14);
        new TaskCache(store, clock, maxAge).Save(config, [Task("t1", "First")]);

        clock.Advance(maxAge + TimeSpan.FromSeconds(1)); // just past the window
        var loaded = new TaskCache(store, clock, maxAge).Load(config);

        Assert.Null(loaded);
        Assert.False(store.Exists(StateKeys.Tasks)); // self-pruned, not left to linger
    }

    [Fact]
    public void Load_WhenTimestampIsOutOfRange_ReturnsNullAndPrunes()
    {
        // A hand-tampered file with a nonsense timestamp must degrade to a miss (and get pruned), never
        // throw on the pre-UI-loop load path.
        var store = Store();
        var config = Config();
        store.Save(StateKeys.Tasks, new TaskCacheDocument
        {
            SchemaVersion = TaskCache.CurrentSchemaVersion,
            Key = TaskCache.KeyFor(config),
            CapturedAtMs = long.MaxValue,
            Tasks = [Task("t1", "First")],
        });

        Assert.Null(new TaskCache(store).Load(config));
        Assert.False(store.Exists(StateKeys.Tasks));
    }

    // --- supersede / clear -----------------------------------------------------------------------

    [Fact]
    public void Save_OverwritesPriorDocument()
    {
        var cache = new TaskCache(Store());
        var config = Config();

        cache.Save(config, [Task("t1", "First")]);
        cache.Save(config, [Task("t2", "Second"), Task("t3", "Third")]);

        var loaded = cache.Load(config);
        Assert.NotNull(loaded);
        Assert.Equal(["t2", "t3"], loaded!.Select(t => t.Id));
    }

    [Fact]
    public void Clear_RemovesDocument()
    {
        var cache = new TaskCache(Store());
        var config = Config();
        cache.Save(config, [Task("t1", "First")]);

        cache.Clear();

        Assert.Null(cache.Load(config));
    }

    // --- KeyFor purity / design contract ---------------------------------------------------------

    [Fact]
    public void KeyFor_IsStable_ForTheSameConfig()
        => Assert.Equal(TaskCache.KeyFor(Config()), TaskCache.KeyFor(Config()));

    [Fact]
    public void KeyFor_ChangesWith_Workspace_List_And_AssigneeScope()
    {
        var baseKey = TaskCache.KeyFor(Config());
        Assert.NotEqual(baseKey, TaskCache.KeyFor(Config(workspace: "other")));
        Assert.NotEqual(baseKey, TaskCache.KeyFor(Config(list: "other")));

        var teammate = new ViewSettings
        {
            Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "99" }],
        };
        Assert.NotEqual(baseKey, TaskCache.KeyFor(Config(view: teammate)));
    }

    [Fact]
    public void KeyFor_IsUnaffectedBy_Sort_Group_AndNonAssigneeFilters()
    {
        // The fetched working set doesn't depend on client-side sort/group or a Status IS NOT rule, so
        // the cache stays valid across those between sessions (locks in the design decision).
        var plain = new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] };
        var tweaked = new ViewSettings
        {
            Filters =
            [
                ViewSettings.DefaultAssigneeRule(),
                ViewSettings.StatusIsNotRule("blocked"),
            ],
            SortField = TaskField.Due,
            SortDirection = SortDirection.Descending,
            GroupField = TaskField.List,
        };

        Assert.Equal(TaskCache.KeyFor(Config(view: plain)), TaskCache.KeyFor(Config(view: tweaked)));
    }

    [Fact]
    public void KeyFor_IsCaseInsensitive_InTheAssigneeValues()
    {
        // Same server-side scope typed with different casing → same fingerprint (no false miss).
        var upper = new ViewSettings
        {
            Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "Ben" }],
        };
        var lower = new ViewSettings
        {
            Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "ben" }],
        };

        Assert.Equal(TaskCache.KeyFor(Config(view: upper)), TaskCache.KeyFor(Config(view: lower)));
    }

    [Fact]
    public void KeyFor_IsOrderIndependent_InTheAssigneeSet()
    {
        var ab = new ViewSettings
        {
            Filters =
            [
                new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "a" },
                new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "b" },
            ],
        };
        var ba = new ViewSettings
        {
            Filters =
            [
                new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "b" },
                new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "a" },
            ],
        };

        Assert.Equal(TaskCache.KeyFor(Config(view: ab)), TaskCache.KeyFor(Config(view: ba)));
    }

    [Fact]
    public void Save_SwallowsWriteFailure_DoesNotThrow()
    {
        // The task cache is a throwaway warm-paint snapshot; a failed write (read-only/full disk, or
        // LiteDB contention under multi-tab writes #293) must never break the refresh loop.
        var cache = new TaskCache(new ThrowOnSaveStore());

        var ex = Record.Exception(() => cache.Save(Config(), [Task("t1", "One")]));

        Assert.Null(ex);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A store whose write always fails — stands in for a read-only/full disk or a LiteDB
    /// contention error, so a test can assert <see cref="TaskCache.Save"/> swallows it.</summary>
    private sealed class ThrowOnSaveStore : IStateStore
    {
        public bool Exists(string key) => false;
        public T? Load<T>(string key) where T : class => null;
        public void Save<T>(string key, T value) where T : class => throw new IOException("disk full");
        public void Delete(string key) { }
    }
}
