using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.GetTaskCommentsWithRepliesAsync"/> (#328): it fetches the flat
/// comments then loads reply threads only for the comments that report one, and returns them enriched —
/// exercised against an in-memory <see cref="IClickUpClient"/> fake (no generated client, no token).
/// </summary>
public sealed class TaskServiceThreadedCommentsTests
{
    private sealed class FakeClient : IClickUpClient
    {
        public required IReadOnlyList<CommentItem> Comments { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<CommentItem>> Threads { get; init; }
        public List<string> ThreadFetches { get; } = [];

        public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult(Comments);

        public Task<IReadOnlyList<CommentItem>> GetThreadedCommentsAsync(string commentId, CancellationToken ct = default)
        {
            ThreadFetches.Add(commentId);
            return Task.FromResult(Threads.TryGetValue(commentId, out var t) ? t : []);
        }

        // Everything else is unused by GetTaskCommentsWithRepliesAsync.
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
        public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static CommentItem Comment(string id, int replyCount = 0, string? taskId = null)
        => new(id, Author: "author", DateMs: 100, Text: $"comment {id}", Resolved: false, TaskId: taskId,
            ReplyCount: replyCount);

    private static CommentItem Reply(string id, long dateMs)
        => new(id, Author: "replier", DateMs: dateMs, Text: $"reply {id}", Resolved: false);

    private static TaskService Service(IClickUpClient client)
        => new(client, new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "list" }, userId: 1);

    [Fact]
    public async Task GetTaskCommentsWithReplies_enriches_only_comments_that_report_a_thread()
    {
        var client = new FakeClient
        {
            Comments = [Comment("a", replyCount: 2, taskId: "t1"), Comment("b", replyCount: 0, taskId: "t1")],
            Threads = new Dictionary<string, IReadOnlyList<CommentItem>>
            {
                ["a"] = [Reply("a2", 200), Reply("a1", 100)],
            },
        };

        var result = await Service(client).GetTaskCommentsWithRepliesAsync("t1");

        // Only "a" reported replies, so only its thread was fetched.
        Assert.Equal(["a"], client.ThreadFetches);
        var a = result.Single(c => c.Id == "a");
        Assert.Equal(["a1", "a2"], a.Replies.Select(r => r.Id));          // oldest-first
        Assert.All(a.Replies, r => Assert.Equal("a", r.ParentCommentId)); // stamped to parent
        Assert.All(a.Replies, r => Assert.Equal("t1", r.TaskId));         // stamped to parent's task
        Assert.Empty(result.Single(c => c.Id == "b").Replies);
    }

    [Fact]
    public async Task GetTaskCommentsWithReplies_returns_flat_comments_unchanged_when_none_have_threads()
    {
        var client = new FakeClient
        {
            Comments = [Comment("a", taskId: "t1"), Comment("b", taskId: "t1")],
            Threads = new Dictionary<string, IReadOnlyList<CommentItem>>(),
        };

        var result = await Service(client).GetTaskCommentsWithRepliesAsync("t1");

        Assert.Empty(client.ThreadFetches);
        Assert.Equal(["a", "b"], result.Select(c => c.Id));
        Assert.All(result, c => Assert.Empty(c.Replies));
    }
}
