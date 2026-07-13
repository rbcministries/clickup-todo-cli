using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.LoadAsync"/>'s fetch wiring — specifically that the F12
/// "Show Completed" toggle (#178) threads <c>ViewSettings.ShowCompleted</c> into the
/// <c>includeClosed</c> flag on both the assigned-tasks and personal-list fetches, so completed
/// (closed-type) tasks are only requested when the toggle is on.
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
    }

    private static AppConfig Config(bool showCompleted) => new()
    {
        WorkspaceId = "ws",
        PersonalTasksListId = "list",
        View = new ViewSettings { ShowCompleted = showCompleted },
    };

    [Fact]
    public async Task LoadAsync_ShowCompletedOff_FetchesOpenOnly()
    {
        var fake = new FakeClient();
        var service = new TaskService(fake, Config(showCompleted: false), userId: 1);

        await service.LoadAsync();

        Assert.False(fake.AssignedIncludeClosed);
        Assert.False(fake.PersonalIncludeClosed);
    }

    [Fact]
    public async Task LoadAsync_ShowCompletedOn_IncludesClosedOnBothFetches()
    {
        var fake = new FakeClient();
        var service = new TaskService(fake, Config(showCompleted: true), userId: 1);

        await service.LoadAsync();

        Assert.True(fake.AssignedIncludeClosed);
        Assert.True(fake.PersonalIncludeClosed);
    }
}
