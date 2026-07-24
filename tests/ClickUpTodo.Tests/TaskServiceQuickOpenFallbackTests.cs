using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.GetTaskDetailWithCustomIdFallbackAsync"/> — the #353 (item 3)
/// hyphenless-custom-id fallback: a bare token parses as a plain id, so an uncached hyphenless custom
/// id would take the plain <c>GET /task/{id}</c> path and 404. The service retries a 404 as a custom-id
/// lookup when a team id is available, and only then.
/// </summary>
public sealed class TaskServiceQuickOpenFallbackTests
{
    private static TaskDetail Detail(string id) => new() { Id = id, Name = $"task {id}" };

    /// <summary>A fake facade that records the calls the fallback makes and returns/throws per the case
    /// under test. Only the two methods the fallback touches are meaningful; the rest throw.</summary>
    private sealed class FakeClient : IClickUpClient
    {
        private readonly Func<string, Task<TaskDetail>> _byId;
        private readonly Func<string, string, Task<TaskDetail>> _byCustomId;

        public FakeClient(Func<string, Task<TaskDetail>> byId, Func<string, string, Task<TaskDetail>> byCustomId)
        {
            _byId = byId;
            _byCustomId = byCustomId;
        }

        public int PlainCalls { get; private set; }
        public int CustomCalls { get; private set; }
        public string? LastCustomId { get; private set; }
        public string? LastTeamId { get; private set; }

        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        {
            PlainCalls++;
            return _byId(taskId);
        }

        public Task<TaskDetail> GetTaskDetailByCustomIdAsync(string customId, string teamId, CancellationToken ct = default)
        {
            CustomCalls++;
            LastCustomId = customId;
            LastTeamId = teamId;
            return _byCustomId(customId, teamId);
        }

        // ── Unused by the fallback ────────────────────────────────────────────
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

    private static ClickUpApiException NotFound() => new(404, "GetTask", new InvalidOperationException("boom"));

    [Fact]
    public async Task PlainIdFound_ReturnsIt_WithoutFallback()
    {
        var client = new FakeClient(
            byId: id => Task.FromResult(Detail(id)),
            byCustomId: (_, _) => throw new InvalidOperationException("fallback must not run"));
        var svc = new TaskService(client, new AppConfig(), userId: 0);

        var detail = await svc.GetTaskDetailWithCustomIdFallbackAsync("86abc123", "ws1");

        Assert.Equal("86abc123", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
    }

    [Fact]
    public async Task PlainId404_WithTeamId_RetriesAsCustomId()
    {
        // A hyphenless custom id (e.g. PROJ123) 404s as a plain id, then resolves via the custom-id
        // lookup — which returns the task's REAL plain id.
        var client = new FakeClient(
            byId: _ => throw NotFound(),
            byCustomId: (_, _) => Task.FromResult(Detail("realid")));
        var svc = new TaskService(client, new AppConfig(), userId: 0);

        var detail = await svc.GetTaskDetailWithCustomIdFallbackAsync("PROJ123", "ws1");

        Assert.Equal("realid", detail.Id);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(1, client.CustomCalls);
        Assert.Equal("PROJ123", client.LastCustomId);
        Assert.Equal("ws1", client.LastTeamId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PlainId404_WithoutTeamId_PropagatesTheError(string? teamId)
    {
        // No team id to resolve a custom id against ⇒ nothing to fall back to; the 404 surfaces.
        var client = new FakeClient(
            byId: _ => throw NotFound(),
            byCustomId: (_, _) => throw new InvalidOperationException("fallback must not run"));
        var svc = new TaskService(client, new AppConfig(), userId: 0);

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(
            () => svc.GetTaskDetailWithCustomIdFallbackAsync("PROJ123", teamId));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(1, client.PlainCalls);
        Assert.Equal(0, client.CustomCalls);
    }

    [Fact]
    public async Task PlainIdNon404_NeverFallsBack()
    {
        // A 401/403/500 is not a "wrong id shape" signal — it must surface, not be masked by a retry.
        var client = new FakeClient(
            byId: _ => throw new ClickUpApiException(500, "GetTask", new InvalidOperationException("server")),
            byCustomId: (_, _) => throw new InvalidOperationException("fallback must not run"));
        var svc = new TaskService(client, new AppConfig(), userId: 0);

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(
            () => svc.GetTaskDetailWithCustomIdFallbackAsync("86abc123", "ws1"));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal(0, client.CustomCalls);
    }
}
