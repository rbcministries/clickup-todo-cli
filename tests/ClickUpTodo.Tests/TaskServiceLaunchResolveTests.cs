using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.ResolveLaunchTaskAsync"/> — the #464 cache-first resolution
/// behind <c>--task</c>, sharing the Ctrl+O parser and resolution order. Asserts on the calls each path
/// makes (a test double counting them), never on timing.
/// </summary>
public sealed class TaskServiceLaunchResolveTests
{
    private static TaskDetail Detail(string id) => new() { Id = id, Name = $"task {id}" };

    private static TaskItem Item(string id, string? customId = null) =>
        new() { Id = id, Name = $"task {id}", CustomId = customId };

    private static ClickUpApiException NotFound() => new(404, "GetTask", new InvalidOperationException("gone"));

    /// <summary>A fake facade recording the two lookups the resolver can make; the rest throw. Mirrors the
    /// fake in <see cref="TaskServiceQuickOpenFallbackTests"/>.</summary>
    private sealed class FakeClient : IClickUpClient
    {
        private readonly Func<string, Task<TaskDetail>> _byId;
        private readonly Func<string, string, Task<TaskDetail>> _byCustomId;

        public FakeClient(
            Func<string, Task<TaskDetail>> byId,
            Func<string, string, Task<TaskDetail>>? byCustomId = null)
        {
            _byId = byId;
            _byCustomId = byCustomId ?? ((_, _) => throw new InvalidOperationException("custom-id lookup must not run"));
        }

        public int PlainCalls { get; private set; }
        public int CustomCalls { get; private set; }
        public string? LastPlainId { get; private set; }
        public string? LastCustomId { get; private set; }
        public string? LastTeamId { get; private set; }

        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        {
            PlainCalls++;
            LastPlainId = taskId;
            return _byId(taskId);
        }

        public Task<TaskDetail> GetTaskDetailByCustomIdAsync(string customId, string teamId, CancellationToken ct = default)
        {
            CustomCalls++;
            LastCustomId = customId;
            LastTeamId = teamId;
            return _byCustomId(customId, teamId);
        }

        // ── Unused by the resolver ────────────────────────────────────────────
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
        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static TaskService Service(FakeClient client) => new(client, new AppConfig(), userId: 0);

    // ── Snapshot hits ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotHit_PlainId_OneGet_NoCustomLookup()
    {
        var client = new FakeClient(byId: id => Task.FromResult(Detail(id)));
        var snapshot = new[] { Item("86plain") };

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Task("86plain"), snapshot, "ws1");

        Assert.Equal("86plain", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
        Assert.Equal("86plain", client.LastPlainId);
    }

    [Fact]
    public async Task SnapshotHit_CustomId_ResolvesViaPlainId_NoRetry()
    {
        // The snapshot maps ABC-123 → its plain id, so the resolver already knows the correct endpoint:
        // exactly one GET /task, and never the custom-id lookup (the win the cache buys).
        var client = new FakeClient(byId: id => Task.FromResult(Detail(id)));
        var snapshot = new[] { Item("86plain", customId: "ABC-123") };

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Custom("ABC-123"), snapshot, "ws1");

        Assert.Equal("86plain", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
    }

    [Fact]
    public async Task SnapshotHit_HyphenlessCustomId_ResolvesInOneRequest()
    {
        // A hyphenless custom id parses as a plain-id reference, but FindInCache still matches it on
        // CustomId — so a cached one resolves with a single correct GET, closing the hyphenless hole.
        var client = new FakeClient(byId: id => Task.FromResult(Detail(id)));
        var snapshot = new[] { Item("86plain", customId: "PROJ123") };

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Task("PROJ123"), snapshot, "ws1");

        Assert.Equal("86plain", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
        Assert.Equal("86plain", client.LastPlainId);
    }

    [Fact]
    public async Task StaleSnapshotHit_PlainId404_FallsBackToLiveCustomLookup()
    {
        // The cached mapping is stale (task deleted / custom id reassigned): the plain GET 404s, so the
        // resolver falls back to a live custom-id lookup of the ORIGINAL reference instead of failing.
        var client = new FakeClient(
            byId: _ => throw NotFound(),
            byCustomId: (_, _) => Task.FromResult(Detail("realid")));
        var snapshot = new[] { Item("86stale", customId: "ABC-123") };

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Custom("ABC-123"), snapshot, "ws1");

        Assert.Equal("realid", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal("86stale", client.LastPlainId);
        Assert.Equal(1, client.CustomCalls);
        Assert.Equal("ABC-123", client.LastCustomId);
        Assert.Equal("ws1", client.LastTeamId);
    }

    [Fact]
    public async Task StaleSnapshotHit_PlainIdMatchedById_404_SurfacesWithoutDoubleFetch()
    {
        // A plain-id ref matched by Id: the cached and live ids are identical, so a 404 means the task is
        // genuinely gone. Re-fetching the same id (and a custom-id lookup of a known-plain id) would be
        // wasteful — the 404 surfaces after exactly one request, no fall-through.
        var client = new FakeClient(byId: _ => throw NotFound());
        var snapshot = new[] { Item("86plain") };

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(
            () => Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Task("86plain"), snapshot, "ws1"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
    }

    [Fact]
    public async Task StaleSnapshotHit_HyphenlessCustomId_404_FallsBackToLive()
    {
        // A hyphenless custom id parses as a plain-id ref but matches the snapshot by CustomId, so the
        // cached plain id differs from the ref. A stale cached plain id (404) is worth a live retry: the
        // fallback re-tries the ORIGINAL token, 404s as a plain id, then resolves it as a custom id.
        var client = new FakeClient(
            byId: id => id == "realid" ? Task.FromResult(Detail("realid")) : throw NotFound(),
            byCustomId: (_, _) => Task.FromResult(Detail("realid")));
        var snapshot = new[] { Item("86stale", customId: "PROJ123") };

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Task("PROJ123"), snapshot, "ws1");

        Assert.Equal("realid", detail.Id);
        Assert.Equal("PROJ123", client.LastCustomId);
        Assert.Equal(1, client.CustomCalls);
    }

    // ── Live resolution (snapshot miss) ─────────────────────────────────────────

    [Fact]
    public async Task Uncached_PlainId_OneGet()
    {
        var client = new FakeClient(byId: id => Task.FromResult(Detail(id)));

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Task("86abc123"), [], "ws1");

        Assert.Equal("86abc123", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
    }

    [Fact]
    public async Task Uncached_HyphenatedCustomId_DirectLookup_OneRequest()
    {
        // A hyphenated custom id resolves straight through the custom-id endpoint — no plain-id 404 first.
        var client = new FakeClient(
            byId: _ => throw new InvalidOperationException("plain lookup must not run"),
            byCustomId: (_, _) => Task.FromResult(Detail("realid")));

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Custom("ABC-123"), [], "ws9");

        Assert.Equal("realid", detail.Id);
        Assert.Equal(0, client.PlainCalls);
        Assert.Equal(1, client.CustomCalls);
        Assert.Equal("ABC-123", client.LastCustomId);
        Assert.Equal("ws9", client.LastTeamId);
    }

    [Fact]
    public async Task Uncached_HyphenlessCustomId_404ThenCustomLookup()
    {
        // Uncached and hyphenless ⇒ parsed as a plain id ⇒ the plain GET 404s, then the fallback retries
        // it as a custom id.
        var client = new FakeClient(
            byId: _ => throw NotFound(),
            byCustomId: (_, _) => Task.FromResult(Detail("realid")));

        var detail = await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Task("PROJ123"), [], "ws1");

        Assert.Equal("realid", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(1, client.CustomCalls);
        Assert.Equal("PROJ123", client.LastCustomId);
        Assert.Equal("ws1", client.LastTeamId);
    }

    [Fact]
    public async Task Live_CustomLookup_UsesTheProvidedTeamId()
    {
        // The team id the caller passes (a URL-carried id in preference to the configured one, computed by
        // Program) is threaded through to the custom-id lookup verbatim.
        var client = new FakeClient(
            byId: _ => throw new InvalidOperationException("plain lookup must not run"),
            byCustomId: (_, _) => Task.FromResult(Detail("realid")));

        await Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Custom("ABC-123", "urlteam"), [], "urlteam");

        Assert.Equal("urlteam", client.LastTeamId);
    }

    [Fact]
    public async Task InvalidReference_Throws()
    {
        var client = new FakeClient(byId: _ => throw new InvalidOperationException("must not be called"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(client).ResolveLaunchTaskAsync(QuickOpenRef.Invalid, [], "ws1"));

        Assert.Equal(0, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
    }
}
