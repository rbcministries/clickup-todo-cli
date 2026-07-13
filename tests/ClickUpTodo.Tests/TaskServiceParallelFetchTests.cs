using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Concurrency-shape tests for the refresh pipeline's parallelized stages (#192), driven through the
/// <see cref="IClickUpClient"/> seam with an in-memory fake — no generated client, no token. These
/// assert the *shape* of the fetching (overlap where stages are independent, a hard cap per fan-out)
/// plus the merge/best-effort semantics that must survive the rewrite, deterministically: overlap is
/// proven by a rendezvous (both calls must be in flight to proceed — serial code times out), and the
/// cap by counting in-flight calls (Parallel.ForEachAsync guarantees the bound, so no flakiness).
/// </summary>
public sealed class TaskServiceParallelFetchTests
{
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(5);

    private static TaskItem Item(string id, string? list = "L", long? due = null)
        => new() { Id = id, Name = id, ListId = list, DueDateMs = due };

    private static TaskService Service(FakeClient fake)
        => new(fake, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "pl" }, userId: 1);

    /// <summary>In-memory <see cref="IClickUpClient"/> with per-method hooks so a test can observe or
    /// gate individual calls; unused paths throw so accidental reliance is loud.</summary>
    private sealed class FakeClient : IClickUpClient
    {
        public List<TaskItem> Assigned { get; set; } = [];
        public List<TaskItem> Personal { get; set; } = [];
        public Dictionary<string, TaskDetail> Details { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ThrowOnDetail { get; } = new(StringComparer.Ordinal);

        public Func<Task>? OnAssigned { get; set; }
        public Func<Task>? OnPersonal { get; set; }
        public Func<Task>? OnDetail { get; set; }
        public Func<Task>? OnListColor { get; set; }

        public async Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, CancellationToken ct = default)
        {
            await (OnAssigned?.Invoke() ?? Task.CompletedTask);
            return Assigned;
        }

        public async Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default)
        {
            await (OnPersonal?.Invoke() ?? Task.CompletedTask);
            return Personal;
        }

        public async Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        {
            await (OnDetail?.Invoke() ?? Task.CompletedTask);
            if (ThrowOnDetail.Contains(taskId))
                throw new InvalidOperationException("boom");
            return Details.TryGetValue(taskId, out var d) ? d : new TaskDetail { Id = taskId, Name = taskId };
        }

        public async Task<string?> GetListColorAsync(string listId, CancellationToken ct = default)
        {
            await (OnListColor?.Invoke() ?? Task.CompletedTask);
            return "#123456";
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
        public Task<IReadOnlyList<StatusOption>> GetListStatusesAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>A rendezvous of <paramref name="parties"/> arrivals: each caller signals and waits for
    /// the rest. Only reachable if the callers are genuinely concurrent — serial callers time out.</summary>
    private sealed class Rendezvous(int parties)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= parties)
                _allArrived.TrySetResult();
            return _allArrived.Task.WaitAsync(RendezvousTimeout);
        }
    }

    // ── LoadAsync: assigned ∪ personal fetched concurrently ─────────────────

    [Fact]
    public async Task LoadAsync_AssignedAndPersonalFetches_Overlap()
    {
        var fake = new FakeClient
        {
            Assigned = [Item("a")],
            Personal = [Item("p")],
        };
        // Neither fetch completes until BOTH are in flight; the pre-#192 serial code (personal only
        // started after assigned finished) can never satisfy this and fails via the timeout.
        var rendezvous = new Rendezvous(2);
        fake.OnAssigned = rendezvous.ArriveAsync;
        fake.OnPersonal = rendezvous.ArriveAsync;

        var result = await Service(fake).LoadAsync();

        Assert.Equal(["a", "p"], result.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_StillMergesDedupesAndOrders()
    {
        var fake = new FakeClient
        {
            Assigned = [Item("dup", due: 2), Item("b", due: 1)],
            Personal = [Item("dup", due: 2), Item("c", due: 3)],
        };

        var result = await Service(fake).LoadAsync();

        // De-duped by id; ordered by due date.
        Assert.Equal(["b", "dup", "c"], result.Select(t => t.Id));
    }

    [Fact]
    public async Task LoadAsync_PersonalFetchFailure_StillFails()
    {
        var fake = new FakeClient
        {
            OnPersonal = () => throw new InvalidOperationException("boom"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fake).LoadAsync());
    }

    // ── ResolveContextParentsAsync: bounded fan-out ─────────────────────────

    private static TaskItem Subtask(string id, string parent)
        => new() { Id = id, Name = id, ParentId = parent };

    [Fact]
    public async Task ResolveContextParents_FetchesOverlap_AndRespectCap()
    {
        // 12 subtasks referencing 12 missing parents. Every detail fetch counts itself in flight;
        // the first MaxFanOutConcurrency arrivals rendezvous (proving genuine overlap) and the peak
        // must never exceed the cap (Parallel.ForEachAsync guarantees the bound — deterministic).
        var snapshot = Enumerable.Range(0, 12).Select(i => Subtask($"s{i}", $"P{i}")).ToList();
        var fake = new FakeClient();
        var rendezvous = new Rendezvous(TaskService.MaxFanOutConcurrency);
        var inFlight = 0;
        var peak = 0;
        fake.OnDetail = async () =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await rendezvous.ArriveAsync();
            Interlocked.Decrement(ref inFlight);
        };

        var parents = await Service(fake).ResolveContextParentsAsync(snapshot);

        Assert.Equal(12, parents.Count);
        Assert.True(peak <= TaskService.MaxFanOutConcurrency, $"peak in-flight {peak} exceeded the cap");
        Assert.True(peak >= 2, "detail fetches never overlapped"); // rendezvous makes >= cap certain; >= 2 is the safe floor
    }

    [Fact]
    public async Task ResolveContextParents_BestEffort_SkipsFailedParent()
    {
        var snapshot = new[] { Subtask("s1", "P1"), Subtask("s2", "P2"), Subtask("s3", "P3") };
        var fake = new FakeClient();
        fake.ThrowOnDetail.Add("P2");

        var parents = await Service(fake).ResolveContextParentsAsync(snapshot);

        Assert.Equal(["P1", "P3"], parents.Keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ── ResolveListColorsAsync: bounded fan-out ─────────────────────────────

    [Fact]
    public async Task ResolveListColors_RespectsCap()
    {
        // The rendezvous holds the first MaxFanOutConcurrency calls in flight together, so an
        // unbounded implementation (the pre-#192 WhenAll) drives the peak to 12 and fails; a
        // Task.Yield alone would let calls drain too fast to ever observe a violation.
        var listIds = Enumerable.Range(0, 12).Select(i => $"L{i}").ToList();
        var fake = new FakeClient();
        var rendezvous = new Rendezvous(TaskService.MaxFanOutConcurrency);
        var inFlight = 0;
        var peak = 0;
        fake.OnListColor = async () =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await rendezvous.ArriveAsync();
            Interlocked.Decrement(ref inFlight);
        };

        var colors = await Service(fake).ResolveListColorsAsync(listIds);

        Assert.Equal(12, colors.Count);
        Assert.All(colors.Values, c => Assert.Equal("#123456", c));
        Assert.True(peak <= TaskService.MaxFanOutConcurrency, $"peak in-flight {peak} exceeded the cap");
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
