using ClickUpTodo.ClickUp;
using ClickUpTodo.ClickUp.Generated.Models;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the (offline) cursor de-paging that backs <see cref="ClickUpClient.GetTaskCommentsAsync"/>
/// — <see cref="ClickUpClient.NextCommentCursor"/> (cursor derivation) and
/// <see cref="ClickUpClient.DePageCommentsAsync"/> (the start/start_id walk, #130). ClickUp returns
/// comments most-recent-first, 25 per page, paginated by a start/start_id cursor; these lock in cursor
/// derivation, short-page/stuck-cursor termination, id de-dup, and the bounded worst case — all against
/// constructed responses (no HTTP), mirroring <c>ClickUpClientCommentMapTests</c>.
/// </summary>
public sealed class ClickUpClientCommentDePageTests
{
    private const int PageSize = 25; // mirrors ClickUpClient.CommentPageSize
    private const int MaxPages = 40; // mirrors ClickUpClient.MaxCommentPages

    private static Comment C(string id, string? date) => new() { Id = id, Date = date };

    private static Comment C(string id, long ms) => C(id, ms.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static CommentsResponse Page(IEnumerable<Comment> comments) => new() { Comments = comments.ToList() };

    /// <summary>A full page of <see cref="PageSize"/> comments with a shared id prefix and strictly
    /// descending (newest-first) dates, so each page is fresh and forces another round.</summary>
    private static CommentsResponse FullFreshPage(string prefix, long startDate)
        => Page(Enumerable.Range(0, PageSize).Select(i => C($"{prefix}-{i}", startDate - i)));

    // ── NextCommentCursor ──────────────────────────────────────────────────

    [Fact]
    public void NextCommentCursor_UsesOldestComment_TheLastInNewestFirstOrder()
    {
        var page = new List<Comment> { C("newest", 3000), C("mid", 2000), C("oldest", 1000) };

        var cursor = ClickUpClient.NextCommentCursor(page);

        Assert.Equal(new ClickUpClient.CommentCursor(1000, "oldest"), cursor);
    }

    [Fact]
    public void NextCommentCursor_SkipsTrailingBlankIdOrUnparseableDate()
    {
        // Scanning from the end: unparseable date, then blank id, then the first usable comment.
        var page = new List<Comment> { C("a", 900), C("b", 800), C(id: "", date: "700"), C("c", "not-a-number") };

        var cursor = ClickUpClient.NextCommentCursor(page);

        Assert.Equal(new ClickUpClient.CommentCursor(800, "b"), cursor);
    }

    [Fact]
    public void NextCommentCursor_EmptyOrAllUnusable_ReturnsNull()
    {
        Assert.Null(ClickUpClient.NextCommentCursor([]));
        Assert.Null(ClickUpClient.NextCommentCursor([C(id: "", date: "100"), C("x", "nope")]));
    }

    // ── DePageCommentsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DePage_SingleShortPage_FetchesOnceAndReturnsAll()
    {
        var calls = 0;
        var result = await ClickUpClient.DePageCommentsAsync((cursor, _) =>
        {
            calls++;
            Assert.Null(cursor); // first (and only) fetch has no cursor
            return Task.FromResult<CommentsResponse?>(Page([C("c1", 300), C("c2", 200), C("c3", 100)]));
        }, onCapReached: null, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(["c1", "c2", "c3"], result.Select(c => c.Id));
    }

    [Fact]
    public async Task DePage_WalksCursorThenStopsOnShortPage()
    {
        var cursors = new List<ClickUpClient.CommentCursor?>();
        var result = await ClickUpClient.DePageCommentsAsync((cursor, _) =>
        {
            cursors.Add(cursor);
            // Page 0: a full page (forces a second fetch). Page 1: a short page (terminates).
            return Task.FromResult<CommentsResponse?>(cursor is null
                ? FullFreshPage("p0", startDate: 1000)
                : Page([C("tail-0", 500), C("tail-1", 490)]));
        }, onCapReached: null, CancellationToken.None);

        // Two fetches: the second carried the cursor derived from page 0's oldest comment ("p0-24", 976).
        Assert.Equal(2, cursors.Count);
        Assert.Null(cursors[0]);
        Assert.Equal(new ClickUpClient.CommentCursor(1000 - (PageSize - 1), "p0-24"), cursors[1]);
        Assert.Equal(PageSize + 2, result.Count);
    }

    [Fact]
    public async Task DePage_DeDupesBoundaryAnchorAcrossPages()
    {
        var result = await ClickUpClient.DePageCommentsAsync((cursor, _) =>
        {
            if (cursor is null)
                return Task.FromResult<CommentsResponse?>(FullFreshPage("x", startDate: 1000)); // x-0..x-24
            // Next (short) page re-returns the boundary anchor x-24, then a few genuinely older ones.
            return Task.FromResult<CommentsResponse?>(Page([C("x-24", 976), C("y-0", 500), C("y-1", 490)]));
        }, onCapReached: null, CancellationToken.None);

        Assert.Equal(PageSize + 2, result.Count);                 // the re-returned anchor is not double-counted
        Assert.Equal(result.Count, result.Select(c => c.Id).Distinct().Count());
        Assert.Single(result, c => c.Id == "x-24");
    }

    [Fact]
    public async Task DePage_FullPageOfAllSeenIds_TerminatesInsteadOfLooping()
    {
        var calls = 0;
        var result = await ClickUpClient.DePageCommentsAsync((cursor, _) =>
        {
            calls++;
            return Task.FromResult<CommentsResponse?>(FullFreshPage("same", startDate: 1000)); // identical every call
        }, onCapReached: null, CancellationToken.None);

        // Page 0 is all-fresh; page 1 is the same ids (a stuck cursor) → freshCount 0 → stop. No cap needed.
        Assert.Equal(2, calls);
        Assert.Equal(PageSize, result.Count);
    }

    [Fact]
    public async Task DePage_StopsAtCap_AndReportsTruncation()
    {
        var calls = 0;
        int? reportedAtCap = null;
        var result = await ClickUpClient.DePageCommentsAsync((cursor, _) =>
        {
            calls++;
            // Every page is full and entirely fresh, so only the cap can stop the walk.
            return Task.FromResult<CommentsResponse?>(FullFreshPage($"pg{calls}", startDate: 1_000_000 - calls * 1000));
        }, onCapReached: count => reportedAtCap = count, CancellationToken.None);

        Assert.Equal(MaxPages, calls);
        Assert.Equal(MaxPages * PageSize, result.Count);
        Assert.Equal(result.Count, reportedAtCap); // truncation surfaced, not silent
    }

    [Fact]
    public async Task DePage_ObservesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ClickUpClient.DePageCommentsAsync(
                (_, _) => Task.FromResult<CommentsResponse?>(FullFreshPage("p", 1000)),
                onCapReached: null,
                cts.Token));
    }

    [Fact]
    public async Task DePage_NullResponse_TreatedAsEmptyAndStops()
    {
        var calls = 0;
        var result = await ClickUpClient.DePageCommentsAsync((cursor, _) =>
        {
            calls++;
            return Task.FromResult<CommentsResponse?>(null);
        }, onCapReached: null, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Empty(result);
    }
}
