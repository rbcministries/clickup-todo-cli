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

    /// <summary>A #419 ancestry seed backed by the given items — the lookup form the TUI passes into
    /// <see cref="TaskService.GetTaskTreeAsync"/> so a parent already in hand skips its round-trip.</summary>
    private static Func<string, TaskItem?> Snapshot(params TaskItem[] items)
    {
        var map = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        return id => map.TryGetValue(id, out var v) ? v : null;
    }

    /// <summary>A #450 descendant children index backed by the given per-parent entries — the lookup the TUI
    /// passes into <see cref="TaskService.GetTaskTreeAsync"/> so a parent whose complete child set is already
    /// in hand skips its <c>GetSubtasksAsync</c> round-trip. An absent parent returns <c>null</c> (a miss →
    /// fetch); a present entry (including an empty one) is a vouched-for complete set.</summary>
    private static Func<string, IReadOnlyList<TaskItem>?> Index(params (string Parent, TaskItem[] Children)[] entries)
    {
        var map = entries.ToDictionary(
            e => e.Parent, e => (IReadOnlyList<TaskItem>)e.Children, StringComparer.Ordinal);
        return id => map.TryGetValue(id, out var v) ? v : null;
    }

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

    [Fact]
    public async Task DescendantBfs_WideLevel_FetchesFirstBudgetParentsInFifoOrder()
    {
        // A single level wider than the fetch budget: every child of T is a descendant, but only the first
        // MaxTreeSubtaskFetches-1 of them (after T's own probe) get their subtasks fetched — and in FIFO
        // order — so batching honours the budget and the breadth-first fetch order exactly. (#417)
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        var width = TaskService.MaxTreeSubtaskFetches + 5;
        var children = new List<TaskItem>();
        for (var i = 0; i < width; i++)
            children.Add(Item($"c{i:D2}", parent: "T"));
        fake.Subtasks["T"] = children;

        var rows = await Service(fake).GetTaskTreeAsync("T");

        // Budget spent exactly: T's own probe + the first (budget-1) children, in FIFO order.
        var expected = new List<string> { "T" };
        for (var i = 0; i < TaskService.MaxTreeSubtaskFetches - 1; i++)
            expected.Add($"c{i:D2}");
        Assert.Equal(expected, fake.SubtaskCalls);
        // Every child is still present as a descendant row (a child is added when T is probed, whether or
        // not its own subtasks were later fetched), so the cap never drops a discovered node.
        Assert.Equal(width + 1, rows.Count); // T + all its children
        Assert.True(rows[0].IsCurrent);
    }

    [Fact]
    public async Task DescendantBfs_FetchesAFrontierConcurrently()
    {
        // T's three children form one frontier; their subtask probes must be in flight at once, not serial.
        var fake = new GatedClient(gatedIds: ["c1", "c2", "c3"]);
        fake.Items["T"] = Item("T");
        fake.Subtasks["T"] = [Item("c1", parent: "T"), Item("c2", parent: "T"), Item("c3", parent: "T")];

        var treeTask = Service(fake).GetTaskTreeAsync("T");
        await fake.WaitForInFlightAsync(3);   // all three probes reached the gate together
        var peak = fake.MaxInFlight;
        fake.Release();
        var rows = await treeTask;

        Assert.Equal(3, peak);                // the whole frontier was fetched concurrently
        Assert.Equal([("T", 0, true), ("c1", 1, false), ("c2", 1, false), ("c3", 1, false)], rows.Select(Row));
    }

    [Fact]
    public async Task DescendantBfs_NeverExceedsConcurrencyBound()
    {
        // A frontier wider than the bound must fetch at most MaxTreeSubtaskConcurrency at a time.
        var width = TaskService.MaxTreeSubtaskConcurrency + 4;
        var childIds = Enumerable.Range(0, width).Select(i => $"c{i:D2}").ToArray();
        var fake = new GatedClient(gatedIds: childIds);
        fake.Items["T"] = Item("T");
        fake.Subtasks["T"] = childIds.Select(id => Item(id, parent: "T")).ToList();

        var treeTask = Service(fake).GetTaskTreeAsync("T");
        await fake.WaitForInFlightAsync(TaskService.MaxTreeSubtaskConcurrency); // first batch fills the bound
        var peak = fake.MaxInFlight;
        fake.Release();
        var rows = await treeTask;

        Assert.Equal(TaskService.MaxTreeSubtaskConcurrency, peak); // filled the bound, never beyond it
        Assert.Equal(width + 1, rows.Count);                       // T + all children still discovered
    }

    // --- #419 idea #2: seeding the ancestry walk from the in-memory snapshot ---

    [Fact]
    public async Task SeededAncestor_SkipsItsFetch()
    {
        // "p" is only in the snapshot, never in fake.Items — so a round-trip for it would throw
        // "no task p". The walk completing (and placing "p") proves the fetch was skipped, seeded instead.
        var fake = new FakeClient();
        fake.Items["T"] = Item("T", parent: "p");

        var rows = await Service(fake).GetTaskTreeAsync("T", Snapshot(Item("p")));

        Assert.Equal([("p", 0, false), ("T", 1, true)], rows.Select(Row));
        Assert.Equal(["T"], fake.ItemCalls); // only the current task fetched; "p" came from the snapshot
    }

    [Fact]
    public async Task PartialSeed_FetchesOnlyTheMissingLevels()
    {
        // Chain gp <- p <- T. The snapshot holds "p" (pointing at "gp") but not "gp"; "gp" is API-only.
        var fake = new FakeClient();
        fake.Items["gp"] = Item("gp");
        fake.Items["T"] = Item("T", parent: "p");

        var rows = await Service(fake).GetTaskTreeAsync("T", Snapshot(Item("p", parent: "gp")));

        Assert.Equal([("gp", 0, false), ("p", 1, false), ("T", 2, true)], rows.Select(Row));
        Assert.Equal(["T", "gp"], fake.ItemCalls); // T (current) + gp (missing); "p" seeded, not fetched
    }

    [Fact]
    public async Task NullReturningLookup_ReproducesUnseededFetches()
    {
        // A seed that misses on everything must leave the fetch path byte-for-byte identical to no seed.
        var fake = new FakeClient();
        fake.Items["gp"] = Item("gp");
        fake.Items["p"] = Item("p", parent: "gp");
        fake.Items["T"] = Item("T", parent: "p");

        var rows = await Service(fake).GetTaskTreeAsync("T", _ => null);

        Assert.Equal([("gp", 0, false), ("p", 1, false), ("T", 2, true)], rows.Select(Row));
        Assert.Equal(["T", "p", "gp"], fake.ItemCalls); // current, then up the chain — all fetched
    }

    [Fact]
    public async Task Cap_CountsSeededAncestors_BoundsDepth()
    {
        // A fully-seeded chain longer than the cap: none of the ancestors are fetched, but the walk still
        // stops at MaxAncestorFetches — so the cap bounds ancestry *depth*, not just round-trips.
        var fake = new FakeClient();
        var depth = TaskService.MaxAncestorFetches + 5;
        var currentId = $"t{depth}";
        fake.Items[currentId] = Item(currentId, parent: $"t{depth - 1}"); // current task is API-fetched
        var snapshot = new List<TaskItem>();
        for (var i = 0; i < depth; i++)
            snapshot.Add(Item($"t{i}", parent: i == 0 ? null : $"t{i - 1}"));

        var rows = await Service(fake).GetTaskTreeAsync(currentId, Snapshot(snapshot.ToArray()));

        Assert.Equal(TaskService.MaxAncestorFetches + 1, rows.Count); // current + exactly cap ancestors
        Assert.True(rows[^1].IsCurrent);
        Assert.Equal([currentId], fake.ItemCalls); // only the current task hit the API; all ancestors seeded
    }

    [Fact]
    public async Task SeedPresent_InitialTaskIsStillFetched()
    {
        // Even when the snapshot also holds the current task, the initial fetch is deliberately NOT seeded
        // (#419): it stays a round-trip so its data is fresh and its error can propagate (next test).
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");

        await Service(fake).GetTaskTreeAsync("T", Snapshot(Item("T")));

        Assert.Equal(["T"], fake.ItemCalls); // the current task was fetched despite being in the snapshot
    }

    [Fact]
    public async Task SeedPresent_InitialTaskError_StillPropagates()
    {
        // The seed must not swallow the one error the tree walk is allowed to surface.
        var fake = new FakeClient();
        fake.ThrowOnItem.Add("T");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(fake).GetTaskTreeAsync("T", Snapshot(Item("T"))));
    }

    [Fact]
    public async Task SeededAncestryCycle_TerminatesWithoutLooping()
    {
        // A cycle reached entirely through the snapshot: the seeded parent "p" points back at "T". The
        // seen-set guard runs in the while-condition before resolution, so the walk terminates on the
        // seeded path exactly as it does when the cycle is fetched (cf. AncestryCycle_Terminates…).
        var fake = new FakeClient();
        fake.Items["T"] = Item("T", parent: "p");

        var rows = await Service(fake).GetTaskTreeAsync("T", Snapshot(Item("p", parent: "T")));

        Assert.Equal([("p", 0, false), ("T", 1, true)], rows.Select(Row));
        Assert.Equal(["T"], fake.ItemCalls); // current fetched; "p" seeded; "T" is not re-walked
    }

    // --- #450: seeding the descendant BFS from a known-complete children index ---

    [Fact]
    public async Task IndexedParent_SkipsItsSubtaskFetch()
    {
        // T's children come from the index, so T is never probed; its children (misses) still fetch normally.
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");

        var rows = await Service(fake).GetTaskTreeAsync(
            "T", childrenIndex: Index(("T", [Item("c1", "T"), Item("c2", "T")])));

        Assert.Equal([("T", 0, true), ("c1", 1, false), ("c2", 1, false)], rows.Select(Row));
        Assert.Equal(["c1", "c2"], fake.SubtaskCalls); // T seeded from the index (no probe); c1/c2 fetched
    }

    [Fact]
    public async Task IndexedParent_ChildrenAreStillBfsd_PastTheSeededLevel()
    {
        // A seeded level doesn't end the walk: an index hit's children are themselves resolved (fetched or
        // indexed), and FIFO order is preserved across the hit/miss mix (c1 seeded before c2 fetched).
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        fake.Subtasks["T"] = [Item("c1", "T"), Item("c2", "T")];

        var rows = await Service(fake).GetTaskTreeAsync(
            "T", childrenIndex: Index(("c1", [Item("gc", "c1")])));

        Assert.Equal(
            [("T", 0, true), ("c1", 1, false), ("gc", 2, false), ("c2", 1, false)],
            rows.Select(Row));
        // T fetched (miss), c1 seeded (skipped), c2 fetched (miss), then gc fetched (miss). c1 never probed.
        Assert.Equal(["T", "c2", "gc"], fake.SubtaskCalls);
    }

    [Fact]
    public async Task IndexMiss_FallsBackToFetch()
    {
        // A non-null index that doesn't contain a parent returns null for it → the BFS fetches exactly as if
        // no index were supplied.
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        fake.Subtasks["T"] = [Item("c", "T")];

        var rows = await Service(fake).GetTaskTreeAsync("T", childrenIndex: Index(("unrelated", [])));

        Assert.Equal([("T", 0, true), ("c", 1, false)], rows.Select(Row));
        Assert.Equal(["T", "c"], fake.SubtaskCalls); // neither T nor c is in the index → both fetched
    }

    [Fact]
    public async Task FullyIndexedTree_ResolvesWithZeroFetches_EvenPastTheBudget()
    {
        // A chain deeper than MaxTreeSubtaskFetches, entirely in the index: an index hit spends no fetch
        // budget ("only fetches the rest"), so the whole tree resolves with zero GetSubtasksAsync calls.
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        var chain = TaskService.MaxTreeSubtaskFetches + 10;
        var entries = new List<(string, TaskItem[])> { ("T", [Item("n0", "T")]) };
        for (var i = 0; i < chain; i++)
            entries.Add(($"n{i}", i < chain - 1 ? [Item($"n{i + 1}", $"n{i}")] : []));

        var rows = await Service(fake).GetTaskTreeAsync("T", childrenIndex: Index(entries.ToArray()));

        Assert.Empty(fake.SubtaskCalls);            // nothing fetched — every level came from the index
        Assert.Equal(chain + 1, rows.Count);        // T + n0..n(chain-1), none truncated by the fetch budget
        Assert.True(rows[0].IsCurrent);
    }

    [Fact]
    public async Task IndexedEmptySet_IsTrusted_SkipsFetch_AndAddsNoChildren()
    {
        // A present-but-empty entry is a parent VOUCHED to have no children: it skips the fetch and adds
        // nothing — distinct from a miss (null), which would fetch and discover the fake's real subtask.
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        fake.Subtasks["T"] = [Item("c", "T")]; // present in the fake, but the index says T has no children

        var rows = await Service(fake).GetTaskTreeAsync("T", childrenIndex: Index(("T", [])));

        Assert.Equal([("T", 0, true)], rows.Select(Row)); // the vouched-for empty set is trusted
        Assert.Empty(fake.SubtaskCalls);                   // T not probed, so its fake child "c" never surfaces
    }

    [Fact]
    public async Task IndexedDescendantEchoingAncestor_IsNotReAddedOrRefetched()
    {
        // The de-dup (seeded with the ancestry ids) applies to indexed children too: an indexed set echoing
        // an ancestor / the current task can't loop the tree, and the deduped ids are never probed.
        var fake = new FakeClient();
        fake.Items["anc"] = Item("anc");
        fake.Items["T"] = Item("T", parent: "anc");

        var rows = await Service(fake).GetTaskTreeAsync(
            "T", childrenIndex: Index(("T", [Item("c", "T"), Item("anc"), Item("T", "anc")]), ("c", [])));

        Assert.Equal([("anc", 0, false), ("T", 1, true), ("c", 2, false)], rows.Select(Row));
        Assert.Equal(["T", "anc"], fake.ItemCalls); // ancestry unchanged
        Assert.Empty(fake.SubtaskCalls);            // all children came from the index; anc/T deduped, not probed
    }

    [Fact]
    public async Task AllMissIndex_ReproducesTheUnindexedBudgetCap()
    {
        // A non-null index that misses on everything must leave the budget-bounded fetch path byte-for-byte:
        // a chain longer than the budget still stops at exactly MaxTreeSubtaskFetches fetches.
        var fake = new FakeClient();
        fake.Items["T"] = Item("T");
        var chain = TaskService.MaxTreeSubtaskFetches + 10;
        for (var i = 0; i < chain; i++)
            fake.Subtasks[i == 0 ? "T" : $"n{i - 1}"] = [Item($"n{i}", parent: i == 0 ? "T" : $"n{i - 1}")];

        var rows = await Service(fake).GetTaskTreeAsync("T", childrenIndex: Index()); // empty index → all miss

        Assert.Equal(TaskService.MaxTreeSubtaskFetches, fake.SubtaskCalls.Count);
        Assert.True(rows[0].IsCurrent);
    }

    /// <summary>In-memory <see cref="IClickUpClient"/> exposing only the two fetch paths the tree walk
    /// uses; every other member throws so accidental reliance is loud.</summary>
    private class FakeClient : IClickUpClient
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

        public virtual Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default)
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

    /// <summary>A <see cref="FakeClient"/> whose subtask fetches for <c>gatedIds</c> block on a release gate,
    /// tracking how many are in flight at once — so a test can prove the descendant BFS fetches a frontier
    /// concurrently and never past the bound (#417) deterministically, with no timing/<c>Task.Delay</c>
    /// dependence in the assertion. Ungated ids (e.g. the root's own probe) return immediately.</summary>
    private sealed class GatedClient(IReadOnlyCollection<string> gatedIds) : FakeClient
    {
        private readonly HashSet<string> _gated = new(gatedIds, StringComparer.Ordinal);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private int _inFlight;

        private int _maxInFlight;

        /// <summary>The peak number of gated probes seen simultaneously in flight, read under the lock so
        /// the assertion sees the fold from the fetch threads without relying on a happens-before.</summary>
        public int MaxInFlight
        {
            get { lock (_lock) return _maxInFlight; }
        }

        public void Release()
        {
            lock (_lock)
                _release.TrySetResult();
        }

        /// <summary>Completes once at least <paramref name="target"/> gated probes are simultaneously in
        /// flight. Bounded so a regression (serial fetching) falls through rather than hanging forever — the
        /// test's <see cref="MaxInFlight"/> assertion then reports the shortfall.</summary>
        public async Task WaitForInFlightAsync(int target)
        {
            for (var i = 0; i < 500; i++)
            {
                lock (_lock)
                {
                    if (_inFlight >= target)
                        return;
                }
                await Task.Delay(10);
            }
        }

        public override async Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default)
        {
            lock (_lock)
                SubtaskCalls.Add(taskId);

            if (!_gated.Contains(taskId))
                return Subtasks.TryGetValue(taskId, out var immediate) ? immediate : (IReadOnlyList<TaskItem>)[];

            lock (_lock)
            {
                _inFlight++;
                if (_inFlight > _maxInFlight)
                    _maxInFlight = _inFlight;
            }
            try
            {
                await _release.Task.WaitAsync(ct);
            }
            finally
            {
                lock (_lock)
                    _inFlight--;
            }
            return Subtasks.TryGetValue(taskId, out var v) ? v : (IReadOnlyList<TaskItem>)[];
        }
    }
}
