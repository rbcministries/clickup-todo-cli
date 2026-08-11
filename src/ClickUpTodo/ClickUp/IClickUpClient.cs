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

    /// <summary>The list's Custom Field definitions — id, name, type, required flag, and drop-down/label
    /// options (#249). See <see cref="ClickUpClient.GetListCustomFieldsAsync"/>. A default throwing
    /// implementation (mirroring the writes/deltas below) spares fakes that don't exercise custom fields
    /// from implementing it; <see cref="ClickUpClient"/> overrides it.</summary>
    Task<IReadOnlyList<CustomFieldDefinition>> GetListCustomFieldsAsync(string listId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement custom-field fetch.");

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

    /// <summary>Permanently delete a task. See <see cref="ClickUpClient.DeleteTaskAsync"/>. The app has no
    /// delete UI — this backs the create-task integration test's cleanup — so, like the writes above, it
    /// has a default throwing implementation and read-only fakes needn't implement it.</summary>
    Task DeleteTaskAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement task deletion.");
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

    /// <summary>Add a task to an <b>additional</b> list — ClickUp's "Tasks in Multiple Lists" feature
    /// (#237). The home list is unchanged; the extra membership surfaces in
    /// <see cref="TaskDetail.Lists"/>. Requires the "Tasks in Multiple Lists" ClickApp; when disabled the
    /// call fails with a caught <see cref="ClickUpApiException"/> (HTTP 4xx, ClickUp <c>OV_016</c>). Default
    /// throwing implementation so read-only fakes needn't implement a write they never call (mirrors the
    /// other writes); <see cref="ClickUpClient"/> overrides it. See <see cref="ClickUpClient.AddTaskToListAsync"/>.</summary>
    Task AddTaskToListAsync(string taskId, string listId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement task↔list membership writes.");

    /// <summary>Remove a task from an additional list (#237) — the inverse of
    /// <see cref="AddTaskToListAsync"/>. The home list is unaffected. Default throwing implementation as
    /// above; <see cref="ClickUpClient"/> overrides it.</summary>
    Task RemoveTaskFromListAsync(string taskId, string listId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement task↔list membership writes.");

    /// <summary>Toggle (or set) a checklist item's <c>resolved</c> state (D, #457) and return the
    /// server-confirmed parent <see cref="TaskChecklist"/>. <paramref name="taskId"/> is the owning task,
    /// used to record a multi-tab change-marker nudge (#519). Default throwing implementation so read-only
    /// fakes needn't implement a write they never call (mirroring the other writes);
    /// <see cref="ClickUpClient"/> overrides it. See <see cref="ClickUpClient.SetChecklistItemResolvedAsync"/>.</summary>
    Task<TaskChecklist> SetChecklistItemResolvedAsync(string taskId, string checklistId, string itemId, bool resolved, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-item writes.");

    /// <summary>Create a checklist item (E, #458) and return the server-confirmed parent
    /// <see cref="TaskChecklist"/> (the new item included). Default throwing so read-only fakes needn't
    /// implement it; <see cref="ClickUpClient"/> overrides it. See <see cref="ClickUpClient.CreateChecklistItemAsync"/>.</summary>
    Task<TaskChecklist> CreateChecklistItemAsync(string checklistId, string name, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-item writes.");

    /// <summary>Rename a checklist item (E, #458) and return the server-confirmed parent
    /// <see cref="TaskChecklist"/>. Default throwing; <see cref="ClickUpClient"/> overrides it.
    /// See <see cref="ClickUpClient.RenameChecklistItemAsync"/>.</summary>
    Task<TaskChecklist> RenameChecklistItemAsync(string checklistId, string itemId, string name, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-item writes.");

    /// <summary>Delete a checklist item (E, #458). ClickUp returns an empty body, so this is a void write.
    /// Default throwing; <see cref="ClickUpClient"/> overrides it. See <see cref="ClickUpClient.DeleteChecklistItemAsync"/>.</summary>
    Task DeleteChecklistItemAsync(string checklistId, string itemId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-item writes.");

    /// <summary>Reorder / reparent a checklist item (G, #569) and return the server-confirmed parent
    /// <see cref="TaskChecklist"/>. <paramref name="taskId"/> is the owning task, used to record a multi-tab
    /// change-marker nudge (#519). Default throwing so read-only fakes needn't implement it;
    /// <see cref="ClickUpClient"/> overrides it. See <see cref="ClickUpClient.MoveChecklistItemAsync"/>.</summary>
    Task<TaskChecklist> MoveChecklistItemAsync(string taskId, string checklistId, string itemId, string? parentId, double orderIndex, bool clearParent, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-item writes.");

    /// <summary>Create a checklist group on a task (F, #459) and return the server-confirmed
    /// <see cref="TaskChecklist"/> (a new empty group). Default throwing so read-only fakes needn't
    /// implement it; <see cref="ClickUpClient"/> overrides it. See <see cref="ClickUpClient.CreateChecklistAsync"/>.</summary>
    Task<TaskChecklist> CreateChecklistAsync(string taskId, string name, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-group writes.");

    /// <summary>Rename a checklist group (F, #459) and return the server-confirmed
    /// <see cref="TaskChecklist"/>. Default throwing; <see cref="ClickUpClient"/> overrides it.
    /// See <see cref="ClickUpClient.RenameChecklistAsync"/>.</summary>
    Task<TaskChecklist> RenameChecklistAsync(string checklistId, string name, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-group writes.");

    /// <summary>Delete a checklist group and all its items (F, #459). ClickUp returns an empty body, so this
    /// is a void write. Default throwing; <see cref="ClickUpClient"/> overrides it. See
    /// <see cref="ClickUpClient.DeleteChecklistAsync"/>.</summary>
    Task DeleteChecklistAsync(string checklistId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement checklist-group writes.");

    Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default);

    /// <summary>Full detail for a task addressed by its workspace <b>custom id</b> (#303, Ctrl+O
    /// quick-open) via <c>custom_task_ids=true&amp;team_id=…</c>. The returned <see cref="TaskDetail.Id"/>
    /// is the task's <b>plain</b> id, so the caller can then open it through the normal path. Default
    /// throwing implementation so read-only fakes needn't implement a lookup they never call (mirrors the
    /// membership writes); <see cref="ClickUpClient"/> overrides it. See
    /// <see cref="ClickUpClient.GetTaskDetailByCustomIdAsync"/>.</summary>
    Task<TaskDetail> GetTaskDetailByCustomIdAsync(string customId, string teamId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement custom-id task lookup.");

    Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default);

    /// <summary>A single task mapped to the stable <see cref="TaskItem"/> shape (#291) — <c>GET /task/{id}</c>
    /// through the same mapper the list/subtask fetches use, so it carries <see cref="TaskItem.ParentId"/>,
    /// structured assignees, and status/priority colours. The Task Tree tab uses it to walk a task's
    /// ancestry (repeated parent fetches) alongside <see cref="GetSubtasksAsync"/> for descendants. Default
    /// throwing implementation so read-only fakes needn't implement a lookup they never call (mirrors the
    /// membership writes / custom-id lookup); <see cref="ClickUpClient"/> overrides it.</summary>
    Task<TaskItem> GetTaskItemAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement single-task item lookup.");

    Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default);

    /// <summary>Post a plain-text comment to a task (#210) and return it as a <see cref="CommentItem"/>
    /// for optimistic append. Rich content (@-mentions, task links) is out of scope. See
    /// <see cref="ClickUpClient.CreateTaskCommentAsync"/> for the minimal-response mapping contract.</summary>
    Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default);

    /// <summary>Post a comment carrying structured runs — literal text and/or @-mention tags (#322) — to a
    /// task, the write substrate for the #325 @-mention composer. Default throwing implementation so
    /// read-only fakes need not implement it; <see cref="ClickUpClient"/> overrides it. See
    /// <see cref="ClickUpClient.CreateTaskCommentAsync(string, IReadOnlyList{CommentRun}, CancellationToken)"/>
    /// for the block mapping and minimal-response contract.</summary>
    Task<CommentItem> CreateTaskCommentAsync(string taskId, IReadOnlyList<CommentRun> runs, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement structured-comment writes.");

    /// <summary>Fetch the replies in a comment's thread (#327). Default throwing implementation so
    /// existing fakes need not implement it; <see cref="ClickUpClient"/> overrides it. See
    /// <see cref="ClickUpClient.GetThreadedCommentsAsync"/>.</summary>
    Task<IReadOnlyList<CommentItem>> GetThreadedCommentsAsync(string commentId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement threaded-comment reads.");

    /// <summary>Post a plain-text reply into a comment's thread (#327) and return it as a
    /// <see cref="CommentItem"/> for optimistic append. Default throwing implementation as above;
    /// <see cref="ClickUpClient"/> overrides it. See
    /// <see cref="ClickUpClient.CreateThreadedCommentAsync"/> for the minimal-response mapping contract.</summary>
    Task<CommentItem> CreateThreadedCommentAsync(string commentId, string text, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement threaded-comment writes.");
}
