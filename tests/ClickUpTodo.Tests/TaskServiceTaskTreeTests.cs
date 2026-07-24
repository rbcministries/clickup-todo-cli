using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Executor tests for <see cref="TaskService.GetTaskTreeAsync"/> (#291) through the
/// <see cref="IClickUpClient"/> seam with an in-memory fake — no generated client, no token. The pure
/// arrangement is covered by <see cref="TaskTreeArrangerTests"/>; these pin how the fetch is executed:
/// the ancestry walk up (capped, cycle-safe, best-effort), the descendant BFS (capped, deduped,
/// best-effort), and that only the initial task fetch propagates its error.
/// </summary>
public sealed class TaskServiceTaskTreeTests
{
    private static TaskItem Item(string id, string? parent = null)
        => new() { Id = id, Name = id, ParentId = parent };

    private static TaskService Service(FakeClient fake)
        => new(fake, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "pl" }, userId: 1);

    private static (string Id, int Depth, bool Current) Row(TaskTreeRow r) => (r.Task.Id, r.Depth, r.IsCurrent);

    [Fact]
    public async Task LoneTask_NoAncestryNoSubtasks_SingleRow()
    {
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");

        var rows = await Service(fake).GetTaskTreeAsync("T");

        Assert.Equal([("T", 0, true)], rows.Select(Row));
        Assert.Equal(["T"], fake.ItemCalls);            // only the task itself fetched as an item
        Assert.Equal(["T"], fake.SubtaskCalls);         // one subtask probe on the task
    }

    [Fact]
    public async Task WalksAncestryUp_TopMostFirst()
    {
        var fake = new FakeClient();
        fake.Items["gp"] = Item("gp");
        fake.Items["p"] = Item("p", parent: "gp");
        fake.Items["T"] = Item("T", parent: "p");

        var rows = await Service(fake).GetTaskTreeAsync("T");

        Assert.Equal([("gp", 0, false), ("p", 1, false), ("T", 2, true)], rows.Select(Row));
        Assert.Equal(["T", "p", "gp"], fake.ItemCalls); // current, then up the chain
    }

    [Fact]
    public async Task GathersDescendants_BreadthFirst_Nested()
    {
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        fake.Subtasks["T"] = [Item("c1", parent: "T"), Item("c2", parent: "T")];
        fake.Subtasks["c1"] = [Item("gc", parent: "c1")];

        var rows = await Service(fake).GetTaskTreeAsync("T");

        Assert.Equal(
            [("T", 0, true), ("c1", 1, false), ("gc", 2, false), ("c2", 1, false)],
            rows.Select(Row));
    }

    [Fact]
    public async Task AncestorFetchError_IsBestEffort_StopsWalk()
    {
        var fake = new FakeClient();
        fake.Items["T"] = Item("T", parent: "p");
        fake.ThrowOnItem.Add("p"); // the parent fetch fails

        var rows = await Service(fake).GetTaskTreeAsync("T");

        Assert.Equal([("T", 0, true)], rows.Select(Row)); // tree still shows the task
    }

    [Fact]
    public async Task SubtaskFetchError_IsBestEffort_TaskStillShown()
    {
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        fake.ThrowOnSubtask.Add("T");

        var rows = await Service(fake).GetTaskTreeAsync("T");

        Assert.Equal([("T", 0, true)], rows.Select(Row));
    }

    [Fact]
    public async Task InitialTaskFetchError_Propagates()
    {
        var fake = new FakeClient();
        fake.ThrowOnItem.Add("T");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fake).GetTaskTreeAsync("T"));
    }

    [Fact]
    public async Task AncestryCycle_TerminatesWithoutLooping()
    {
        var fake = new FakeClient();
        fake.Items["T"] = Item("T", parent: "p");
        fake.Items["p"] = Item("p", parent: "T"); // p points back at T

        var rows = await Service(fake).GetTaskTreeAsync("T");

        Assert.Equal([("p", 0, false), ("T", 1, true)], rows.Select(Row));
        Assert.Equal(["T", "p"], fake.ItemCalls); // T (current), p (its parent); T is not re-fetched
    }

    [Fact]
    public async Task AncestryWalk_CapsAtMaxAncestorFetches()
    {
        var fake = new FakeClient();
        // A long chain: t0 (root) <- t1 <- ... <- t(N) where the current task is the deepest.
        var depth = TaskService.MaxAncestorFetches + 5;
        for (var i = 0; i <= depth; i++)
            fake.Items[$"t{i}"] = Item($"t{i}", parent: i == 0 ? null : $"t{i - 1}");
        var currentId = $"t{depth}";

        var rows = await Service(fake).GetTaskTreeAsync(currentId);

        // current + exactly MaxAncestorFetches ancestors resolved (the walk stops at the cap).
        Assert.Equal(TaskService.MaxAncestorFetches + 1, rows.Count);
        Assert.True(rows[^1].IsCurrent);
        // The current-item fetch plus MaxAncestorFetches parent fetches.
        Assert.Equal(TaskService.MaxAncestorFetches + 1, fake.ItemCalls.Count);
    }

    [Fact]
    public async Task DescendantEchoingAncestor_IsNotReAddedOrRefetched()
    {
        // Defensive: a subtask fetch that echoes an ancestor (or the current task) back must not loop the
        // tree — the descendant de-dup is seeded with the ancestry ids, so it's dropped and never re-BFS'd.
        var fake = new FakeClient();
        fake.Items["anc"] = Item("anc");
        fake.Items["T"] = Item("T", parent: "anc");
        // T's subtasks include a real child AND (pathologically) its own ancestor "anc" and itself "T".
        fake.Subtasks["T"] = [Item("c", parent: "T"), Item("anc"), Item("T", parent: "anc")];

        var rows = await Service(fake).GetTaskTreeAsync("T");

        Assert.Equal([("anc", 0, false), ("T", 1, true), ("c", 2, false)], rows.Select(Row));
        // "anc"/"T" were already seen (ancestry + current), so they were neither re-added nor re-fetched
        // as subtask parents — only the genuine new child "c" was probed for its own subtasks.
        Assert.Equal(["T", "c"], fake.SubtaskCalls);
    }

    [Fact]
    public async Task DescendantBfs_CapsAtMaxSubtaskFetches()
    {
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        // A wide/deep chain where every node has one child, so BFS would recurse indefinitely without a cap.
        var chain = TaskService.MaxTreeSubtaskFetches + 10;
        for (var i = 0; i < chain; i++)
            fake.Subtasks[i == 0 ? "T" : $"n{i - 1}"] = [Item($"n{i}", parent: i == 0 ? "T" : $"n{i - 1}")];

        var rows = await Service(fake).GetTaskTreeAsync("T");

        // Exactly MaxTreeSubtaskFetches subtask round-trips were spent.
        Assert.Equal(TaskService.MaxTreeSubtaskFetches, fake.SubtaskCalls.Count);
        // The tree is non-empty and the current task is present at the root.
        Assert.True(rows[0].IsCurrent);
    }

    /// <summary>In-memory <see cref="IClickUpClient"/> exposing only the two fetch paths the tree walk
    /// uses; every other member throws so accidental reliance is loud.</summary>
    private sealed class FakeClient : IClickUpClient
    {
        public Dictionary<string, TaskItem> Items { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<TaskItem>> Subtasks { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ThrowOnItem { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ThrowOnSubtask { get; } = new(StringComparer.Ordinal);
        public List<string> ItemCalls { get; } = [];
        public List<string> SubtaskCalls { get; } = [];

        public Task<TaskItem> GetTaskItemAsync(string taskId, CancellationToken ct = default)
        {
            ItemCalls.Add(taskId);
            if (ThrowOnItem.Contains(taskId))
                throw new InvalidOperationException($"boom:{taskId}");
            return Task.FromResult(Items.TryGetValue(taskId, out var v)
                ? v
                : throw new InvalidOperationException($"no task {taskId}"));
        }

        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default)
        {
            SubtaskCalls.Add(taskId);
            if (ThrowOnSubtask.Contains(taskId))
                throw new InvalidOperationException($"boom:{taskId}");
            return Task.FromResult(Subtasks.TryGetValue(taskId, out var v) ? v : (IReadOnlyList<TaskItem>)[]);
        }

        // Unused by the tree walk.
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
        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, long? updatedAfterMs = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
