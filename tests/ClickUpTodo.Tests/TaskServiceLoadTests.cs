using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.LoadAsync"/>'s fetch wiring — specifically that the F12
/// completed view (#178/#191) threads <c>ViewSettings.IncludesClosedTasks</c> into the
/// <c>includeClosed</c> flag on both the assigned-tasks and personal-list fetches, so closed-type
/// tasks are only requested in the All state. done-type tasks arrive regardless of the flag, so the
/// Active and WithDone states both fetch open-only.
/// </summary>
public sealed class TaskServiceLoadTests
{
    private sealed class FakeClient : IClickUpClient
    {
        public bool? AssignedIncludeClosed { get; private set; }
        public bool? PersonalIncludeClosed { get; private set; }

        public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, CancellationToken ct = default)
        {
            AssignedIncludeClosed = includeClosed;
            return Task.FromResult(new List<TaskItem>());
        }

        public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default)
        {
            PersonalIncludeClosed = includeClosed;
            return Task.FromResult(new List<TaskItem>());
        }

        // Unused by LoadAsync on the default (Assignee IS me / empty) view.
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
        public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static AppConfig Config(CompletedView completed) => new()
    {
        WorkspaceId = "ws",
        PersonalTasksListId = "list",
        View = new ViewSettings { Completed = completed },
    };

    [Theory]
    [InlineData(CompletedView.Active)]
    [InlineData(CompletedView.WithDone)] // done-type arrives regardless, so no wider fetch than Active
    public async Task LoadAsync_NotAll_FetchesOpenOnly(CompletedView completed)
    {
        var fake = new FakeClient();
        var service = new TaskService(fake, Config(completed), userId: 1);

        await service.LoadAsync();

        Assert.False(fake.AssignedIncludeClosed);
        Assert.False(fake.PersonalIncludeClosed);
    }

    [Fact]
    public async Task LoadAsync_All_IncludesClosedOnBothFetches()
    {
        var fake = new FakeClient();
        var service = new TaskService(fake, Config(CompletedView.All), userId: 1);

        await service.LoadAsync();

        Assert.True(fake.AssignedIncludeClosed);
        Assert.True(fake.PersonalIncludeClosed);
    }
}
