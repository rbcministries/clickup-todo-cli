using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Executor tests for <see cref="TaskService.ResolveForeignSubtasksAsync"/> (#87), driven through the
/// <see cref="IClickUpClient"/> seam with an in-memory fake — no generated client, no token. These cover
/// what the pure <see cref="SubtaskFetchStrategy"/> and <see cref="TaskService.ForeignDescendants"/> tests
/// can't: how the plan is *executed* — per-parent BFS + recursion, the whole-list branch (incl.
/// <c>includeClosed</c>), pooling/dedup across both, best-effort error skipping, and the total-round-trip
/// budget that bounds a deep/wide subtree and flags truncation.
/// </summary>
public sealed class ResolveForeignSubtasksTests
{
    private static TaskItem Item(string id, string? parent = null, string? list = "L")
        => new() { Id = id, Name = id, ParentId = parent, ListId = list };

    private static TaskService Service(FakeClickUpClient fake)
        => new(fake, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "pl" }, userId: 1);

    private static IReadOnlyList<string> Ids(ForeignSubtaskResolution r) =>
        r.Subtasks.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>Configurable in-memory <see cref="IClickUpClient"/>; only the two fetch paths the executor
    /// uses are real, the rest throw so accidental reliance is loud.</summary>
    private sealed class FakeClickUpClient : IClickUpClient
    {
        public Dictionary<string, IReadOnlyList<TaskItem>> Subtasks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<TaskItem>> Lists { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ThrowOnSubtask { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ThrowOnList { get; } = new(StringComparer.Ordinal);

        public List<string> SubtaskCalls { get; } = [];
        public List<(string ListId, bool IncludeClosed)> ListCalls { get; } = [];

        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default)
        {
            SubtaskCalls.Add(taskId);
            if (ThrowOnSubtask.Contains(taskId))
                throw new InvalidOperationException("boom");
            return Task.FromResult(Subtasks.TryGetValue(taskId, out var v) ? v : (IReadOnlyList<TaskItem>)[]);
        }

        public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default)
        {
            ListCalls.Add((listId, includeClosed));
            if (ThrowOnList.Contains(listId))
                throw new InvalidOperationException("boom");
            return Task.FromResult(Lists.TryGetValue(listId, out var v) ? v : []);
        }

        // Unused by the executor under test.
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
        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task EmptySnapshot_NoFetches_NotTruncated()
    {
        var fake = new FakeClickUpClient();

        var result = await Service(fake).ResolveForeignSubtasksAsync([]);

        Assert.Empty(result.Subtasks);
        Assert.False(result.Truncated);
        Assert.Empty(fake.SubtaskCalls);
        Assert.Empty(fake.ListCalls);
    }

    [Fact]
    public async Task FewParents_PerParent_PullsForeignChild_NoWholeListCall()
    {
        var fake = new FakeClickUpClient();
        fake.Subtasks["P"] = [Item("c", parent: "P")]; // teammate-owned child, not in snapshot

        var result = await Service(fake).ResolveForeignSubtasksAsync([Item("P")]);

        Assert.Equal(["c"], Ids(result));
        Assert.False(result.Truncated);
        Assert.Empty(fake.ListCalls);              // few parents -> never uses the whole-list branch
        Assert.Contains("P", fake.SubtaskCalls);
        Assert.Contains("c", fake.SubtaskCalls);   // recursed into the pulled-in child
    }

    [Fact]
    public async Task PerParent_RecursesToGrandchild()
    {
        var fake = new FakeClickUpClient();
        fake.Subtasks["P"] = [Item("c", parent: "P")];
        fake.Subtasks["c"] = [Item("gc", parent: "c")];

        var result = await Service(fake).ResolveForeignSubtasksAsync([Item("P")]);

        Assert.Equal(["c", "gc"], Ids(result));
    }

    [Fact]
    public async Task Hybrid_DenseListWhole_SparseRemainderPerParent_PoolsAndDedups()
    {
        // 9 parents > PerParentThreshold(8): list "A" has 5 (>= WholeListMinParents 4) -> whole-list;
        // b/c/d/e sit alone in their own lists -> per-parent.
        var fake = new FakeClickUpClient();
        TaskItem[] snapshot =
        [
            Item("a0", list: "A"), Item("a1", list: "A"), Item("a2", list: "A"), Item("a3", list: "A"), Item("a4", list: "A"),
            Item("b", list: "B"), Item("c", list: "C"), Item("d", list: "D"), Item("e", list: "E"),
        ];
        // Whole list A contains its own tasks + a foreign child of a0 (same list).
        fake.Lists["A"] = [.. snapshot.Where(t => t.ListId == "A"), Item("fa", parent: "a0", list: "A")];
        // A per-parent parent still gathers its foreign child.
        fake.Subtasks["b"] = [Item("fb", parent: "b", list: "B")];

        var result = await Service(fake).ResolveForeignSubtasksAsync(snapshot);

        Assert.Equal(["fa", "fb"], Ids(result));
        Assert.False(result.Truncated);
        // A routed to whole-list (closed included); its parents NOT fetched per-parent.
        Assert.Contains(("A", true), fake.ListCalls);
        Assert.DoesNotContain(fake.SubtaskCalls, id => id is "a0" or "a1" or "a2" or "a3" or "a4");
        // Sparse remainder fetched per-parent.
        Assert.Contains("b", fake.SubtaskCalls);
        Assert.Contains("e", fake.SubtaskCalls);
    }

    [Fact]
    public async Task WholeListFetchError_IsBestEffort_OthersStillResolve()
    {
        var fake = new FakeClickUpClient();
        TaskItem[] snapshot =
        [
            Item("a0", list: "A"), Item("a1", list: "A"), Item("a2", list: "A"), Item("a3", list: "A"), Item("a4", list: "A"),
            Item("b", list: "B"), Item("c", list: "C"), Item("d", list: "D"), Item("e", list: "E"),
        ];
        fake.ThrowOnList.Add("A"); // the whole-list fetch fails
        fake.Subtasks["b"] = [Item("fb", parent: "b", list: "B")];

        var result = await Service(fake).ResolveForeignSubtasksAsync(snapshot);

        Assert.Equal(["fb"], Ids(result));   // A contributes nothing; b still resolves
        Assert.False(result.Truncated);      // a fetch error is not truncation
    }

    [Fact]
    public async Task PerParentFetchError_IsBestEffort()
    {
        var fake = new FakeClickUpClient();
        fake.ThrowOnSubtask.Add("P");

        var result = await Service(fake).ResolveForeignSubtasksAsync([Item("P")]);

        Assert.Empty(result.Subtasks);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task TotalRoundTripBudget_BoundsRecursion_AndFlagsTruncated()
    {
        // One seed (under the seed cap, so the plan itself is not truncated), but a deep chain. The BFS
        // budget counts every round-trip: with budget 2 it fetches P then c, then refuses the pending gc.
        var fake = new FakeClickUpClient();
        fake.Subtasks["P"] = [Item("c", parent: "P")];
        fake.Subtasks["c"] = [Item("gc", parent: "c")];
        fake.Subtasks["gc"] = [Item("ggc", parent: "gc")];

        var opts = new SubtaskFetchOptions(MaxPerParentFetches: 2);
        var result = await Service(fake).ResolveForeignSubtasksAsync([Item("P")], opts);

        Assert.True(result.Truncated);
        Assert.Equal(["c", "gc"], Ids(result));            // gc was pooled from c's fetch; ggc never reached
        Assert.Equal(["P", "c"], fake.SubtaskCalls);       // exactly two round-trips — gc not fetched
    }

    [Fact]
    public async Task SeedCapTruncation_PropagatesThroughExecutor()
    {
        // 9 sparse parents (each alone in its list, below WholeListMinParents) -> all per-parent; the plan
        // caps the seeds at MaxPerParentFetches(2) and flags truncation, which the resolution surfaces.
        var fake = new FakeClickUpClient();
        TaskItem[] snapshot = [.. Enumerable.Range(0, 9).Select(i => Item($"p{i}", list: $"L{i}"))];

        var opts = new SubtaskFetchOptions(MaxPerParentFetches: 2);
        var result = await Service(fake).ResolveForeignSubtasksAsync(snapshot, opts);

        Assert.True(result.Truncated);
        Assert.Equal(2, fake.SubtaskCalls.Count); // only the two un-dropped seeds were fetched
    }
}
