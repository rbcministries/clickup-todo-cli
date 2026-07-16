namespace ClickUpTodo.ClickUp;

/// <summary>
/// The stable domain-facing surface of the ClickUp API facade. <see cref="ClickUpClient"/> is the real
/// implementation (paging, auth, mapping over the Kiota-generated client); this seam lets services that
/// depend only on the facade — chiefly <see cref="Services.TaskService"/> — be unit-tested against a fake
/// without the generated client or a live token. Signatures mirror <see cref="ClickUpClient"/> exactly,
/// including default arguments, so callers are identical whether they hold the interface or the class.
/// </summary>
public interface IClickUpClient
{
    Task<ClickUpUser> GetMeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NamedEntity>> GetWorkspacesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> GetWorkspaceMembersAsync(string workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<NamedEntity>> GetSpacesAsync(string workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<NamedEntity>> GetFoldersAsync(string spaceId, CancellationToken ct = default);
    Task<IReadOnlyList<NamedEntity>> GetFolderlessListsAsync(string spaceId, CancellationToken ct = default);
    Task<IReadOnlyList<NamedEntity>> GetListsInFolderAsync(string folderId, CancellationToken ct = default);
    Task<NamedEntity> GetListAsync(string listId, CancellationToken ct = default);
    Task<string?> GetListColorAsync(string listId, CancellationToken ct = default);
    Task<IReadOnlyList<StatusOption>> GetListStatusesAsync(string listId, CancellationToken ct = default);
    Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, long? updatedAfterMs = null, CancellationToken ct = default);
    Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default);

    /// <summary>Delta fetches for the incremental refresh (#194): tasks updated after the epoch-ms
    /// watermark, closed included so closures surface. Default implementations throw so a fake that a
    /// delta test forgot to extend fails loudly instead of silently behaving like a full fetch;
    /// <see cref="ClickUpClient"/> overrides both.</summary>
    Task<List<TaskItem>> GetAssignedTasksDeltaAsync(string workspaceId, IReadOnlyList<long> assigneeIds, long updatedAfterMs, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement the delta fetch.");

    /// <inheritdoc cref="GetAssignedTasksDeltaAsync"/>
    Task<List<TaskItem>> GetListTasksDeltaAsync(string listId, long updatedAfterMs, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement the delta fetch.");
    /// <summary>Creates a task in a list from the given fields and returns it mapped to the domain
    /// <see cref="TaskItem"/> (#209). See <see cref="ClickUpClient.CreateTaskAsync"/>. A default
    /// throwing implementation (mirroring the delta fetches above) spares read-only fakes that never
    /// create tasks from implementing it; <see cref="ClickUpClient"/> overrides it.</summary>
    Task<TaskItem> CreateTaskAsync(string listId, NewTaskRequest task, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement task creation.");
    Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default);
    Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default);

    /// <summary>Set a task's (plain-text) description. Pass <c>""</c> to clear it; <c>null</c> is
    /// rejected. Returns the server-confirmed description from the write response. Default throwing
    /// implementation so read-only fakes needn't implement a write path they never call (mirrors the
    /// delta fetches); <see cref="ClickUpClient"/> overrides it.</summary>
    Task<string?> SetTaskDescriptionAsync(string taskId, string description, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement the description write.");
    Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default);
    Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default);
    Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default);

    /// <summary>Post a plain-text comment to a task (#210) and return it as a <see cref="CommentItem"/>
    /// for optimistic append. Rich content (@-mentions, task links) is out of scope. See
    /// <see cref="ClickUpClient.CreateTaskCommentAsync"/> for the minimal-response mapping contract.</summary>
    Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default);
}
