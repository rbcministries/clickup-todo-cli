namespace ClickUpTodo.Configuration;

/// <summary>
/// Non-secret, user-facing settings persisted to <c>config.json</c>. The API token is stored
/// separately and encrypted (see <see cref="TokenStore"/>).
/// </summary>
public sealed class AppConfig
{
    /// <summary>Selected ClickUp workspace (team) id.</summary>
    public string WorkspaceId { get; set; } = "";

    public string WorkspaceName { get; set; } = "";

    /// <summary>The list the user treats as their "Personal Tasks" list.</summary>
    public string PersonalTasksListId { get; set; } = "";

    public string PersonalTasksListName { get; set; } = "";

    /// <summary>How often the task list polls ClickUp, in seconds.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>Task ids pinned to the "Current Focus" pane, so focus survives restarts.</summary>
    public List<string> PinnedTaskIds { get; set; } = [];

    /// <summary>True once the setup wizard has completed at least once.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(WorkspaceId) && !string.IsNullOrWhiteSpace(PersonalTasksListId);
}
