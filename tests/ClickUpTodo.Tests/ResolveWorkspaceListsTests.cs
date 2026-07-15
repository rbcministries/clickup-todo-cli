using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The workspace list-hierarchy walk (#236) through the <see cref="IClickUpClient"/> seam: each
/// <see cref="TaskService.ResolveWorkspaceListsAsync"/> call walks at most
/// <see cref="TaskService.MaxSpacesPerWalkStep"/> spaces (folderless + per-folder lists), a pass
/// spreads across calls without re-enumerating spaces, results accumulate deduped by id, a space
/// that fails is skipped best-effort, and a completed pass makes the next call start fresh.
/// </summary>
public sealed class ResolveWorkspaceListsTests
{
    private static TaskService Service(FakeClient fake)
        => new(fake, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "pl" }, userId: 1);

    private static NamedEntity E(string id, string? name = null) => new(id, name ?? id);

    /// <summary>In-memory hierarchy: spaces → (folderless lists, folders → lists), with call
    /// counters and per-space failure injection. Unused paths throw so accidental reliance is loud.</summary>
    private sealed class FakeClient : IClickUpClient
    {
        public List<NamedEntity> Spaces { get; set; } = [];
        public Dictionary<string, List<NamedEntity>> FolderlessLists { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<NamedEntity>> Folders { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<NamedEntity>> FolderLists { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ThrowOnSpace { get; } = new(StringComparer.Ordinal);

        public int SpacesCalls;
        public readonly List<string> WalkedSpaces = [];
        private readonly object _gate = new();

        public Task<IReadOnlyList<NamedEntity>> GetSpacesAsync(string workspaceId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SpacesCalls);
            return Task.FromResult<IReadOnlyList<NamedEntity>>(Spaces.ToList());
        }

        public Task<IReadOnlyList<NamedEntity>> GetFolderlessListsAsync(string spaceId, CancellationToken ct = default)
        {
            lock (_gate)
                WalkedSpaces.Add(spaceId);
            if (ThrowOnSpace.Contains(spaceId))
                throw new InvalidOperationException("boom");
            return Task.FromResult<IReadOnlyList<NamedEntity>>(
                FolderlessLists.TryGetValue(spaceId, out var lists) ? lists : []);
        }

        public Task<IReadOnlyList<NamedEntity>> GetFoldersAsync(string spaceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedEntity>>(
                Folders.TryGetValue(spaceId, out var folders) ? folders : []);

        public Task<IReadOnlyList<NamedEntity>> GetListsInFolderAsync(string folderId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedEntity>>(
                FolderLists.TryGetValue(folderId, out var lists) ? lists : []);

        // Unused by the walk.
        public Task<ClickUpUser> GetMeAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<NamedEntity>> GetWorkspacesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkspaceMember>> GetWorkspaceMembersAsync(string workspaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NamedEntity> GetListAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetListColorAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<StatusOption>> GetListStatusesAsync(string listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, CancellationToken ct = default) => throw new NotImplementedException();
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

    [Fact]
    public async Task SmallWorkspace_CompletesInOneStep_WalkingFolderlessAndFolderedLists()
    {
        var fake = new FakeClient { Spaces = [E("s1"), E("s2")] };
        fake.FolderlessLists["s1"] = [E("l1", "Inbox")];
        fake.Folders["s1"] = [E("f1")];
        fake.FolderLists["f1"] = [E("l2", "Sprint")];
        fake.FolderlessLists["s2"] = [E("l3", "Personal")];

        var result = await Service(fake).ResolveWorkspaceListsAsync();

        Assert.True(result.PassComplete);
        Assert.Equal(["l1", "l2", "l3"], result.Lists.Select(l => l.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(1, fake.SpacesCalls);
    }

    [Fact]
    public async Task LargeWorkspace_SpreadsThePassAcrossSteps_WithoutReEnumeratingSpaces()
    {
        var fake = new FakeClient
        {
            Spaces = Enumerable.Range(1, TaskService.MaxSpacesPerWalkStep * 2 + 1)
                .Select(i => E($"s{i}")).ToList(),
        };
        foreach (var space in fake.Spaces)
            fake.FolderlessLists[space.Id] = [E($"list-of-{space.Id}")];
        var service = Service(fake);

        var step1 = await service.ResolveWorkspaceListsAsync();
        Assert.False(step1.PassComplete);
        Assert.Equal(TaskService.MaxSpacesPerWalkStep, step1.Lists.Count);

        var step2 = await service.ResolveWorkspaceListsAsync();
        Assert.False(step2.PassComplete);
        Assert.Equal(TaskService.MaxSpacesPerWalkStep * 2, step2.Lists.Count);

        var step3 = await service.ResolveWorkspaceListsAsync();
        Assert.True(step3.PassComplete);
        Assert.Equal(fake.Spaces.Count, step3.Lists.Count);

        Assert.Equal(1, fake.SpacesCalls); // one enumeration per pass, however many steps it takes
        Assert.Equal(fake.Spaces.Count, fake.WalkedSpaces.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CompletedPass_MakesTheNextCallStartAFreshOne()
    {
        var fake = new FakeClient { Spaces = [E("s1")] };
        fake.FolderlessLists["s1"] = [E("l1")];
        var service = Service(fake);

        var first = await service.ResolveWorkspaceListsAsync();
        Assert.True(first.PassComplete);

        // A list created between passes is picked up by the re-walk; the known set accumulates.
        fake.FolderlessLists["s1"].Add(E("l-new"));
        var second = await service.ResolveWorkspaceListsAsync();

        Assert.True(second.PassComplete);
        Assert.Equal(2, fake.SpacesCalls);
        Assert.Equal(["l-new", "l1"], second.Lists.Select(l => l.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task FailedSpace_IsSkippedBestEffort_AndTheStepStillSucceeds()
    {
        var fake = new FakeClient { Spaces = [E("s1"), E("s2")] };
        fake.ThrowOnSpace.Add("s1");
        fake.FolderlessLists["s2"] = [E("l2")];

        var result = await Service(fake).ResolveWorkspaceListsAsync();

        Assert.True(result.PassComplete);
        Assert.Equal(["l2"], result.Lists.Select(l => l.Id));
    }

    [Fact]
    public async Task Lists_AreDedupedById_LastNameWins()
    {
        // The same list id can surface twice (e.g. renamed between passes); the map must not grow
        // duplicates and should keep the freshest name.
        var fake = new FakeClient { Spaces = [E("s1")] };
        fake.FolderlessLists["s1"] = [E("l1", "Old name")];
        var service = Service(fake);

        await service.ResolveWorkspaceListsAsync();
        fake.FolderlessLists["s1"] = [E("l1", "New name")];
        var second = await service.ResolveWorkspaceListsAsync();

        var only = Assert.Single(second.Lists);
        Assert.Equal("l1", only.Id);
        Assert.Equal("New name", only.Name);
    }

    [Fact]
    public async Task KnownWorkspaceLists_ExposesTheAccumulatedSet()
    {
        var fake = new FakeClient { Spaces = [E("s1")] };
        fake.FolderlessLists["s1"] = [E("l1")];
        var service = Service(fake);

        Assert.Empty(service.KnownWorkspaceLists);
        await service.ResolveWorkspaceListsAsync();
        Assert.Equal(["l1"], service.KnownWorkspaceLists.Select(l => l.Id));
    }

    [Fact]
    public async Task EmptyWorkspace_CompletesImmediately()
    {
        var result = await Service(new FakeClient()).ResolveWorkspaceListsAsync();

        Assert.True(result.PassComplete);
        Assert.Empty(result.Lists);
    }
}
