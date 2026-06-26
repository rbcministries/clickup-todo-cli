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
    public async Task GetAssignedTasks_ReturnsTasksWithIds()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(WorkspaceId),
            "Set CLICKUP_TOKEN and CLICKUP_WORKSPACE_ID to run this test.");
        using var client = new ClickUpClient(Token!);
        var me = await client.GetMeAsync();

        var tasks = await client.GetAssignedTasksAsync(WorkspaceId!, me.Id);

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
    public async Task BadToken_IsReportedAsAuthFailure()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient("pk_0_INVALIDTOKENVALUE");

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.GetMeAsync());

        Assert.True(ex.IsAuthFailure);
    }
}
