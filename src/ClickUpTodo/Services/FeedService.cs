using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The result of a feed load (#117): the aggregated <see cref="Comments"/> feed (mentions &amp; comments,
/// #109) plus the recent-<see cref="Activity"/> projection of the same assigned tasks. Both are already
/// newest-first. The feed screen shows <see cref="Comments"/> always and merges <see cref="Activity"/>
/// in only when its <c>F6</c> "show activity" display state is on — a client-side re-render, since both
/// sets come from one fetch.
/// </summary>
public sealed record FeedResult(
    IReadOnlyList<CommentItem> Comments, IReadOnlyList<ActivityItem> Activity)
{
    /// <summary>An empty result (no comments, no activity) — a convenient default.</summary>
    public static readonly FeedResult Empty = new([], []);
}

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
public sealed class FeedService(IClickUpClient client, TaskService taskService, AppConfig config, TimeProvider? timeProvider = null)
{
    // Clock for the look-back window (#244); injectable so ComputeUpdatedAfterMs's caller is testable.
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Default number of tasks whose comments are fetched concurrently. Bounded so a large
    /// assigned-task set doesn't open a fetch per task all at once, while still hiding per-call latency.</summary>
    public const int DefaultMaxConcurrency = 8;

    /// <summary>Default cap on the number of feed entries returned. The newest entries are kept; the
    /// cap is a bound on feed size, not a claim of complete history (a very busy workspace can have
    /// more comments than this).</summary>
    public const int DefaultMaxEntries = 200;

    // The signed-in user, resolved lazily on first feed load and reused thereafter so a background
    // refresh (#116) doesn't re-fetch the (session-stable) identity every tick. Idempotent, so an
    // unsynchronised double-fetch on the first two concurrent loads is harmless.
    private ClickUpUser? _currentUser;

    /// <summary>
    /// Loads the comment feed for the user's actionable tasks: resolves the view's <c>Assignee IS</c>
    /// rules to an assignee-id set, fetches the assigned tasks (<see cref="ClickUpClient.GetAssignedTasksAsync"/>),
    /// then fans out <see cref="ClickUpClient.GetTaskCommentsAsync"/> across them and merges the results
    /// into one newest-first, de-duplicated, capped list. Each entry is stamped with
    /// <see cref="CommentItem.MentionsMe"/> (#113) against the signed-in user; when
    /// <paramref name="mentionsOnly"/> is true the result is filtered to mentions only. Best-effort per
    /// task (a task whose comments can't be fetched is skipped); genuine cancellation propagates.
    /// <para>
    /// <paramref name="includeClosed"/> is the feed's F12 "Show Completed" toggle
    /// (<see cref="AppConfig.FeedShowCompleted"/>): off (default) the assigned-task fetch is open-only,
    /// so comments on a ticket that has since closed don't appear; on, closed tasks are included so their
    /// activity surfaces. It's passed in (not read from <c>config</c> here) so the caller captures the
    /// flag on the UI thread at fetch-start — the flag is toggled at runtime while the feed is open, and
    /// a worker-thread read could otherwise disagree with the cache key the result is saved under.
    /// </para>
    /// </summary>
    public async Task<FeedResult> LoadFeedAsync(
        bool includeClosed, bool mentionsOnly = false, CancellationToken ct = default)
    {
        var assigneeIds = await taskService.ResolveAssigneeIdsAsync(config.View, ct);
        // Optional server-side look-back window (#244): when configured, shrink the fetch to tasks
        // updated in the last N days. Null (the default) leaves the full-set fetch unchanged.
        //
        // The window is intentionally NOT part of FeedCache.KeyFor: it is time-relative (now − N days),
        // so keying on it would make the cache key perpetually unstable (never a hit) for no benefit.
        // The bounded instant-paint is reconciled by the near-immediate live refresh; workspace/
        // assignees/completed are still keyed, so the cache can never surface a wrong feed.
        var updatedAfterMs = ComputeUpdatedAfterMs(config.FeedActivityLookbackDays, _clock.GetUtcNow());
        var tasks = await client.GetAssignedTasksAsync(
            config.WorkspaceId, assigneeIds, includeClosed: includeClosed, updatedAfterMs: updatedAfterMs, ct: ct);

        var taskIds = tasks
            .Select(t => t.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var feed = await GatherAsync(taskIds, client.GetTaskCommentsAsync, DefaultMaxConcurrency, DefaultMaxEntries, ct);

        var me = _currentUser ??= await client.GetMeAsync(ct);
        var comments = StampMentions(feed, MentionSpec.ForUser(me), mentionsOnly);

        // The recent-activity source (#117) is a pure projection of the tasks we already fetched above —
        // no extra endpoint. F12 (includeClosed) bounds it exactly as it does the comments: when off, a
        // closed task pulled in as a subtask anchor (the server returns those even with include_closed
        // false when subtasks=true) is dropped, matching what TaskView.Apply hides on the dashboard.
        var activity = BuildActivity(tasks, DefaultMaxEntries, includeCompleted: includeClosed);
        return new FeedResult(comments, activity);
    }

    /// <summary>Defensive upper bound (~100 years) on the look-back window (#244). The F2 settings
    /// field already clamps to <see cref="Tui.Screens.SettingsForm.MaxLookbackDays"/>, but a
    /// hand-edited config can carry any value; capping here keeps <see cref="ComputeUpdatedAfterMs"/>
    /// from overflowing <see cref="DateTimeOffset"/> (subtracting more than ~730k days throws). Any
    /// value this large already means "effectively everything", so the cap changes no real behaviour.</summary>
    internal const int MaxLookbackDays = 36_500;

    /// <summary>
    /// Computes the <c>date_updated_gt</c> window (epoch ms) for the feed's assigned-task fetch (#244)
    /// from the configured look-back <paramref name="lookbackDays"/> and the current time
    /// <paramref name="now"/>. Returns null when the window is disabled (<c>0</c>) or non-positive
    /// (defensive against a hand-edited config), so the caller omits the filter and fetches the full
    /// set. A positive value is clamped to <see cref="MaxLookbackDays"/> so an out-of-range
    /// hand-edited config can't overflow <see cref="DateTimeOffset"/>. Pure and unit-testable.
    /// </summary>
    internal static long? ComputeUpdatedAfterMs(int lookbackDays, DateTimeOffset now)
        => lookbackDays > 0 ? now.AddDays(-Math.Min(lookbackDays, MaxLookbackDays)).ToUnixTimeMilliseconds() : null;

    /// <summary>
    /// Projects the assigned tasks into the recent-activity feed (#117): each task with a non-empty id
    /// becomes an <see cref="ActivityItem"/>, de-duplicated by task id (paging with <c>subtasks=true</c>
    /// can return the same task twice — the comment path de-dups ids for the same reason), ordered
    /// newest-first by <see cref="TaskItem.UpdatedMs"/> (an undated task sorts last, ties broken by task
    /// id for a deterministic order), and capped to <paramref name="maxEntries"/> — the same bound the
    /// comment feed uses. When <paramref name="includeCompleted"/> is false, completed
    /// (<see cref="TaskView.IsCompleted"/>) tasks are dropped, mirroring the F12/<c>include_closed</c>
    /// bound the dashboard applies (a closed subtask anchor otherwise leaks in). Pure and unit-testable.
    /// </summary>
    internal static IReadOnlyList<ActivityItem> BuildActivity(
        IReadOnlyList<TaskItem> tasks, int maxEntries, bool includeCompleted = true)
        => tasks
            .Where(t => !string.IsNullOrEmpty(t.Id))
            .Where(t => includeCompleted || !TaskView.IsCompleted(t))
            .Select(ActivityItem.FromTask)
            .DistinctBy(a => a.Id, StringComparer.Ordinal)
            .OrderByDescending(a => a.UpdatedMs ?? long.MinValue)
            .ThenBy(a => a.TaskId, StringComparer.Ordinal)
            .Take(Math.Max(0, maxEntries))
            .ToList();

    /// <summary>
    /// Stamps <see cref="CommentItem.MentionsMe"/> on each feed entry via <see cref="MentionDetector"/>
    /// against <paramref name="spec"/>, and — when <paramref name="mentionsOnly"/> is true — filters the
    /// result to mentioned entries only (newest-first order preserved). Pure and unit-testable offline.
    /// </summary>
    internal static IReadOnlyList<CommentItem> StampMentions(
        IReadOnlyList<CommentItem> feed, MentionSpec spec, bool mentionsOnly)
    {
        var stamped = new List<CommentItem>(feed.Count);
        foreach (var comment in feed)
        {
            var mentionsMe = MentionDetector.Mentions(comment, spec);
            if (mentionsOnly && !mentionsMe)
                continue;
            stamped.Add(comment.MentionsMe == mentionsMe ? comment : comment with { MentionsMe = mentionsMe });
        }

        return stamped;
    }

    /// <summary>
    /// Fans <paramref name="fetchComments"/> out over <paramref name="taskIds"/> with at most
    /// <paramref name="maxConcurrency"/> in flight, gathers the per-task results, and merges them via
    /// <see cref="Aggregate"/> into a newest-first, de-duplicated list capped to
    /// <paramref name="maxEntries"/>. Best-effort: a task whose fetch throws — including a transient
    /// per-task timeout (a <see cref="TaskCanceledException"/>) — contributes nothing rather than failing
    /// the whole feed; only a genuine caller cancellation (<paramref name="ct"/> signalled) propagates.
    /// <c>internal</c> and delegate-driven so it can be unit-tested with an in-memory fetcher.
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine caller cancellation (e.g. app shutdown) — let it propagate
            }
            catch (Exception)
            {
                // Best-effort: a task we can't fetch contributes nothing. This deliberately also
                // swallows a per-task HttpClient timeout, which surfaces as a TaskCanceledException
                // (an OperationCanceledException) even though our ct wasn't signalled — so one slow
                // task can't abort the whole feed. Mirrors TaskService.ResolveAssigneeIdsAsync.
                results[index] = [];
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
