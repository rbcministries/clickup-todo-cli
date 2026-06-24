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
    public async Task BadToken_IsReportedAsAuthFailure()
    {
        Skip.If(string.IsNullOrWhiteSpace(Token), "Set CLICKUP_TOKEN to run ClickUp integration tests.");
        using var client = new ClickUpClient("pk_0_INVALIDTOKENVALUE");

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.GetMeAsync());

        Assert.True(ex.IsAuthFailure);
    }
}
