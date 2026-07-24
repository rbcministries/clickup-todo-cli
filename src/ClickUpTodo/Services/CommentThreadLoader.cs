using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Loads reply threads into a flat comment list (#328, part of epic #314 — Threaded comments): for each
/// parent comment that reports a thread (<see cref="CommentItem.ReplyCount"/> &gt; 0), fetches its replies
/// and attaches them to the parent's <see cref="CommentItem.Replies"/>, stamped so a reply knows both the
/// comment it answers (<see cref="CommentItem.ParentCommentId"/>) and the task it belongs to
/// (<see cref="CommentItem.TaskId"/> — the reply payload carries no task context, so #327 leaves it null).
/// </summary>
/// <remarks>
/// The concurrency-bearing logic lives in the <c>internal static</c> <see cref="LoadRepliesAsync"/> so it is
/// unit-testable offline (via <c>InternalsVisibleTo</c>) with an in-memory fetcher — mirroring the
/// delegate-driven, <see cref="System.Threading.SemaphoreSlim"/>-bounded fan-out of
/// <see cref="FeedService.GatherAsync"/>. Only comments that report replies trigger a fetch, so a comment
/// without a thread incurs no extra call, and a busy task can't set off an N+1 fetch storm.
/// </remarks>
public static class CommentThreadLoader
{
    /// <summary>Default cap on how many reply threads are fetched concurrently for one task's comments.
    /// Bounded so a comment-heavy task doesn't open a fetch per thread all at once, while still hiding
    /// per-call latency; mirrors <see cref="FeedService.DefaultMaxConcurrency"/>.</summary>
    public const int DefaultMaxConcurrency = 8;

    /// <summary>
    /// Returns <paramref name="comments"/> with each parent that reports a thread
    /// (<see cref="CommentItem.ReplyCount"/> &gt; 0 and a non-empty <see cref="CommentItem.Id"/>) enriched
    /// with its replies via <paramref name="fetchReplies"/>, at most <paramref name="maxConcurrency"/>
    /// fetches in flight. Each reply is stamped with its parent's id (<see cref="CommentItem.ParentCommentId"/>)
    /// and task (<see cref="CommentItem.TaskId"/>), then ordered oldest-first by
    /// <see cref="CommentItem.DateMs"/> (ties by <see cref="CommentItem.Id"/>) for a deterministic, top-down
    /// thread order. Comments without a reported thread are returned unchanged and incur <b>no</b> fetch.
    /// Input order and identity are preserved.
    /// <para>
    /// Best-effort per thread: a reply fetch that throws yields the parent with empty
    /// <see cref="CommentItem.Replies"/> rather than failing the whole load — including a transient per-call
    /// timeout (a <see cref="TaskCanceledException"/>); only a genuine caller cancellation
    /// (<paramref name="ct"/> signalled) propagates. <c>internal</c> and delegate-driven so it can be
    /// unit-tested with an in-memory fetcher.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<CommentItem>> LoadRepliesAsync(
        IReadOnlyList<CommentItem> comments,
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetchReplies,
        int maxConcurrency,
        CancellationToken ct = default)
    {
        if (comments.Count == 0)
            return comments;

        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var result = new CommentItem[comments.Count];

        async Task LoadOneAsync(int index)
        {
            var comment = comments[index];
            // No reported thread (or a degenerate empty id) → leave the comment untouched and, crucially,
            // fetch nothing. This is what keeps a comment without replies free of any extra call.
            if (comment.ReplyCount <= 0 || string.IsNullOrEmpty(comment.Id))
            {
                result[index] = comment;
                return;
            }

            await gate.WaitAsync(ct);
            try
            {
                var replies = await fetchReplies(comment.Id, ct);
                result[index] = comment with { Replies = StampAndOrder(replies, comment) };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine caller cancellation (e.g. app shutdown) — let it propagate
            }
            catch (Exception)
            {
                // Best-effort: a thread we can't fetch contributes no replies rather than failing the whole
                // load. Also swallows a per-call HttpClient timeout (a TaskCanceledException whose token
                // isn't ours) so one slow thread can't abort the comment load. Mirrors FeedService.GatherAsync.
                result[index] = comment with { Replies = [] };
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, comments.Count).Select(LoadOneAsync));
        return result;
    }

    /// <summary>
    /// Stamps each reply with its <paramref name="parent"/>'s id (<see cref="CommentItem.ParentCommentId"/>)
    /// and task (<see cref="CommentItem.TaskId"/>), and orders the thread oldest-first by
    /// <see cref="CommentItem.DateMs"/> (an undated reply sorts last, ties broken by
    /// <see cref="CommentItem.Id"/> for a deterministic order). Pure and unit-testable.
    /// </summary>
    private static IReadOnlyList<CommentItem> StampAndOrder(IReadOnlyList<CommentItem> replies, CommentItem parent)
        => replies
            .Select(r => r with { ParentCommentId = parent.Id, TaskId = parent.TaskId })
            .OrderBy(r => r.DateMs ?? long.MaxValue)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
}
