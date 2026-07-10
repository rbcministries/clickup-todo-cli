using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for the incremental refresh (#194): the pure <see cref="TaskService.MergeDelta"/> merge, the
/// watermark math, and <see cref="TaskService.LoadSnapshotAsync"/>'s full-vs-delta decision driven
/// through a fake <see cref="IClickUpClient"/> — no generated client, no token.
/// </summary>
public sealed class TaskServiceDeltaRefreshTests
{
    private static TaskItem Item(string id, long? updated = null, long? due = null, string? statusType = "open")
        => new() { Id = id, Name = id, UpdatedMs = updated, DueDateMs = due, StatusType = statusType };

    private static TaskItem Closed(string id, long? updated = null)
        => Item(id, updated, statusType: "closed");

    // ── MergeDelta (pure) ────────────────────────────────────────────────────

    [Fact]
    public void MergeDelta_EmptyDelta_ReturnsSameInstanceUnchanged()
    {
        var previous = new[] { Item("a"), Item("b") };

        var (tasks, changed) = TaskService.MergeDelta(previous, []);

        Assert.False(changed);
        Assert.Same(previous, tasks);
    }

    [Fact]
    public void MergeDelta_UpsertsUpdatedTask()
    {
        var previous = new[] { Item("a"), Item("b") };
        var updated = Item("a") with { Name = "a (renamed)" };

        var (tasks, changed) = TaskService.MergeDelta(previous, [updated]);

        Assert.True(changed);
        Assert.Equal("a (renamed)", tasks.Single(t => t.Id == "a").Name);
        Assert.Equal(2, tasks.Count);
    }

    [Fact]
    public void MergeDelta_InsertsNewTask_InDueDateOrder()
    {
        var previous = new[] { Item("a", due: 1), Item("c", due: 3) };

        var (tasks, changed) = TaskService.MergeDelta(previous, [Item("b", due: 2)]);

        Assert.True(changed);
        Assert.Equal(["a", "b", "c"], tasks.Select(t => t.Id));
    }

    [Fact]
    public void MergeDelta_RemovesTaskThatClosed()
    {
        var previous = new[] { Item("a"), Item("b") };

        var (tasks, changed) = TaskService.MergeDelta(previous, [Closed("b")]);

        Assert.True(changed);
        Assert.Equal(["a"], tasks.Select(t => t.Id));
    }

    [Fact]
    public void MergeDelta_ClosedTaskNeverPresent_Unchanged()
    {
        var previous = new[] { Item("a") };

        var (tasks, changed) = TaskService.MergeDelta(previous, [Closed("zzz")]);

        Assert.False(changed);
        Assert.Same(previous, tasks);
    }

    [Fact]
    public void MaxUpdatedMs_IgnoresNulls_NullWhenNone()
    {
        Assert.Equal(9, TaskService.MaxUpdatedMs([Item("a", 4), Item("b"), Item("c", 9)]));
        Assert.Null(TaskService.MaxUpdatedMs([Item("a"), Item("b")]));
    }

    // ── LoadSnapshotAsync (fake client) ──────────────────────────────────────

    /// <summary>Fake with scripted full + delta results; delta calls record their watermark argument.
    /// Unused paths throw so accidental reliance is loud.</summary>
    private sealed class FakeClient : IClickUpClient
    {
        public List<TaskItem> Assigned { get; set; } = [];
        public List<TaskItem> Personal { get; set; } = [];
        public List<TaskItem> AssignedDelta { get; set; } = [];
        public List<TaskItem> PersonalDelta { get; set; } = [];

        public int FullFetches { get; private set; }
        public List<long> AssignedDeltaSince { get; } = [];
        public List<long> PersonalDeltaSince { get; } = [];

        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, CancellationToken ct = default)
        {
            FullFetches++;
            return Task.FromResult(Assigned);
        }

        public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default)
            => Task.FromResult(Personal);

        public Task<List<TaskItem>> GetAssignedTasksDeltaAsync(string workspaceId, IReadOnlyList<long> assigneeIds, long updatedAfterMs, CancellationToken ct = default)
        {
            AssignedDeltaSince.Add(updatedAfterMs);
            return Task.FromResult(AssignedDelta);
        }

        public Task<List<TaskItem>> GetListTasksDeltaAsync(string listId, long updatedAfterMs, CancellationToken ct = default)
        {
            PersonalDeltaSince.Add(updatedAfterMs);
            return Task.FromResult(PersonalDelta);
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
    }

    private static TaskService Service(FakeClient fake)
        => new(fake, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "pl" }, userId: 1);

    [Fact]
    public async Task FirstLoad_IsAlwaysFull_EvenWhenDeltaPreferred()
    {
        var fake = new FakeClient { Assigned = [Item("a", updated: 1000)] };
        var service = Service(fake);

        var result = await service.LoadSnapshotAsync(preferDelta: true);

        Assert.False(result.WasDelta);
        Assert.True(result.Changed);
        Assert.Equal(1, fake.FullFetches);
        Assert.Empty(fake.AssignedDeltaSince);
        Assert.Equal(["a"], result.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task SecondLoad_UsesDelta_WithSkewedWatermark()
    {
        var fake = new FakeClient { Assigned = [Item("a", updated: 500_000)] };
        var service = Service(fake);
        await service.LoadSnapshotAsync(preferDelta: true);

        fake.AssignedDelta = [Item("b", updated: 600_000, due: 1)];
        var result = await service.LoadSnapshotAsync(preferDelta: true);

        Assert.True(result.WasDelta);
        Assert.True(result.Changed);
        // Watermark 500_000 minus the 60s skew allowance.
        Assert.Equal([500_000 - TaskService.DeltaSkewMs], fake.AssignedDeltaSince);
        Assert.Equal(fake.AssignedDeltaSince, fake.PersonalDeltaSince);
        Assert.Equal(["b", "a"], result.Tasks.Select(t => t.Id)); // due-dated "b" sorts first
        Assert.Equal(1, fake.FullFetches);
    }

    [Fact]
    public async Task EmptyDelta_ReportsUnchanged_AndKeepsSnapshotInstance()
    {
        var fake = new FakeClient { Assigned = [Item("a", updated: 500_000)] };
        var service = Service(fake);
        var full = await service.LoadSnapshotAsync(preferDelta: true);

        var result = await service.LoadSnapshotAsync(preferDelta: true);

        Assert.True(result.WasDelta);
        Assert.False(result.Changed);
        Assert.Same(full.Tasks, result.Tasks);
    }

    [Fact]
    public async Task DeltaAdvancesWatermark_ForTheNextDelta()
    {
        var fake = new FakeClient { Assigned = [Item("a", updated: 500_000)] };
        var service = Service(fake);
        await service.LoadSnapshotAsync(preferDelta: true);

        fake.AssignedDelta = [Item("b", updated: 900_000)];
        await service.LoadSnapshotAsync(preferDelta: true);
        fake.AssignedDelta = [];
        await service.LoadSnapshotAsync(preferDelta: true);

        Assert.Equal(
            [500_000 - TaskService.DeltaSkewMs, 900_000 - TaskService.DeltaSkewMs],
            fake.AssignedDeltaSince);
    }

    [Fact]
    public async Task TaskThatClosed_IsDroppedByTheDeltaPoll()
    {
        var fake = new FakeClient { Assigned = [Item("a", updated: 500_000), Item("b", updated: 500_000)] };
        var service = Service(fake);
        await service.LoadSnapshotAsync(preferDelta: true);

        fake.AssignedDelta = [Closed("b", updated: 600_000)];
        var result = await service.LoadSnapshotAsync(preferDelta: true);

        Assert.True(result.Changed);
        Assert.Equal(["a"], result.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task PreferDeltaFalse_AlwaysFullFetches_AndResetsTheBaseline()
    {
        var fake = new FakeClient { Assigned = [Item("a", updated: 500_000)] };
        var service = Service(fake);
        await service.LoadSnapshotAsync(preferDelta: true);

        fake.Assigned = [Item("z", updated: 700_000)];
        var full = await service.LoadSnapshotAsync(preferDelta: false);

        Assert.False(full.WasDelta);
        Assert.Equal(["z"], full.Tasks.Select(t => t.Id));
        Assert.Equal(2, fake.FullFetches);

        // The next delta baselines on the re-fetched snapshot's watermark.
        var delta = await service.LoadSnapshotAsync(preferDelta: true);
        Assert.True(delta.WasDelta);
        Assert.Equal(700_000 - TaskService.DeltaSkewMs, fake.AssignedDeltaSince.Single());
    }

    [Fact]
    public async Task FullLoadWithoutTimestamps_DisablesDelta_UntilOneProvidesThem()
    {
        // No task carries date_updated ⇒ no watermark ⇒ a "delta" poll must stay a full fetch.
        var fake = new FakeClient { Assigned = [Item("a")] };
        var service = Service(fake);
        await service.LoadSnapshotAsync(preferDelta: true);

        var result = await service.LoadSnapshotAsync(preferDelta: true);

        Assert.False(result.WasDelta);
        Assert.Equal(2, fake.FullFetches);
        Assert.Empty(fake.AssignedDeltaSince);
    }
}
