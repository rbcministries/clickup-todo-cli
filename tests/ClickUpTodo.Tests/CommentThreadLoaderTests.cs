using System.Collections.Concurrent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="CommentThreadLoader.LoadRepliesAsync"/> (#328): only comments that report a
/// thread trigger a fetch, replies are stamped with their parent id + task and ordered oldest-first, the
/// fan-out is bounded and best-effort, and input order/identity are preserved. Delegate-driven, so an
/// in-memory recording fetcher stands in for the live reply endpoint.
/// </summary>
public sealed class CommentThreadLoaderTests
{
    private static CommentItem Comment(string id, int replyCount = 0, string? taskId = null, long? dateMs = null)
        => new(id, Author: "author", DateMs: dateMs, Text: $"comment {id}", Resolved: false, TaskId: taskId,
            ReplyCount: replyCount);

    private static CommentItem Reply(string id, long? dateMs = null)
        => new(id, Author: "replier", DateMs: dateMs, Text: $"reply {id}", Resolved: false);

    /// <summary>Records which comment ids replies were fetched for, and hands back a per-id thread.</summary>
    private sealed class RecordingFetcher(IReadOnlyDictionary<string, IReadOnlyList<CommentItem>> threads)
    {
        public ConcurrentBag<string> FetchedIds { get; } = [];

        public Task<IReadOnlyList<CommentItem>> FetchAsync(string commentId, CancellationToken ct)
        {
            FetchedIds.Add(commentId);
            return Task.FromResult(threads.TryGetValue(commentId, out var t) ? t : []);
        }
    }

    [Fact]
    public async Task LoadReplies_fetches_only_comments_that_report_a_thread()
    {
        var comments = new[]
        {
            Comment("a", replyCount: 2),
            Comment("b", replyCount: 0),   // no thread — must not be fetched
            Comment("c", replyCount: 1),
        };
        var fetcher = new RecordingFetcher(new Dictionary<string, IReadOnlyList<CommentItem>>
        {
            ["a"] = [Reply("a1")],
            ["c"] = [Reply("c1")],
        });

        var result = await CommentThreadLoader.LoadRepliesAsync(comments, fetcher.FetchAsync, maxConcurrency: 4);

        Assert.Equal(new[] { "a", "c" }, fetcher.FetchedIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(result.Single(c => c.Id == "b").Replies);
        Assert.Single(result.Single(c => c.Id == "a").Replies);
    }

    [Fact]
    public async Task LoadReplies_never_fetches_a_comment_with_an_empty_id()
    {
        // A degenerate empty-id comment that (nonsensically) reports replies must still not be fetched —
        // there's no thread to key by, and it guards the fetcher from a bad request.
        var comments = new[] { Comment("", replyCount: 3) };
        var fetcher = new RecordingFetcher(new Dictionary<string, IReadOnlyList<CommentItem>>());

        var result = await CommentThreadLoader.LoadRepliesAsync(comments, fetcher.FetchAsync, maxConcurrency: 4);

        Assert.Empty(fetcher.FetchedIds);
        Assert.Empty(result.Single().Replies);
    }

    [Fact]
    public async Task LoadReplies_stamps_replies_with_parent_id_and_task()
    {
        var comments = new[] { Comment("p", replyCount: 1, taskId: "task-1") };
        var fetcher = new RecordingFetcher(new Dictionary<string, IReadOnlyList<CommentItem>>
        {
            // The reply payload carries no task context (#327 leaves TaskId null); the loader stamps it.
            ["p"] = [Reply("r1")],
        });

        var result = await CommentThreadLoader.LoadRepliesAsync(comments, fetcher.FetchAsync, maxConcurrency: 4);

        var reply = result.Single().Replies.Single();
        Assert.Equal("p", reply.ParentCommentId);
        Assert.Equal("task-1", reply.TaskId);
    }

    [Fact]
    public async Task LoadReplies_orders_a_thread_oldest_first()
    {
        var comments = new[] { Comment("p", replyCount: 3) };
        var fetcher = new RecordingFetcher(new Dictionary<string, IReadOnlyList<CommentItem>>
        {
            // Supplied newest-first (as ClickUp returns them) and with an undated reply that must sort last.
            ["p"] = [Reply("newer", dateMs: 300), Reply("older", dateMs: 100), Reply("undated", dateMs: null)],
        });

        var result = await CommentThreadLoader.LoadRepliesAsync(comments, fetcher.FetchAsync, maxConcurrency: 4);

        Assert.Equal(new[] { "older", "newer", "undated" }, result.Single().Replies.Select(r => r.Id));
    }

    [Fact]
    public async Task LoadReplies_is_best_effort_when_a_thread_fetch_throws()
    {
        var comments = new[] { Comment("a", replyCount: 1), Comment("b", replyCount: 1) };

        Task<IReadOnlyList<CommentItem>> Fetch(string id, CancellationToken ct)
            => id == "a"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult<IReadOnlyList<CommentItem>>([Reply("b1")]);

        var result = await CommentThreadLoader.LoadRepliesAsync(comments, Fetch, maxConcurrency: 4);

        Assert.Empty(result.Single(c => c.Id == "a").Replies);   // failed thread → no replies, no throw
        Assert.Single(result.Single(c => c.Id == "b").Replies);  // the other thread still loads
    }

    [Fact]
    public async Task LoadReplies_preserves_input_order_and_identity()
    {
        var a = Comment("a", replyCount: 0);
        var b = Comment("b", replyCount: 0);
        var comments = new[] { a, b };
        var fetcher = new RecordingFetcher(new Dictionary<string, IReadOnlyList<CommentItem>>());

        var result = await CommentThreadLoader.LoadRepliesAsync(comments, fetcher.FetchAsync, maxConcurrency: 4);

        Assert.Equal(new[] { "a", "b" }, result.Select(c => c.Id));
        // No thread to load → the exact same instances flow through untouched.
        Assert.Same(a, result[0]);
        Assert.Same(b, result[1]);
    }

    [Fact]
    public async Task LoadReplies_never_exceeds_max_concurrency()
    {
        var comments = Enumerable.Range(0, 20).Select(i => Comment($"c{i}", replyCount: 1)).ToArray();
        var inFlight = 0;
        var peak = 0;
        var sync = new object();

        async Task<IReadOnlyList<CommentItem>> Fetch(string id, CancellationToken ct)
        {
            lock (sync) { inFlight++; peak = Math.Max(peak, inFlight); }
            try { await Task.Delay(5, ct); return [Reply($"{id}-r")]; }
            finally { lock (sync) { inFlight--; } }
        }

        await CommentThreadLoader.LoadRepliesAsync(comments, Fetch, maxConcurrency: 3);

        Assert.True(peak <= 3, $"peak concurrency {peak} exceeded the cap of 3");
    }

    [Fact]
    public async Task LoadReplies_propagates_caller_cancellation()
    {
        var comments = new[] { Comment("a", replyCount: 1) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Task<IReadOnlyList<CommentItem>> Fetch(string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CommentItem>>([]);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CommentThreadLoader.LoadRepliesAsync(comments, Fetch, maxConcurrency: 4, cts.Token));
    }

    [Fact]
    public async Task LoadReplies_on_empty_list_returns_empty_without_fetching()
    {
        var fetcher = new RecordingFetcher(new Dictionary<string, IReadOnlyList<CommentItem>>());

        var result = await CommentThreadLoader.LoadRepliesAsync([], fetcher.FetchAsync, maxConcurrency: 4);

        Assert.Empty(result);
        Assert.Empty(fetcher.FetchedIds);
    }
}
