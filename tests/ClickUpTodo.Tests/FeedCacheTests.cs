using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the persistent feed cache (#123): the feed round-trip through <see cref="IStateStore"/>
/// (including mention flags, mentioned-user ids, and task attribution), the workspace/assignee
/// fingerprint keying that keeps a switched context from painting a stale feed, and the schema-version
/// guard. Uses a real temp-dir <see cref="JsonFileStateStore"/> so the JSON round-trip of
/// <see cref="CommentItem"/> is exercised end-to-end. Mirrors <see cref="TaskCacheTests"/>.
/// </summary>
public sealed class FeedCacheTests : IDisposable
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

    private static CommentItem Comment(
        string id, string author = "Ben", long? dateMs = 1000, bool mentionsMe = false,
        string? taskId = "t1", IReadOnlyList<long>? mentioned = null)
        => new(id, author, dateMs, $"body of {id}", Resolved: false, TaskId: taskId,
            MentionsMe: mentionsMe, MentionedUserIds: mentioned);

    // --- round-trip ------------------------------------------------------------------------------

    [Fact]
    public void Save_ThenLoad_RoundTripsFeed_IncludingMentionMetadata()
    {
        var cache = new FeedCache(Store());
        var config = Config();
        var feed = new[]
        {
            Comment("c1", author: "Ada", dateMs: 2000, mentionsMe: true, taskId: "task-a", mentioned: [42, 7]),
            Comment("c2", author: "Ben", dateMs: 1000, mentionsMe: false, taskId: "task-b"),
        };

        cache.Save(config, feed);
        var loaded = cache.Load(config);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Equal("c1", loaded[0].Id);
        Assert.Equal("Ada", loaded[0].Author);
        Assert.Equal(2000, loaded[0].DateMs);
        Assert.True(loaded[0].MentionsMe);
        Assert.Equal("task-a", loaded[0].TaskId);
        Assert.Equal([42L, 7L], loaded[0].MentionedUserIds);
        Assert.False(loaded[1].MentionsMe);
        Assert.Empty(loaded[1].MentionedUserIds);
    }

    [Fact]
    public void Load_WhenNothingCached_ReturnsNull()
        => Assert.Null(new FeedCache(Store()).Load(Config()));

    [Fact]
    public void Load_WhenEmptyFeedWasCached_ReturnsEmptyNotNull()
    {
        // A genuinely-empty feed is distinct from a miss: the caller shouldn't fall back to a stale
        // paint just because the last aggregation had zero comments.
        var cache = new FeedCache(Store());
        var config = Config();

        cache.Save(config, []);
        var loaded = cache.Load(config);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!);
    }

    // --- context keying (the "never show the wrong feed" guarantee) ------------------------------

    [Fact]
    public void Load_WhenWorkspaceDiffers_ReturnsNull()
    {
        var cache = new FeedCache(Store());
        cache.Save(Config(workspace: "ws-A"), [Comment("c1")]);

        Assert.Null(cache.Load(Config(workspace: "ws-B")));
    }

    [Fact]
    public void Load_WhenAssigneeScopeDiffers_ReturnsNull()
    {
        var cache = new FeedCache(Store());
        var mine = new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] };
        var teammate = new ViewSettings
        {
            Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "99" }],
        };

        cache.Save(Config(view: mine), [Comment("c1")]);

        Assert.Null(cache.Load(Config(view: teammate)));
    }

    [Fact]
    public void Load_WhenOnlyPersonalListDiffers_StillHits()
    {
        // The feed is built from assigned tasks only, never the Personal Tasks list, so a pure
        // personal-list change must NOT invalidate the cache (unlike TaskCache).
        var cache = new FeedCache(Store());
        cache.Save(Config(list: "list-A"), [Comment("c1")]);

        var loaded = cache.Load(Config(list: "list-B"));
        Assert.NotNull(loaded);
        Assert.Equal("c1", loaded!.Single().Id);
    }

    [Fact]
    public void Load_WhenDocumentIsCorrupt_ReturnsNullNotThrow()
    {
        // A truncated / garbage feed.json (e.g. a quit or crash mid-write) must degrade to a miss, not
        // throw — a stale paint fallback is exactly the safe degradation the cache is meant to have.
        var store = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor(StateKeys.Feed), "{ \"items\": [ {\"id\": \"c1\""); // truncated

        Assert.Null(new FeedCache(store).Load(Config()));
    }

    [Fact]
    public void Load_WhenSchemaVersionMismatch_ReturnsNull()
    {
        // A document written by an incompatible (future) schema is discarded rather than trusted.
        var store = Store();
        var config = Config();
        store.Save(StateKeys.Feed, new FeedCacheDocument
        {
            SchemaVersion = FeedCache.CurrentSchemaVersion + 1,
            Key = FeedCache.KeyFor(config),
            Items = [Comment("c1")],
        });

        Assert.Null(new FeedCache(store).Load(config));
    }

    [Fact]
    public void Load_WhenPriorSchemaVersion_ReturnsNull()
    {
        // The #124 bump (1 → 2, adding the capture timestamp) must discard a pre-#124 v1 document rather
        // than paint it with a fabricated (zero) age.
        var store = Store();
        var config = Config();
        store.Save(StateKeys.Feed, new FeedCacheDocument
        {
            SchemaVersion = 1,
            Key = FeedCache.KeyFor(config),
            Items = [Comment("c1")],
        });

        Assert.Null(new FeedCache(store).Load(config));
    }

    // --- staleness / TTL / eviction (#124) -------------------------------------------------------

    [Fact]
    public void LoadSnapshot_ReturnsTheCaptureTime()
    {
        var clock = NewClock();
        var store = Store();
        var config = Config();
        new FeedCache(store, clock).Save(config, [Comment("c1")]);

        clock.Advance(TimeSpan.FromMinutes(5));
        var snapshot = new FeedCache(store, clock).LoadSnapshot(config);

        Assert.NotNull(snapshot);
        Assert.Equal("c1", snapshot!.Items.Single().Id);
        Assert.Equal(TimeSpan.FromMinutes(5), clock.GetUtcNow() - snapshot.CapturedAt);
    }

    [Fact]
    public void Load_WhenWithinMaxAge_Hits()
    {
        var clock = NewClock();
        var store = Store();
        var config = Config();
        var maxAge = TimeSpan.FromDays(14);
        new FeedCache(store, clock, maxAge).Save(config, [Comment("c1")]);

        clock.Advance(maxAge - TimeSpan.FromSeconds(1)); // just inside the window
        var loaded = new FeedCache(store, clock, maxAge).Load(config);

        Assert.NotNull(loaded);
        Assert.Equal("c1", loaded!.Single().Id);
    }

    [Fact]
    public void Load_WhenOlderThanMaxAge_ReturnsNullAndPrunesTheStaleDocument()
    {
        var clock = NewClock();
        var store = Store();
        var config = Config();
        var maxAge = TimeSpan.FromDays(14);
        new FeedCache(store, clock, maxAge).Save(config, [Comment("c1")]);

        clock.Advance(maxAge + TimeSpan.FromSeconds(1)); // just past the window
        var loaded = new FeedCache(store, clock, maxAge).Load(config);

        Assert.Null(loaded);
        Assert.False(store.Exists(StateKeys.Feed)); // self-pruned, not left to linger
    }

    [Fact]
    public void Load_WhenTimestampIsOutOfRange_ReturnsNullAndPrunes()
    {
        // A hand-tampered file with a nonsense timestamp must degrade to a miss (and get pruned), never
        // throw on the pre-UI-loop load path.
        var store = Store();
        var config = Config();
        store.Save(StateKeys.Feed, new FeedCacheDocument
        {
            SchemaVersion = FeedCache.CurrentSchemaVersion,
            Key = FeedCache.KeyFor(config),
            CapturedAtMs = long.MaxValue,
            Items = [Comment("c1")],
        });

        Assert.Null(new FeedCache(store).Load(config));
        Assert.False(store.Exists(StateKeys.Feed));
    }

    // --- supersede / clear -----------------------------------------------------------------------

    [Fact]
    public void Save_OverwritesPriorDocument()
    {
        var cache = new FeedCache(Store());
        var config = Config();

        cache.Save(config, [Comment("c1")]);
        cache.Save(config, [Comment("c2"), Comment("c3")]);

        var loaded = cache.Load(config);
        Assert.NotNull(loaded);
        Assert.Equal(["c2", "c3"], loaded!.Select(c => c.Id));
    }

    [Fact]
    public void Clear_RemovesDocument()
    {
        var cache = new FeedCache(Store());
        var config = Config();
        cache.Save(config, [Comment("c1")]);

        cache.Clear();

        Assert.Null(cache.Load(config));
    }

    // --- KeyFor purity / design contract ---------------------------------------------------------

    [Fact]
    public void KeyFor_IsStable_ForTheSameConfig()
        => Assert.Equal(FeedCache.KeyFor(Config()), FeedCache.KeyFor(Config()));

    [Fact]
    public void KeyFor_ChangesWith_Workspace_And_AssigneeScope()
    {
        var baseKey = FeedCache.KeyFor(Config());
        Assert.NotEqual(baseKey, FeedCache.KeyFor(Config(workspace: "other")));

        var teammate = new ViewSettings
        {
            Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "99" }],
        };
        Assert.NotEqual(baseKey, FeedCache.KeyFor(Config(view: teammate)));
    }

    [Fact]
    public void KeyFor_ChangesWith_FeedShowCompleted()
    {
        // The feed's F12 toggle changes which tasks are fetched (open-only vs open + closed), so a cache
        // captured under one setting must not instant-paint under the other.
        var open = Config();
        var completed = Config();
        completed.FeedShowCompleted = true;

        Assert.NotEqual(FeedCache.KeyFor(open), FeedCache.KeyFor(completed));
    }

    [Fact]
    public void KeyFor_IsUnaffectedBy_PersonalList_Sort_Group_AndNonAssigneeFilters()
    {
        // The aggregated feed depends only on the workspace + assignee scope, so it stays valid across
        // a personal-list change or a pure client-side sort/group/filter tweak between sessions.
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

        Assert.Equal(FeedCache.KeyFor(Config(list: "list-A", view: plain)), FeedCache.KeyFor(Config(list: "list-B", view: tweaked)));
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

        Assert.Equal(FeedCache.KeyFor(Config(view: upper)), FeedCache.KeyFor(Config(view: lower)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
