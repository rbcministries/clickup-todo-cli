using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="FeedService"/>'s offline cores (#112): the pure <c>Aggregate</c>
/// (merge / de-dup / newest-first order / cap) and the delegate-driven <c>GatherAsync</c> fan-out
/// (bounded concurrency, best-effort per task, cancellation propagation). The live
/// <c>LoadFeedAsync</c> glue is exercised by the app; here we pin the decision logic without a
/// ClickUp client.
/// </summary>
public sealed class FeedServiceTests
{
    private static CommentItem C(string id, long? date, string? taskId = null)
        => new(id, "author", date, "text", false, taskId);

    // ── Aggregate ───────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_MergesTasksIntoOneNewestFirstList()
    {
        var result = FeedService.Aggregate(
            new[]
            {
                new[] { C("a", 100), C("b", 300) },
                new[] { C("c", 200) },
            },
            maxEntries: 100);

        Assert.Equal(new[] { "b", "c", "a" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Aggregate_DedupsByIdKeepingFirst_AndPreservesDistinctEmptyIds()
    {
        var first = new CommentItem("dup", "first", 100, "t", false, "task1");
        var second = new CommentItem("dup", "second", 100, "t", false, "task2");
        var blank1 = new CommentItem("", "x", 50, "t", false, "task1");
        var blank2 = new CommentItem("", "y", 40, "t", false, "task2");

        var result = FeedService.Aggregate(
            new[] { new[] { first, blank1 }, new[] { second, blank2 } }, maxEntries: 100);

        // The shared "dup" id collapses to its first occurrence; both empty-id comments survive.
        Assert.Equal(3, result.Count);
        Assert.Equal("first", Assert.Single(result, c => c.Id == "dup").Author);
        Assert.Equal(2, result.Count(c => c.Id == ""));
    }

    [Fact]
    public void Aggregate_NullDateSortsLast_AndTiesAreDeterministic()
    {
        var result = FeedService.Aggregate(
            new[] { new[] { C("z", null), C("m", 100), C("a", 100), C("n", null) } },
            maxEntries: 100);

        // Dated comments first (100 tie → id ordinal a, m), then undated (id ordinal n, z).
        Assert.Equal(new[] { "a", "m", "n", "z" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Aggregate_CapsToTheNewestEntries()
    {
        var result = FeedService.Aggregate(
            new[] { new[] { C("old", 1), C("mid", 2), C("new", 3) } }, maxEntries: 2);

        Assert.Equal(new[] { "new", "mid" }, result.Select(c => c.Id));
    }

    [Fact]
    public void Aggregate_NonPositiveCap_ReturnsEmpty()
    {
        var input = new[] { new[] { C("a", 1) } };
        Assert.Empty(FeedService.Aggregate(input, maxEntries: 0));
        Assert.Empty(FeedService.Aggregate(input, maxEntries: -5));
    }

    [Fact]
    public void Aggregate_EmptyOrNullInput_ReturnsEmpty()
    {
        Assert.Empty(FeedService.Aggregate(Array.Empty<IReadOnlyList<CommentItem>>(), maxEntries: 100));
        Assert.Empty(FeedService.Aggregate(new IReadOnlyList<CommentItem>?[] { null }, maxEntries: 100));
    }

    // ── GatherAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GatherAsync_EmptyTaskIds_ReturnsEmptyWithoutFetching()
    {
        var feed = await FeedService.GatherAsync(
            [],
            (_, _) => throw new InvalidOperationException("fetch should not be called"),
            maxConcurrency: 4,
            maxEntries: 100);

        Assert.Empty(feed);
    }

    [Fact]
    public async Task GatherAsync_MergesDedupsAndCapsAcrossTasks()
    {
        var byTask = new Dictionary<string, IReadOnlyList<CommentItem>>(StringComparer.Ordinal)
        {
            ["a"] = new[] { C("c1", 30), C("dup", 10) },
            ["b"] = new[] { C("c2", 20), C("dup", 10) }, // "dup" shared across the two tasks
        };
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch =
            (id, _) => Task.FromResult(byTask[id]);

        var feed = await FeedService.GatherAsync(new[] { "a", "b" }, fetch, maxConcurrency: 4, maxEntries: 2);

        // dedup → c1(30), c2(20), dup(10); newest-2 cap drops dup.
        Assert.Equal(new[] { "c1", "c2" }, feed.Select(c => c.Id));
    }

    [Fact]
    public async Task GatherAsync_SkipsTasksWhoseFetchThrows()
    {
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch = (id, _) =>
            id == "boom"
                ? throw new InvalidOperationException("transient")
                : Task.FromResult<IReadOnlyList<CommentItem>>(new[] { C($"c-{id}", 1) });

        var feed = await FeedService.GatherAsync(
            new[] { "ok1", "boom", "ok2" }, fetch, maxConcurrency: 4, maxEntries: 100);

        Assert.Equal(new[] { "c-ok1", "c-ok2" }, feed.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GatherAsync_PropagatesGenuineCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // caller cancelled (e.g. app shutdown) — must not be swallowed as best-effort
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch =
            (id, _) => Task.FromResult<IReadOnlyList<CommentItem>>(new[] { C($"c-{id}", 1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeedService.GatherAsync(new[] { "t1" }, fetch, maxConcurrency: 4, maxEntries: 100, cts.Token));
    }

    [Fact]
    public async Task GatherAsync_TreatsPerTaskTimeoutAsBestEffort()
    {
        // An HttpClient timeout surfaces as a TaskCanceledException (an OperationCanceledException)
        // even though the caller's token was never signalled; one slow task must not abort the feed.
        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch = (id, _) =>
            id == "slow"
                ? throw new TaskCanceledException()
                : Task.FromResult<IReadOnlyList<CommentItem>>(new[] { C($"c-{id}", 1) });

        var feed = await FeedService.GatherAsync(
            new[] { "ok1", "slow", "ok2" }, fetch, maxConcurrency: 4, maxEntries: 100);

        Assert.Equal(new[] { "c-ok1", "c-ok2" }, feed.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GatherAsync_NeverExceedsMaxConcurrency()
    {
        const int maxConcurrency = 3;
        var ids = Enumerable.Range(0, 10).Select(i => $"t{i}").ToList();

        var current = 0;
        var peak = 0;
        var peakLock = new object();
        var reachedBound = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Func<string, CancellationToken, Task<IReadOnlyList<CommentItem>>> fetch = async (id, _) =>
        {
            var inFlight = Interlocked.Increment(ref current);
            lock (peakLock)
                peak = Math.Max(peak, inFlight);
            if (inFlight >= maxConcurrency)
                reachedBound.TrySetResult();
            await release.Task; // hold every admitted fetch open until the test releases them
            Interlocked.Decrement(ref current);
            return new[] { C(id, 1) };
        };

        var run = FeedService.GatherAsync(ids, fetch, maxConcurrency, maxEntries: 100);

        await reachedBound.Task;   // the gate admitted a full batch of `maxConcurrency`
        await Task.Delay(50);      // a broken bound would let more slip in during this window
        Assert.Equal(maxConcurrency, peak);

        release.SetResult();
        var feed = await run;
        Assert.Equal(10, feed.Count);
    }

    // ── StampMentions (#113) ──────────────────────────────────────────────────

    private static readonly MentionSpec BenSpec = MentionSpec.ForUser(new ClickUpUser(7, "Ben"));

    private static CommentItem Msg(string id, string text)
        => new(id, "author", 100, text, false, "task1");

    [Fact]
    public void StampMentions_SetsMentionsMePerEntry()
    {
        var feed = new[]
        {
            Msg("a", "cc @Ben please look"),
            Msg("b", "unrelated comment"),
        };

        var stamped = FeedService.StampMentions(feed, BenSpec, mentionsOnly: false);

        Assert.Equal(new[] { "a", "b" }, stamped.Select(c => c.Id));
        Assert.True(stamped.Single(c => c.Id == "a").MentionsMe);
        Assert.False(stamped.Single(c => c.Id == "b").MentionsMe);
    }

    [Fact]
    public void StampMentions_MentionsOnly_FiltersToMentionsPreservingOrder()
    {
        var feed = new[]
        {
            Msg("a", "@Ben first"),
            Msg("b", "no mention here"),
            Msg("c", "and @Ben again"),
        };

        var stamped = FeedService.StampMentions(feed, BenSpec, mentionsOnly: true);

        Assert.Equal(new[] { "a", "c" }, stamped.Select(c => c.Id));
        Assert.All(stamped, c => Assert.True(c.MentionsMe));
    }

    [Fact]
    public void StampMentions_EmptyFeed_YieldsEmpty()
    {
        Assert.Empty(FeedService.StampMentions(Array.Empty<CommentItem>(), BenSpec, mentionsOnly: true));
        Assert.Empty(FeedService.StampMentions(Array.Empty<CommentItem>(), BenSpec, mentionsOnly: false));
    }

    [Fact]
    public void StampMentions_EmptySpec_MarksNothingAndMentionsOnlyIsEmpty()
    {
        var feed = new[] { Msg("a", "@Ben here"), Msg("b", "plain") };

        var all = FeedService.StampMentions(feed, MentionSpec.None, mentionsOnly: false);
        Assert.All(all, c => Assert.False(c.MentionsMe));

        Assert.Empty(FeedService.StampMentions(feed, MentionSpec.None, mentionsOnly: true));
    }

    [Fact]
    public void StampMentions_FlagsEntryByMentionedUserId_WithNoHandleInText()
    {
        // BenSpec carries id 7 (ClickUpUser(7, "Ben")); entry "a" has no matchable @handle in its text
        // but a structured block referenced Ben's id, so the feed flags it via the #167 id path.
        var feed = new[]
        {
            new CommentItem("a", "author", 100, "please review the change", false, "task1", MentionedUserIds: new long[] { 7 }),
            Msg("b", "no mention and no id"),
        };

        var stamped = FeedService.StampMentions(feed, BenSpec, mentionsOnly: false);

        Assert.True(stamped.Single(c => c.Id == "a").MentionsMe);
        Assert.False(stamped.Single(c => c.Id == "b").MentionsMe);
    }
}
