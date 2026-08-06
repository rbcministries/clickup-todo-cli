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

        // Call logs are lock-guarded: the per-parent BFS now fetches a level's parents concurrently
        // (#144), so same-level calls can record simultaneously. Reads happen after the resolve completes.
        private readonly object _sync = new();
        public List<string> SubtaskCalls { get; } = [];
        public List<(string ListId, bool IncludeClosed)> ListCalls { get; } = [];

        /// <summary>Optional per-call hook, awaited before the result is returned, so a test can observe
        /// or gate concurrency (e.g. a rendezvous proving genuine same-level overlap).</summary>
        public Func<string, Task>? OnSubtask { get; set; }

        public async Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default)
        {
            lock (_sync)
                SubtaskCalls.Add(taskId);
            if (OnSubtask is { } hook)
                await hook(taskId);
            if (ThrowOnSubtask.Contains(taskId))
                throw new InvalidOperationException("boom");
            return Subtasks.TryGetValue(taskId, out var v) ? v : (IReadOnlyList<TaskItem>)[];
        }

        public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default)
        {
            lock (_sync)
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
        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, long? updatedAfterMs = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
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

    // --- #450: CompleteChildren (the Task Tree descendant-BFS seed) ---

    [Fact]
    public async Task CompleteChildren_RecordsEachPerParentBranchFetch()
    {
        // A successful per-parent GetSubtasksAsync returns a parent's COMPLETE child set, so each fetched
        // parent — the seed P and the recursed child c — records its set (c's is a trusted empty).
        var fake = new FakeClickUpClient();
        fake.Subtasks["P"] = [Item("c", parent: "P")];

        var result = await Service(fake).ResolveForeignSubtasksAsync([Item("P")]);

        Assert.Equal(["c"], result.CompleteChildren["P"].Select(t => t.Id)); // P's complete direct children
        Assert.True(result.CompleteChildren.ContainsKey("c"));               // c was recursed into (fetched)
        Assert.Empty(result.CompleteChildren["c"]);                          // …and vouched to have none
    }

    [Fact]
    public async Task CompleteChildren_ExcludesWholeListBranchParents()
    {
        // The whole-list branch pulls a LIST, not a parent's children, and a parent's children can span
        // lists — so it can't vouch per-parent. Whole-list-routed parents must be absent from the index;
        // only the sparse per-parent remainder is recorded.
        var fake = new FakeClickUpClient();
        TaskItem[] snapshot =
        [
            Item("a0", list: "A"), Item("a1", list: "A"), Item("a2", list: "A"), Item("a3", list: "A"), Item("a4", list: "A"),
            Item("b", list: "B"), Item("c", list: "C"), Item("d", list: "D"), Item("e", list: "E"),
        ];
        fake.Lists["A"] = [.. snapshot.Where(t => t.ListId == "A"), Item("fa", parent: "a0", list: "A")];
        fake.Subtasks["b"] = [Item("fb", parent: "b", list: "B")];

        var result = await Service(fake).ResolveForeignSubtasksAsync(snapshot);

        Assert.False(result.CompleteChildren.ContainsKey("a0"));             // whole-list branch → not recorded
        Assert.DoesNotContain("fa", result.CompleteChildren.Keys);          // a whole-list child isn't recursed
        Assert.Equal(["fb"], result.CompleteChildren["b"].Select(t => t.Id)); // per-parent → recorded
    }

    [Fact]
    public async Task CompleteChildren_ExcludesFailedPerParentFetch()
    {
        // A throwing per-parent fetch returns null, so its parent is NEVER recorded as a (falsely-empty)
        // complete set — the seam must not let an incomplete fetch masquerade as "this parent has no children".
        var fake = new FakeClickUpClient();
        fake.ThrowOnSubtask.Add("P");

        var result = await Service(fake).ResolveForeignSubtasksAsync([Item("P")]);

        Assert.False(result.CompleteChildren.ContainsKey("P"));
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

    [Fact]
    public async Task PerParentBranch_FetchesOverlap_AndRespectCap()
    {
        // 12 sparse parents (each alone in its list, below WholeListMinParents) -> all per-parent, one BFS
        // level. Every fetch counts itself in flight; the first MaxFanOutConcurrency arrivals rendezvous
        // (proving genuine same-level overlap — the pre-#144 serial BFS never gets a second call in flight
        // and times out), and the peak in-flight must never exceed the cap (the SemaphoreSlim gate bounds
        // it, so no flakiness). Each parent has a foreign child, so the concurrent pooling is exercised too.
        var fake = new FakeClickUpClient();
        var parentIds = Enumerable.Range(0, 12).Select(i => $"p{i}").ToList();
        foreach (var id in parentIds)
            fake.Subtasks[id] = [Item($"c-{id}", parent: id, list: $"L-{id}")];
        var snapshot = parentIds.Select((id, i) => Item(id, list: $"L{i}")).ToArray();

        // The rendezvous completes once MaxFanOutConcurrency calls are in flight together; the first
        // parent cohort trips it, and every later call (remaining parents + the recursed children, a
        // second gated level) then finds it already satisfied and proceeds without stalling.
        var rendezvous = new Rendezvous(TaskService.MaxFanOutConcurrency);
        var inFlight = 0;
        var peak = 0;
        fake.OnSubtask = async _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await rendezvous.ArriveAsync();
            Interlocked.Decrement(ref inFlight);
        };

        var result = await Service(fake).ResolveForeignSubtasksAsync(snapshot);

        Assert.Equal(12, result.Subtasks.Count);                                    // every foreign child pooled
        Assert.Equal(24, fake.SubtaskCalls.Count);                                  // 12 parents + 12 recursed children
        Assert.True(peak <= TaskService.MaxFanOutConcurrency, $"peak in-flight {peak} exceeded the cap");
        Assert.True(peak >= 2, "per-parent fetches never overlapped"); // rendezvous makes >= cap certain; >= 2 is the safe floor
    }

    /// <summary>A rendezvous of <paramref name="parties"/> arrivals: each caller signals and waits for the
    /// rest. Only reachable if the callers are genuinely concurrent — serial callers time out.</summary>
    private sealed class Rendezvous(int parties)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= parties)
                _allArrived.TrySetResult();
            return _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Lock-free max: raises <paramref name="target"/> to <paramref name="value"/> if higher.</summary>
    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target))
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
