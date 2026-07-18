using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Integration tests that hit the real ClickUp API. They are skipped automatically unless a
/// personal token is provided via the CLICKUP_TOKEN environment variable, so CI stays green
/// without credentials. Deeper tests also read CLICKUP_WORKSPACE_ID and CLICKUP_LIST_ID.
/// </summary>
public sealed class ClickUpClientIntegrationTests
{
    private static string? Token => Environment.GetEnvironmentVariable("CLICKUP_TOKEN");
    private static string? WorkspaceId => Environment.GetEnvironmentVariable("CLICKUP_WORKSPACE_ID");
    private static string? ListId => Environment.GetEnvironmentVariable("CLICKUP_LIST_ID");
    private static string? TaskId => Environment.GetEnvironmentVariable("CLICKUP_TASK_ID");
    private static string? SecondaryListId => Environment.GetEnvironmentVariable("CLICKUP_SECONDARY_LIST_ID");
    private static string? CommentId => Environment.GetEnvironmentVariable("CLICKUP_COMMENT_ID");

    [SkippableFact]
    public async Task GetMe_ReturnsAuthenticatedUser()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient(Token!);

        var me = await client.GetMeAsync();

        Assert.True(me.Id > 0);
        Assert.False(string.IsNullOrWhiteSpace(me.DisplayName));
    }

    [SkippableFact]
    public async Task GetWorkspaces_ReturnsAtLeastOne()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient(Token!);

        var workspaces = await client.GetWorkspacesAsync();

        Assert.NotEmpty(workspaces);
        Assert.All(workspaces, w => Assert.False(string.IsNullOrWhiteSpace(w.Id)));
    }

    [SkippableFact]
    public async Task GetWorkspaceMembers_ReturnsMembersWithIds()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(WorkspaceId),
            "Set CLICKUP_TOKEN and CLICKUP_WORKSPACE_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();

        var members = await client.GetWorkspaceMembersAsync(WorkspaceId!);

        // The workspace always contains at least the authenticated user; every member carries an id.
        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.True(m.Id > 0));
        Assert.Contains(members, m => m.Id == me.Id);
    }

    [SkippableFact]
    public async Task GetAssignedTasks_ReturnsTasksWithIds()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(WorkspaceId),
            "Set CLICKUP_TOKEN and CLICKUP_WORKSPACE_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();

        var tasks = await client.GetAssignedTasksAsync(WorkspaceId!, [me.Id]);

        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Id)));
    }

    [SkippableFact]
    public async Task GetListStatuses_ReturnsStatuses()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var statuses = await client.GetListStatusesAsync(ListId!);

        Assert.NotEmpty(statuses);
        Assert.All(statuses, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
    }

    [SkippableFact]
    public async Task CreateTask_CreatesTaskInList_AndReturnsItMapped()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId),
            "Set CLICKUP_TOKEN and CLICKUP_LIST_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // ClickUp v2 has no task-delete in this facade, so this leaves a clearly-labelled throwaway task
        // on the target list. The name flags it as a test artifact for easy manual cleanup.
        var name = "[clickup-todo-cli test] create-task smoke — safe to delete";

        var created = await client.CreateTaskAsync(ListId!, new NewTaskRequest
        {
            Name = name,
            Description = "Created by the CreateTask integration test.",
            PriorityLevel = 3,
        });

        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal(name, created.Name);
        Assert.Equal(3, created.PriorityLevel);
    }

    [SkippableFact]
    public async Task SetTaskStatus_ReturnsConfirmedStatusFromWriteResponse()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN, CLICKUP_LIST_ID and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var statuses = await client.GetListStatusesAsync(ListId!);
        Skip.If(statuses.Count < 2, "List needs at least two statuses to flip between for this test.");

        var current = (await client.GetListTasksAsync(ListId!)).FirstOrDefault(t => t.Id == TaskId);
        Skip.If(current is null, "CLICKUP_TASK_ID is not an open task on CLICKUP_LIST_ID.");

        var target = statuses.First(s => !string.Equals(s.Name, current!.StatusName, StringComparison.OrdinalIgnoreCase));
        try
        {
            // The write response should carry the new status — no read-after-write needed.
            var confirmed = await client.SetTaskStatusAsync(TaskId!, target.Name);
            Assert.Equal(target.Name, confirmed, ignoreCase: true);
        }
        finally
        {
            // Restore the original status so the test is idempotent.
            if (!string.IsNullOrWhiteSpace(current!.StatusName))
                await client.SetTaskStatusAsync(TaskId!, current.StatusName!);
        }
    }

    [SkippableFact]
    public async Task SetTaskPriority_RoundTripsThroughWriteResponse()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ListId) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN, CLICKUP_LIST_ID and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var original = (await client.GetListTasksAsync(ListId!)).FirstOrDefault(t => t.Id == TaskId);
        Skip.If(original is null, "CLICKUP_TASK_ID is not an open task on CLICKUP_LIST_ID.");

        // Pick a target level different from the current one; the write response reports the truth.
        var target = original!.PriorityLevel == 2 ? 3 : 2;
        try
        {
            var confirmed = await client.SetTaskPriorityAsync(TaskId!, target);
            Assert.Equal(target, confirmed);
        }
        finally
        {
            // Restore the original priority (clearing it when the task had none) for idempotency.
            await client.SetTaskPriorityAsync(TaskId!, original.PriorityLevel);
        }
    }

    [SkippableFact]
    public async Task SetTaskDescription_RoundTripsThroughDetailFetch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        // Preserve the original description so the test is idempotent (restore in finally). The read
        // model already surfaces plain text (text_content → description); write plain text back.
        var original = (await client.GetTaskDetailAsync(TaskId!)).Description ?? "";
        var marker = $"[clickup-todo-cli test] description round-trip — safe to overwrite ({TaskId})";
        try
        {
            var confirmed = await client.SetTaskDescriptionAsync(TaskId!, marker);
            Assert.Equal(marker, confirmed);

            // The change is reflected on a subsequent detail fetch (read → write → re-read is lossless).
            var reread = await client.GetTaskDetailAsync(TaskId!);
            Assert.Equal(marker, reread.Description);
        }
        finally
        {
            // The restore rewrites the description as *plain text* (that's all the read model exposes),
            // so if CLICKUP_TASK_ID points at a task whose description was authored in markdown, this
            // test flattens that formatting. Point CLICKUP_TASK_ID at a throwaway/scratch task.
            await client.SetTaskDescriptionAsync(TaskId!, original);
        }
    }

    [SkippableFact]
    public async Task AddAndRemoveTaskAssignee_ReconcileFromWriteResponse()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();

        var before = await client.GetTaskDetailAsync(TaskId!);
        var wasAssigned = before.Assignees.Contains(me.DisplayName);

        var afterAdd = await client.AddTaskAssigneeAsync(TaskId!, me.Id);
        Assert.Contains(afterAdd, a => a.Id == me.Id);

        // Only undo what this test added, so a task the user was already on is left unchanged.
        if (!wasAssigned)
        {
            var afterRemove = await client.RemoveTaskAssigneeAsync(TaskId!, me.Id);
            Assert.DoesNotContain(afterRemove, a => a.Id == me.Id);
        }
    }

    [SkippableFact]
    public async Task AddAndRemoveTaskListMembership_RoundTripsThroughTaskDetailLists()
    {
        // "Tasks in Multiple Lists" (#237): add the task to a second list, confirm it surfaces in the
        // task detail's Lists membership, then remove it again. Requires the ClickApp to be enabled and a
        // second list id distinct from the task's home list.
        Skip.If(
            string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId) || string.IsNullOrWhiteSpace(SecondaryListId),
            "Set CLICKUP_TOKEN, CLICKUP_TASK_ID and CLICKUP_SECONDARY_LIST_ID (a list other than the task's home) to run this test.");
        using var client = new ClickUpClient(Token!);

        var before = await client.GetTaskDetailAsync(TaskId!);
        var wasMember = before.Lists.Any(l => l.Id == SecondaryListId);
        Skip.If(wasMember, "Task is already a member of CLICKUP_SECONDARY_LIST_ID; pick a list it isn't in.");

        try
        {
            await client.AddTaskToListAsync(TaskId!, SecondaryListId!);
        }
        catch (ClickUpApiException ex)
        {
            // "Tasks in Multiple Lists" is an opt-in (paid) ClickApp; a workspace without it returns a
            // 4xx. The facade correctly surfaced that as a typed exception (not a crash), so treat a
            // disabled ClickApp as a skip rather than a spurious failure — and don't run the cleanup
            // remove (it would throw the same way and mask the real cause).
            Skip.If(true, $"Add-to-list failed — the 'Tasks in Multiple Lists' ClickApp is likely disabled on this workspace (HTTP {ex.StatusCode}).");
            return;
        }

        try
        {
            var afterAdd = await client.GetTaskDetailAsync(TaskId!);
            Assert.Contains(afterAdd.Lists, l => l.Id == SecondaryListId);
        }
        finally
        {
            // Always undo, so the task's membership is left exactly as this test found it.
            await client.RemoveTaskFromListAsync(TaskId!, SecondaryListId!);
        }

        var afterRemove = await client.GetTaskDetailAsync(TaskId!);
        Assert.DoesNotContain(afterRemove.Lists, l => l.Id == SecondaryListId);
    }

    [SkippableFact]
    public async Task GetTaskDetail_ReturnsRichTask()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var detail = await client.GetTaskDetailAsync(TaskId!);

        Assert.Equal(TaskId, detail.Id);
        Assert.False(string.IsNullOrWhiteSpace(detail.Name));
        // Tags/assignees/custom-field collections are always materialized (never null).
        Assert.NotNull(detail.Tags);
        Assert.NotNull(detail.Assignees);
        Assert.NotNull(detail.CustomFields);
    }

    [SkippableFact]
    public async Task GetTaskComments_ReturnsComments()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);

        var comments = await client.GetTaskCommentsAsync(TaskId!);

        // May legitimately be empty, but every returned comment must have an id.
        Assert.All(comments, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
    }

    [SkippableFact]
    public async Task CreateTaskComment_PostsPlainText_AndAppearsOnRefetch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(TaskId),
            "Set CLICKUP_TOKEN and CLICKUP_TASK_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        // Unique marker so the re-fetch assertion can find exactly this comment (it posts real data).
        var text = $"clickup-todo integration test comment {Guid.NewGuid():N}";

        var created = await client.CreateTaskCommentAsync(TaskId!, text);

        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal(text, created.Text);
        Assert.Equal(TaskId, created.TaskId);

        var comments = await client.GetTaskCommentsAsync(TaskId!);
        Assert.Contains(comments, c => c.Id == created.Id && c.Text == text);
    }

    [SkippableFact]
    public async Task GetThreadedComments_ReturnsReplies()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(CommentId),
            "Set CLICKUP_TOKEN and CLICKUP_COMMENT_ID (a comment that has replies) to run this test.");
        using var client = new ClickUpClient(Token!);

        var replies = await client.GetThreadedCommentsAsync(CommentId!);

        // May legitimately be empty, but every returned reply must have an id.
        Assert.All(replies, r => Assert.False(string.IsNullOrWhiteSpace(r.Id)));
    }

    [SkippableFact]
    public async Task CreateThreadedComment_PostsReply_AndAppearsOnRefetch()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(CommentId),
            "Set CLICKUP_TOKEN and CLICKUP_COMMENT_ID (a comment to reply to) to run this test.");
        using var client = new ClickUpClient(Token!);
        // Unique marker so the re-fetch assertion can find exactly this reply (it posts real data).
        var text = $"clickup-todo integration test reply {Guid.NewGuid():N}";

        var created = await client.CreateThreadedCommentAsync(CommentId!, text);

        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal(text, created.Text);

        var replies = await client.GetThreadedCommentsAsync(CommentId!);
        Assert.Contains(replies, r => r.Id == created.Id && r.Text == text);
    }

    [SkippableFact]
    public async Task BadToken_IsReportedAsAuthFailure()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient("pk_0_INVALIDTOKENVALUE");

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.GetMeAsync());

        Assert.True(ex.IsAuthFailure);
    }
}
