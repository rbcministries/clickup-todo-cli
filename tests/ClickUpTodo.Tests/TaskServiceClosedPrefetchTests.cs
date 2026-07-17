using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The warm closed-task prefetch (#253): <see cref="TaskService.PrefetchClosedTasksAsync"/> fetches
/// with <c>include_closed=true</c>, keeps only closed-type tasks, and — crucially — never advances the
/// delta baseline the open-task poll relies on. Plus the pure <see cref="TaskService.SupplementWithClosed"/>
/// bridge merge. Driven through a fake <see cref="IClickUpClient"/> that models the server dropping
/// closed tasks when <c>include_closed=false</c>.
/// </summary>
public sealed class TaskServiceClosedPrefetchTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static TaskItem Open(string id, long updated)
        => new() { Id = id, Name = id, UpdatedMs = updated, StatusType = "open" };

    private static TaskItem Closed(string id, long updated)
        => new() { Id = id, Name = id, UpdatedMs = updated, StatusType = "closed" };

    /// <summary>A closed task dated <paramref name="daysAgo"/> before the fixed test clock, so it sits
    /// inside the cache's default 30-day age window.</summary>
    private static TaskItem ClosedRecent(string id, int daysAgo)
        => Closed(id, Now.AddDays(-daysAgo).ToUnixTimeMilliseconds());

    private sealed class FakeClient : IClickUpClient
    {
        public List<TaskItem> AssignedOpen { get; set; } = [];
        public List<TaskItem> PersonalOpen { get; set; } = [];
        public List<TaskItem> AssignedAll { get; set; } = [];
        public List<TaskItem> PersonalAll { get; set; } = [];

        public List<bool> AssignedIncludeClosedCalls { get; } = [];
        public List<bool> PersonalIncludeClosedCalls { get; } = [];
        public List<long> AssignedDeltaSince { get; } = [];
        public List<long> PersonalDeltaSince { get; } = [];

        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, CancellationToken ct = default)
        {
            AssignedIncludeClosedCalls.Add(includeClosed);
            return Task.FromResult(includeClosed ? AssignedAll : AssignedOpen);
        }

        public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default)
        {
            PersonalIncludeClosedCalls.Add(includeClosed);
            return Task.FromResult(includeClosed ? PersonalAll : PersonalOpen);
        }

        public Task<List<TaskItem>> GetAssignedTasksDeltaAsync(string workspaceId, IReadOnlyList<long> assigneeIds, long updatedAfterMs, CancellationToken ct = default)
        {
            AssignedDeltaSince.Add(updatedAfterMs);
            return Task.FromResult(new List<TaskItem>());
        }

        public Task<List<TaskItem>> GetListTasksDeltaAsync(string listId, long updatedAfterMs, CancellationToken ct = default)
        {
            PersonalDeltaSince.Add(updatedAfterMs);
            return Task.FromResult(new List<TaskItem>());
        }

        // Unused by the paths under test.
        public Task<ClickUpUser> GetMeAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetWorkspacesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkspaceMember>> GetWorkspaceMembersAsync(string workspaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetSpacesAsync(string workspaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetFoldersAsync(string spaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetFolderlessListsAsync(string spaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetListsInFolderAsync(string folderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NamedEntity> GetListAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetListColorAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<StatusOption>> GetListStatusesAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static TaskService Service(FakeClient fake)
        => new(fake, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "pl" }, userId: 1,
            timeProvider: new FakeClock(Now));

    [Fact]
    public async Task Prefetch_FetchesWithIncludeClosed_AndKeepsOnlyClosedType()
    {
        var fake = new FakeClient
        {
            AssignedAll = [Open("a", Now.AddDays(-1).ToUnixTimeMilliseconds()), ClosedRecent("c1", 2)],
            PersonalAll = [ClosedRecent("c2", 3)],
        };
        var service = Service(fake);

        var dropped = await service.PrefetchClosedTasksAsync();

        Assert.Equal(0, dropped);
        Assert.Equal([true], fake.AssignedIncludeClosedCalls);
        Assert.Equal([true], fake.PersonalIncludeClosedCalls);
        // Only closed-type survive; the open "a" is filtered out.
        Assert.Equal(new[] { "c1", "c2" }.OrderBy(x => x), service.WarmClosedTasks.Select(t => t.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Prefetch_DoesNotAdvanceDeltaWatermark()
    {
        // Baseline the delta state from an open-only full load whose newest task is at t=5_000_000.
        // Both timestamps sit comfortably above DeltaSkewMs (60_000) so the correct and the leaked
        // watermarks produce *different* delta `since` values — otherwise both would floor to 0 and the
        // assertion couldn't tell them apart (the whole point of this test).
        const long openMs = 5_000_000;
        const long closedMs = 9_000_000;
        var fake = new FakeClient
        {
            AssignedOpen = [Open("a", openMs)],
            // The closed set is *newer* (closing bumps date_updated); if the prefetch leaked into the
            // watermark, the next delta would query from ~closedMs instead of ~openMs and skip real churn.
            AssignedAll = [Open("a", openMs), Closed("c1", closedMs)],
        };
        var service = Service(fake);
        await service.LoadAsync(); // sets watermark to openMs

        await service.PrefetchClosedTasksAsync(); // must NOT move the watermark to closedMs

        await service.LoadSnapshotAsync(preferDelta: true);

        // since derives from the open watermark (openMs), never the closed task's closedMs. A leak would
        // make this closedMs - DeltaSkewMs = 8_940_000 instead.
        var expectedSince = openMs - TaskService.DeltaSkewMs; // 4_940_000
        Assert.Equal(expectedSince, Assert.Single(fake.AssignedDeltaSince));
        Assert.Equal(expectedSince, Assert.Single(fake.PersonalDeltaSince));
    }

    [Fact]
    public async Task SupplementWithClosed_MergesWarmSet_SnapshotWinsCollisions()
    {
        var fake = new FakeClient { AssignedAll = [ClosedRecent("c1", 2), ClosedRecent("dup", 3)] };
        var service = Service(fake);
        await service.PrefetchClosedTasksAsync();

        // "dup" also lives in the live snapshot (as the fresher copy) — snapshot must win.
        var live = new[] { Open("live", 500), Open("dup", 999) };
        var merged = service.SupplementWithClosed(live);

        Assert.Contains(merged, t => t.Id == "c1");
        Assert.Contains(merged, t => t.Id == "live");
        // "dup" appears once, and it's the live (open, updated 999) copy, not the cached closed one.
        var dup = Assert.Single(merged, t => t.Id == "dup");
        Assert.Equal("open", dup.StatusType);
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void SupplementWithClosed_EmptyCache_ReturnsSameInstance()
    {
        var service = Service(new FakeClient());
        var live = new[] { Open("a", 1) };

        var merged = service.SupplementWithClosed(live);

        Assert.Same(live, merged);
    }

    [Fact]
    public async Task SupplementWithClosed_AllClosedAlreadyPresent_ReturnsSameInstance()
    {
        var fake = new FakeClient { AssignedAll = [ClosedRecent("c1", 2)] };
        var service = Service(fake);
        await service.PrefetchClosedTasksAsync();
        Assert.Single(service.WarmClosedTasks); // cache really holds c1 — exercising the "all present" path

        var live = new[] { Open("x", 1), Open("c1", 999) }; // c1 already in the snapshot
        var merged = service.SupplementWithClosed(live);

        Assert.Same(live, merged);
    }
}
