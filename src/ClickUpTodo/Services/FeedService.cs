using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// Assembles the mentions/comments feed (#109): fans out per-task comment fetches across the user's
/// actionable tasks and merges them into a single, newest-first list, de-duplicated by comment id and
/// capped to a bounded size. This is the aggregation layer only (#112) — no mention detection (#113)
/// and no rendering (#114); the result is a flat <see cref="CommentItem"/> feed each entry of which is
/// already attributed to its task via <see cref="CommentItem.TaskId"/> (#111).
/// </summary>
/// <remarks>
/// <see cref="LoadFeedAsync"/> is naturally off the UI thread when awaited; the eventual screen
/// consumer (#114) invokes it through the <c>Task.Run</c> + <c>Application.Invoke</c> pattern used by
/// the detail view's <c>OpenDetail()</c>. The concurrency- and cap-bearing logic lives in the
/// <c>internal static</c> <see cref="Aggregate"/> / <see cref="GatherAsync"/> so it is unit-testable
/// offline (via <c>InternalsVisibleTo</c>) without a live ClickUp client.
/// </remarks>
public sealed class FeedService(ClickUpClient client, TaskService taskService, AppConfig config)
{
    /// <summary>Default number of tasks whose comments are fetched concurrently. Bounded so a large
    /// assigned-task set doesn't open a fetch per task all at once, while still hiding per-call latency.</summary>
    public const int DefaultMaxConcurrency = 8;

    /// <summary>Default cap on the number of feed entries returned. The newest entries are kept; the
    /// cap is a bound on feed size, not a claim of complete history (a very busy workspace can have
    /// more comments than this).</summary>
    public const int DefaultMaxEntries = 200;

    /// <summary>
    /// Loads the comment feed for the user's actionable tasks: resolves the view's <c>Assignee IS</c>
    /// rules to an assignee-id set, fetches the assigned tasks (<see cref="ClickUpClient.GetAssignedTasksAsync"/>),
    /// then fans out <see cref="ClickUpClient.GetTaskCommentsAsync"/> across them and merges the results
    /// into one newest-first, de-duplicated, capped list. Best-effort per task (a task whose comments
    /// can't be fetched is skipped); genuine cancellation propagates.
    /// </summary>
    public async Task<IReadOnlyList<CommentItem>> LoadFeedAsync(CancellationToken ct = default)
    {
        var assigneeIds = await taskService.ResolveAssigneeIdsAsync(config.View, ct);
        var tasks = await client.GetAssignedTasksAsync(config.WorkspaceId, assigneeIds, ct);

        var taskIds = tasks
            .Select(t => t.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return await GatherAsync(taskIds, client.GetTaskCommentsAsync, DefaultMaxConcurrency, DefaultMaxEntries, ct);
    }

    /// <summary>
    /// Fans <paramref name="fetchComments"/> out over <paramref name="taskIds"/> with at most
    /// <paramref name="maxConcurrency"/> in flight, gathers the per-task results, and merges them via
    /// <see cref="Aggregate"/> into a newest-first, de-duplicated list capped to
    /// <paramref name="maxEntries"/>. Best-effort: a task whose fetch throws contributes nothing (mirrors
    /// <see cref="TaskService.ResolveContextParentsAsync"/>); an <see cref="OperationCanceledException"/>
    /// propagates so genuine cancellation isn't swallowed. <c>internal</c> and delegate-driven so it can
    /// be unit-tested with an in-memory fetcher.
    /// </summary>
    internal static async Task<IReadOnlyList<CommentItem>> GatherAsync(
        IReadOnlyList<string> taskIds,
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetchComments,
        int maxConcurrency,
        int maxEntries,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return [];

        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var results = new IReadOnlyList<CommentItem>[taskIds.Count];

        async Task FetchOneAsync(int index)
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = await fetchComments(taskIds[index], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[index] = []; // best-effort: a task we can't fetch contributes nothing
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, taskIds.Count).Select(FetchOneAsync));
        return Aggregate(results, maxEntries);
    }

    /// <summary>
    /// Merges the per-task comment lists into one newest-first feed: de-duplicates by non-empty comment
    /// id (first occurrence wins — comment ids are globally unique, so this only collapses a comment that
    /// surfaced under two tasks), orders by <see cref="CommentItem.DateMs"/> descending (a null/undated
    /// comment sorts last, ties broken by id for a deterministic order), and caps to
    /// <paramref name="maxEntries"/>. Empty-id comments (degenerate) are kept as distinct rather than
    /// collapsed together. Pure and unit-testable.
    /// </summary>
    internal static IReadOnlyList<CommentItem> Aggregate(
        IEnumerable<IReadOnlyList<CommentItem>?> perTask, int maxEntries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<CommentItem>();
        foreach (var list in perTask)
        {
            if (list is null)
                continue;
            foreach (var comment in list)
            {
                if (!string.IsNullOrEmpty(comment.Id) && !seen.Add(comment.Id))
                    continue; // a comment already seen under another task — keep the first
                merged.Add(comment);
            }
        }

        return merged
            .OrderByDescending(c => c.DateMs ?? long.MinValue)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, maxEntries))
            .ToList();
    }
}
