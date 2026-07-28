using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Executor test for <see cref="TaskService.GetListCustomFieldsAsync"/> (#395): the New Task screen's
/// field-definition fetch is a thin passthrough to the facade, verified through the
/// <see cref="IClickUpClient"/> seam with an in-memory fake (no generated client, no token).
/// </summary>
public sealed class TaskServiceCustomFieldsTests
{
    [Fact]
    public async Task GetListCustomFields_PassesListIdThrough_ReturnsFacadeResult()
    {
        var fake = new FakeClient
        {
            Fields = [new CustomFieldDefinition("f1", "Notes", "text", false)],
        };
        var service = new TaskService(fake, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "pl" }, userId: 1);

        var fields = await service.GetListCustomFieldsAsync("list-42");

        Assert.Equal("list-42", fake.RequestedListId);
        Assert.Single(fields);
        Assert.Equal("Notes", fields[0].Name);
    }

    private sealed class FakeClient : IClickUpClient
    {
        public IReadOnlyList<CustomFieldDefinition> Fields { get; init; } = [];
        public string? RequestedListId { get; private set; }

        public Task<IReadOnlyList<CustomFieldDefinition>> GetListCustomFieldsAsync(string listId, CancellationToken ct = default)
        {
            RequestedListId = listId;
            return Task.FromResult(Fields);
        }

        // Unused by this test.
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
        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
