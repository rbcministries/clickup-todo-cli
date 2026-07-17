using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="FeedService"/>'s offline cores (#112): the pure <c>Aggregate</c>
/// (merge / de-dup / newest-first order / cap) and the delegate-driven <c>GatherAsync</c> fan-out
/// (bounded concurrency, best-effort per task, cancellation propagation). The live
/// <c>LoadFeedAsync</c> glue is exercised by the app; here we pin the decision logic without a
/// ClickUp client.
/// </summary>
public sealed class FeedServiceTests
{
    private static CommentItem C(string id, long? date, string? taskId = null)
        => new(id, "author", date, "text", false, taskId);

    // ── Aggregate ───────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_MergesTasksIntoOneNewestFirstList()
    {
        var result = FeedService.Aggregate(
            new[]
            {
                new[] { C("a", 100), C("b", 300) },
                new[] { C("c", 200) },
            },
            maxEntries: 100);

        Assert.Equal(new[] { "b", "c", "a" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Aggregate_DedupsByIdKeepingFirst_AndPreservesDistinctEmptyIds()
    {
        var first = new CommentItem("dup", "first", 100, "t", false, "task1");
        var second = new CommentItem("dup", "second", 100, "t", false, "task2");
        var blank1 = new CommentItem("", "x", 50, "t", false, "task1");
        var blank2 = new CommentItem("", "y", 40, "t", false, "task2");

        var result = FeedService.Aggregate(
            new[] { new[] { first, blank1 }, new[] { second, blank2 } }, maxEntries: 100);

        // The shared "dup" id collapses to its first occurrence; both empty-id comments survive.
        Assert.Equal(3, result.Count);
        Assert.Equal("first", Assert.Single(result, c => c.Id == "dup").Author);
        Assert.Equal(2, result.Count(c => c.Id == ""));
    }

    [Fact]
    public void Aggregate_NullDateSortsLast_AndTiesAreDeterministic()
    {
        var result = FeedService.Aggregate(
            new[] { new[] { C("z", null), C("m", 100), C("a", 100), C("n", null) } },
            maxEntries: 100);

        // Dated comments first (100 tie → id ordinal a, m), then undated (id ordinal n, z).
        Assert.Equal(new[] { "a", "m", "n", "z" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Aggregate_CapsToTheNewestEntries()
    {
        var result = FeedService.Aggregate(
            new[] { new[] { C("old", 1), C("mid", 2), C("new", 3) } }, maxEntries: 2);

        Assert.Equal(new[] { "new", "mid" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Aggregate_NonPositiveCap_ReturnsEmpty()
    {
        var input = new[] { new[] { C("a", 1) } };
        Assert.Empty(FeedService.Aggregate(input, maxEntries: 0));
        Assert.Empty(FeedService.Aggregate(input, maxEntries: -5));
    }

    [Fact]
    public void Aggregate_EmptyOrNullInput_ReturnsEmpty()
    {
        Assert.Empty(FeedService.Aggregate(Array.Empty<IReadOnlyList<CommentItem>>(), maxEntries: 100));
        Assert.Empty(FeedService.Aggregate(new IReadOnlyList<CommentItem>?[] { null }, maxEntries: 100));
    }

    // ── GatherAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GatherAsync_EmptyTaskIds_ReturnsEmptyWithoutFetching()
    {
        var feed = await FeedService.GatherAsync(
            [],
            (_, _) => throw new InvalidOperationException("fetch should not be called"),
            maxConcurrency: 4,
            maxEntries: 100);

        Assert.Empty(feed);
    }

    [Fact]
    public async Task GatherAsync_MergesDedupsAndCapsAcrossTasks()
    {
        var byTask = new Dictionary<string, IReadOnlyList<CommentItem>>(StringComparer.Ordinal)
        {
            ["a"] = new[] { C("c1", 30), C("dup", 10) },
            ["b"] = new[] { C("c2", 20), C("dup", 10) }, // "dup" shared across the two tasks
        };
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch =
            (id, _) => Task.FromResult(byTask[id]);

        var feed = await FeedService.GatherAsync(new[] { "a", "b" }, fetch, maxConcurrency: 4, maxEntries: 2);

        // dedup → c1(30), c2(20), dup(10); newest-2 cap drops dup.
        Assert.Equal(new[] { "c1", "c2" }, feed.Select(c => c.Id));
    }

    [Fact]
    public async Task GatherAsync_SkipsTasksWhoseFetchThrows()
    {
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch = (id, _) =>
            id == "boom"
                ? throw new InvalidOperationException("transient")
                : Task.FromResult<IReadOnlyList<CommentItem>>(new[] { C($"c-{id}", 1) });

        var feed = await FeedService.GatherAsync(
            new[] { "ok1", "boom", "ok2" }, fetch, maxConcurrency: 4, maxEntries: 100);

        Assert.Equal(new[] { "c-ok1", "c-ok2" }, feed.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GatherAsync_PropagatesGenuineCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // caller cancelled (e.g. app shutdown) — must not be swallowed as best-effort
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch =
            (id, _) => Task.FromResult<IReadOnlyList<CommentItem>>(new[] { C($"c-{id}", 1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeedService.GatherAsync(new[] { "t1" }, fetch, maxConcurrency: 4, maxEntries: 100, cts.Token));
    }

    [Fact]
    public async Task GatherAsync_TreatsPerTaskTimeoutAsBestEffort()
    {
        // An HttpClient timeout surfaces as a TaskCanceledException (an OperationCanceledException)
        // even though the caller's token was never signalled; one slow task must not abort the feed.
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch = (id, _) =>
            id == "slow"
                ? throw new TaskCanceledException()
                : Task.FromResult<IReadOnlyList<CommentItem>>(new[] { C($"c-{id}", 1) });

        var feed = await FeedService.GatherAsync(
            new[] { "ok1", "slow", "ok2" }, fetch, maxConcurrency: 4, maxEntries: 100);

        Assert.Equal(new[] { "c-ok1", "c-ok2" }, feed.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GatherAsync_NeverExceedsMaxConcurrency()
    {
        const int maxConcurrency = 3;
        var ids = Enumerable.Range(0, 10).Select(i => $"t{i}").ToList();

        var current = 0;
        var peak = 0;
        var peakLock = new object();
        var reachedBound = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch = async (id, _) =>
        {
            var inFlight = Interlocked.Increment(ref current);
            lock (peakLock)
                peak = Math.Max(peak, inFlight);
            if (inFlight >= maxConcurrency)
                reachedBound.TrySetResult();
            await release.Task; // hold every admitted fetch open until the test releases them
            Interlocked.Decrement(ref current);
            return new[] { C(id, 1) };
        };

        var run = FeedService.GatherAsync(ids, fetch, maxConcurrency, maxEntries: 100);

        await reachedBound.Task;   // the gate admitted a full batch of `maxConcurrency`
        await Task.Delay(50);      // a broken bound would let more slip in during this window
        Assert.Equal(maxConcurrency, peak);

        release.SetResult();
        var feed = await run;
        Assert.Equal(10, feed.Count);
    }

    // ── BuildActivity (#117) ──────────────────────────────────────────────────

    private static TaskItem T(string id, long? updatedMs, string name = "Task", string? statusType = "open")
        => new() { Id = id, Name = name, StatusName = "in progress", StatusType = statusType, UpdatedMs = updatedMs };

    [Fact]
    public void BuildActivity_ProjectsTasksNewestFirst()
    {
        var activity = FeedService.BuildActivity(
            new[] { T("a", 100), T("b", 300), T("c", 200) }, maxEntries: 100);

        Assert.Equal(new[] { "b", "c", "a" }, activity.Select(a => a.TaskId));
        // Ids are namespaced apart from comment ids so the merged feed can't collide.
        Assert.All(activity, a => Assert.StartsWith(ActivityItem.IdPrefix, a.Id));
    }

    [Fact]
    public void BuildActivity_CarriesTheTaskFieldsTheRowNeeds()
    {
        var activity = FeedService.BuildActivity(
            new[] { T("t9", 100, name: "Ship it") }, maxEntries: 100);

        var only = Assert.Single(activity);
        Assert.Equal("t9", only.TaskId);
        Assert.Equal("Ship it", only.TaskName);
        Assert.Equal("in progress", only.StatusName);
        Assert.Equal(100, only.UpdatedMs);
    }

    [Fact]
    public void BuildActivity_UndatedTasksSortLast_TiesBrokenByTaskId()
    {
        var activity = FeedService.BuildActivity(
            new[] { T("z", null), T("m", 100), T("a", 100), T("n", null) }, maxEntries: 100);

        // Dated first (100 tie → task-id ordinal a, m), then undated (id ordinal n, z).
        Assert.Equal(new[] { "a", "m", "n", "z" }, activity.Select(a => a.TaskId));
    }

    [Fact]
    public void BuildActivity_CapsToTheNewestEntries()
    {
        var activity = FeedService.BuildActivity(
            new[] { T("old", 1), T("mid", 2), T("new", 3) }, maxEntries: 2);

        Assert.Equal(new[] { "new", "mid" }, activity.Select(a => a.TaskId));
    }

    [Fact]
    public void BuildActivity_SkipsIdlessTasks_AndHandlesEmptyOrNonPositiveCap()
    {
        var withBlank = new[] { T("a", 1), T("", 2) };
        Assert.Equal(new[] { "a" }, FeedService.BuildActivity(withBlank, maxEntries: 100).Select(a => a.TaskId));

        Assert.Empty(FeedService.BuildActivity(Array.Empty<TaskItem>(), maxEntries: 100));
        Assert.Empty(FeedService.BuildActivity(new[] { T("a", 1) }, maxEntries: 0));
        Assert.Empty(FeedService.BuildActivity(new[] { T("a", 1) }, maxEntries: -3));
    }

    [Fact]
    public void BuildActivity_DeDupesByTaskId_KeepingOne()
    {
        // Paging with subtasks=true can return the same task twice; the resulting activity ids
        // ("activity:" + taskId) would otherwise collide and produce duplicate rows.
        var activity = FeedService.BuildActivity(
            new[] { T("a", 100), T("a", 100), T("b", 50) }, maxEntries: 100);

        Assert.Equal(new[] { "a", "b" }, activity.Select(a => a.TaskId));
        Assert.Single(activity, a => a.TaskId == "a");
    }

    [Fact]
    public void BuildActivity_ExcludesCompletedTasks_WhenCompletedNotIncluded()
    {
        // A closed-type task can arrive as a subtask anchor even with include_closed=false; when the F12
        // completed bound is off (includeCompleted:false) it must be dropped, mirroring TaskView.Apply.
        var tasks = new[] { T("open", 100, statusType: "open"), T("closed", 200, statusType: "closed") };

        var hidden = FeedService.BuildActivity(tasks, maxEntries: 100, includeCompleted: false);
        Assert.Equal(new[] { "open" }, hidden.Select(a => a.TaskId));

        // With the bound on (F12), the closed task's activity is included (newest-first).
        var shown = FeedService.BuildActivity(tasks, maxEntries: 100, includeCompleted: true);
        Assert.Equal(new[] { "closed", "open" }, shown.Select(a => a.TaskId));
    }

    // ── StampMentions (#113) ──────────────────────────────────────────────────

    private static readonly MentionSpec BenSpec = MentionSpec.ForUser(new ClickUpUser(7, "Ben"));

    private static CommentItem Msg(string id, string text)
        => new(id, "author", 100, text, false, "task1");

    [Fact]
    public void StampMentions_SetsMentionsMePerEntry()
    {
        var feed = new[]
        {
            Msg("a", "cc @Ben please look"),
            Msg("b", "unrelated comment"),
        };

        var stamped = FeedService.StampMentions(feed, BenSpec, mentionsOnly: false);

        Assert.Equal(new[] { "a", "b" }, stamped.Select(c => c.Id));
        Assert.True(stamped.Single(c => c.Id == "a").MentionsMe);
        Assert.False(stamped.Single(c => c.Id == "b").MentionsMe);
    }

    [Fact]
    public void StampMentions_MentionsOnly_FiltersToMentionsPreservingOrder()
    {
        var feed = new[]
        {
            Msg("a", "@Ben first"),
            Msg("b", "no mention here"),
            Msg("c", "and @Ben again"),
        };

        var stamped = FeedService.StampMentions(feed, BenSpec, mentionsOnly: true);

        Assert.Equal(new[] { "a", "c" }, stamped.Select(c => c.Id));
        Assert.All(stamped, c => Assert.True(c.MentionsMe));
    }

    [Fact]
    public void StampMentions_EmptyFeed_YieldsEmpty()
    {
        Assert.Empty(FeedService.StampMentions(Array.Empty<CommentItem>(), BenSpec, mentionsOnly: true));
        Assert.Empty(FeedService.StampMentions(Array.Empty<CommentItem>(), BenSpec, mentionsOnly: false));
    }

    [Fact]
    public void StampMentions_EmptySpec_MarksNothingAndMentionsOnlyIsEmpty()
    {
        var feed = new[] { Msg("a", "@Ben here"), Msg("b", "plain") };

        var all = FeedService.StampMentions(feed, MentionSpec.None, mentionsOnly: false);
        Assert.All(all, c => Assert.False(c.MentionsMe));

        Assert.Empty(FeedService.StampMentions(feed, MentionSpec.None, mentionsOnly: true));
    }

    [Fact]
    public void StampMentions_FlagsEntryByMentionedUserId_WithNoHandleInText()
    {
        // BenSpec carries id 7 (ClickUpUser(7, "Ben")); entry "a" has no matchable @handle in its text
        // but a structured block referenced Ben's id, so the feed flags it via the #167 id path.
        var feed = new[]
        {
            new CommentItem("a", "author", 100, "please review the change", false, "task1", MentionedUserIds: new long[] { 7 }),
            Msg("b", "no mention and no id"),
        };

        var stamped = FeedService.StampMentions(feed, BenSpec, mentionsOnly: false);

        Assert.True(stamped.Single(c => c.Id == "a").MentionsMe);
        Assert.False(stamped.Single(c => c.Id == "b").MentionsMe);
    }

    // ── ComputeUpdatedAfterMs (look-back window, #244) ───────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void ComputeUpdatedAfterMs_Disabled_WhenZeroOrNegative(int lookbackDays)
    {
        // 0 = off (the default); a non-positive value (e.g. hand-edited config) is treated as off too,
        // so the caller omits the filter and fetches the full assigned set.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        Assert.Null(FeedService.ComputeUpdatedAfterMs(lookbackDays, now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(90)]
    public void ComputeUpdatedAfterMs_ReturnsWindowStart_WhenPositive(int lookbackDays)
    {
        const long nowMs = 1_700_000_000_000;
        const long msPerDay = 86_400_000;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);

        var result = FeedService.ComputeUpdatedAfterMs(lookbackDays, now);

        Assert.Equal(nowMs - lookbackDays * msPerDay, result);
        Assert.Equal(now.AddDays(-lookbackDays).ToUnixTimeMilliseconds(), result);
    }

    [Fact]
    public void ComputeUpdatedAfterMs_ClampsHugeValue_WithoutOverflowing()
    {
        // A hand-edited config can bypass the UI clamp (SettingsForm.MaxLookbackDays); an unbounded
        // AddDays(-huge) would throw ArgumentOutOfRangeException and break the feed load. The compute
        // clamps to MaxLookbackDays instead.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        var result = FeedService.ComputeUpdatedAfterMs(int.MaxValue, now);

        Assert.Equal(now.AddDays(-FeedService.MaxLookbackDays).ToUnixTimeMilliseconds(), result);
    }

    // ── LoadFeedAsync wiring (window threaded into the fetch, #244) ───────────

    [Theory]
    [InlineData(0)]   // disabled → no window passed to the fetch
    [InlineData(7)]   // enabled → computed window passed through
    public async Task LoadFeedAsync_ThreadsConfiguredLookbackWindow_IntoAssignedFetch(int lookbackDays)
    {
        var now = new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);
        var client = new CapturingClient();
        var config = new AppConfig
        {
            WorkspaceId = "ws-1",
            FeedActivityLookbackDays = lookbackDays,
            View = new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] },
        };
        // "Assignee IS me" resolves to [userId] with no client call, so the fetch is the only client hit.
        var taskService = new TaskService(client, config, userId: 42);
        var feed = new FeedService(client, taskService, config, new FixedClock(now));

        await feed.LoadFeedAsync(includeClosed: false);

        Assert.Equal(FeedService.ComputeUpdatedAfterMs(lookbackDays, now), client.LastUpdatedAfterMs);
    }

    /// <summary>A TimeProvider pinned to a fixed instant.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A read-only <see cref="IClickUpClient"/> fake that records the <c>updatedAfterMs</c> the feed
    /// passed to the assigned-task fetch and returns an empty task set (so the comment fan-out and
    /// activity projection are trivially empty). Every other member is unused by this flow.
    /// </summary>
    private sealed class CapturingClient : IClickUpClient
    {
        public long? LastUpdatedAfterMs { get; private set; }

        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, long? updatedAfterMs = null, CancellationToken ct = default)
        {
            LastUpdatedAfterMs = updatedAfterMs;
            return Task.FromResult(new List<TaskItem>());
        }

        public Task<ClickUpUser> GetMeAsync(CancellationToken ct = default) => Task.FromResult(new ClickUpUser(42, "me"));

        public Task<IReadOnlyList<NamedEntity>> GetWorkspacesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkspaceMember>> GetWorkspaceMembersAsync(string workspaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetSpacesAsync(string workspaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetFoldersAsync(string spaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetFolderlessListsAsync(string spaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetListsInFolderAsync(string folderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NamedEntity> GetListAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetListColorAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<StatusOption>> GetListStatusesAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
