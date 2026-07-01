using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// Fetches and merges the user's actionable tasks (assigned-to-me ∪ Personal Tasks list),
/// de-duplicated and stably ordered, and resolves per-list status options on demand (cached).
/// </summary>
public sealed class TaskService(ClickUpClient client, AppConfig config, long userId, TimeProvider? timeProvider = null)
{
    // Per-list status options, cached with a long TTL (statuses rarely change) and warmed by
    // PrefetchStatusesAsync so the picker opens from cache in the common case.
    private readonly StatusCache _statusCache = new(client.GetListStatusesAsync, timeProvider);

    /// <summary>Merged, de-duplicated, stably-ordered task snapshot.</summary>
    public async Task<IReadOnlyList<TaskItem>> LoadAsync(CancellationToken ct = default)
    {
        var assigned = await client.GetAssignedTasksAsync(config.WorkspaceId, userId, ct);
        var personal = await client.GetListTasksAsync(config.PersonalTasksListId, ct);

        // De-dup by task id; a task assigned to me that also lives on my personal list appears once.
        var byId = new Dictionary<string, TaskItem>();
        foreach (var task in assigned.Concat(personal))
            byId[task.Id] = task;

        return ExcludeByStatus(byId.Values, config.ExcludedStatuses)
            .OrderBy(t => t, TaskOrder.Instance)
            .ToList();
    }

    /// <summary>Filters out tasks whose status is in the excluded set (case-insensitive).</summary>
    internal static IEnumerable<TaskItem> ExcludeByStatus(IEnumerable<TaskItem> tasks, IEnumerable<string> excluded)
    {
        var set = new HashSet<string>(
            excluded.Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);
        return set.Count == 0
            ? tasks
            : tasks.Where(t => string.IsNullOrWhiteSpace(t.StatusName) || !set.Contains(t.StatusName));
    }

    /// <summary>The available statuses for a list, served from the TTL cache or fetched on demand.</summary>
    public Task<IReadOnlyList<StatusOption>> GetStatusesForListAsync(string listId, CancellationToken ct = default)
        => _statusCache.GetAsync(listId, ct);

    /// <summary>A list's statuses if cached and still fresh, without a fetch (for opening the picker instantly).</summary>
    public bool TryGetCachedStatuses(string listId, out IReadOnlyList<StatusOption> statuses)
        => _statusCache.TryGetFresh(listId, out statuses);

    /// <summary>Warms the status cache for the given lists (best-effort) so the picker opens from cache.</summary>
    public Task PrefetchStatusesAsync(IEnumerable<string> listIds, CancellationToken ct = default)
        => _statusCache.PrefetchAsync(listIds, ct);

    /// <summary>
    /// Sets a task's status and returns the <b>confirmed</b> status name from the write response
    /// (or null if the API omitted it), so the UI can show the server-confirmed value.
    /// </summary>
    public Task<string?> SetStatusAsync(string taskId, string statusName, CancellationToken ct = default)
        => client.SetTaskStatusAsync(taskId, statusName, ct);

    /// <summary>Full detail for a single task, fetched on demand for the detail view (#17).</summary>
    public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        => client.GetTaskDetailAsync(taskId, ct);

    /// <summary>The comments on a task, for the detail view's Comments tab (#17).</summary>
    public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default)
        => client.GetTaskCommentsAsync(taskId, ct);

    /// <summary>
    /// Returns a new snapshot with the task identified by <paramref name="taskId"/> carrying
    /// <paramref name="newStatus"/>, leaving every other task and the overall order untouched. Pure
    /// (the input list is not mutated) so the TUI can update one record in place without a reload.
    /// </summary>
    public static IReadOnlyList<TaskItem> ApplyStatusChange(IReadOnlyList<TaskItem> tasks, string taskId, string? newStatus)
        => tasks.Select(t => t.Id == taskId ? t with { StatusName = newStatus } : t).ToList();

    /// <summary>
    /// The distinct parent ids referenced by a subtask in <paramref name="snapshot"/> that aren't
    /// themselves present in it — the parents the nested subtasks view (#46) must pull in as context
    /// headers. Pure; order follows first appearance so the fetch is deterministic.
    /// </summary>
    internal static IReadOnlyList<string> MissingParentIds(IReadOnlyList<TaskItem> snapshot)
    {
        var present = new HashSet<string>(snapshot.Select(t => t.Id));
        var missing = new List<string>();
        var seen = new HashSet<string>();
        foreach (var t in snapshot)
        {
            if (string.IsNullOrEmpty(t.ParentId) || present.Contains(t.ParentId))
                continue;
            if (seen.Add(t.ParentId))
                missing.Add(t.ParentId);
        }
        return missing;
    }

    /// <summary>
    /// Fetches the parents of assigned subtasks that aren't themselves in <paramref name="snapshot"/>,
    /// mapped to <see cref="TaskItem"/> headers for the nested subtasks view. Best-effort: a parent
    /// that can't be fetched (deleted / no access) is skipped rather than failing the whole load.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, TaskItem>> ResolveContextParentsAsync(
        IReadOnlyList<TaskItem> snapshot, CancellationToken ct = default)
    {
        var result = new Dictionary<string, TaskItem>();
        foreach (var id in MissingParentIds(snapshot))
        {
            try
            {
                var d = await client.GetTaskDetailAsync(id, ct);
                // ParentId is intentionally left null: a context parent is a header for its subtask, so
                // it's always rendered at the top level (it isn't nested under its own parent here).
                result[id] = new TaskItem
                {
                    Id = d.Id,
                    Name = d.Name,
                    Url = d.Url,
                    StatusName = d.StatusName,
                    StatusColor = d.StatusColor,
                    ListId = d.ListId,
                    ListName = d.ListName,
                    DueDateMs = d.DueDateMs,
                    UpdatedMs = d.UpdatedMs,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: a parent we can't fetch just won't get a context header.
            }
        }
        return result;
    }

    /// <summary>Stable ordering: by due date (soonest first, undated last), then by name.</summary>
    private sealed class TaskOrder : IComparer<TaskItem>
    {
        public static readonly TaskOrder Instance = new();

        public int Compare(TaskItem? x, TaskItem? y)
        {
            if (x is null || y is null)
                return Comparer<object?>.Default.Compare(x, y);

            var dx = x.DueDateMs ?? long.MaxValue;
            var dy = y.DueDateMs ?? long.MaxValue;
            if (dx != dy)
                return dx.CompareTo(dy);

            var byName = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(x.Id, y.Id);
        }
    }
}
