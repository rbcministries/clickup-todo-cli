using ClickUpTodo.Configuration;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure Stream auto-scroll edge-resolution model (issue #107): given the
/// <see cref="StreamAutoScroll"/> preference and the current <see cref="StreamSort"/> direction, which
/// viewport edge to land on. The Terminal.Gui glue in <c>TaskDetailScreen</c> (the actual
/// <c>MoveEnd()</c>/<c>MoveHome()</c> call) is verified via <c>tui-validate</c> per the repo's TUI
/// rule; this locks the decision it delegates so "newest/oldest" stays correct across both sorts.
/// </summary>
public sealed class DetailScrollModelTests
{
    [Theory]
    // Newest lands at the bottom when the stream is oldest-first, at the top when newest-first.
    [InlineData(StreamAutoScroll.Newest, StreamSort.Ascending, DetailScrollModel.Edge.Bottom)]
    [InlineData(StreamAutoScroll.Newest, StreamSort.Descending, DetailScrollModel.Edge.Top)]
    // Oldest is the mirror image: top when oldest-first, bottom when newest-first.
    [InlineData(StreamAutoScroll.Oldest, StreamSort.Ascending, DetailScrollModel.Edge.Top)]
    [InlineData(StreamAutoScroll.Oldest, StreamSort.Descending, DetailScrollModel.Edge.Bottom)]
    public void ResolveEdge_MapsPreferenceAndSortToEdge(
        StreamAutoScroll preference, StreamSort sort, DetailScrollModel.Edge expected)
        => Assert.Equal(expected, DetailScrollModel.ResolveEdge(preference, sort));

    [Theory]
    [InlineData(StreamSort.Ascending)]
    [InlineData(StreamSort.Descending)]
    public void ResolveEdge_NewestAndOldest_AreOppositeEdges_ForAGivenSort(StreamSort sort)
        => Assert.NotEqual(
            DetailScrollModel.ResolveEdge(StreamAutoScroll.Newest, sort),
            DetailScrollModel.ResolveEdge(StreamAutoScroll.Oldest, sort));

    [Theory]
    [InlineData(StreamAutoScroll.Newest)]
    [InlineData(StreamAutoScroll.Oldest)]
    public void ResolveEdge_TogglingSort_FlipsTheEdge_ForAGivenPreference(StreamAutoScroll preference)
        => Assert.NotEqual(
            DetailScrollModel.ResolveEdge(preference, StreamSort.Ascending),
            DetailScrollModel.ResolveEdge(preference, StreamSort.Descending));

    // ── Bare ↑/↓ line-scroll / row-move arithmetic (#452) ────────────────────────────────────────

    [Theory]
    [InlineData(100, 10, 90)]   // content taller than the viewport: max top leaves one screenful visible
    [InlineData(10, 10, 0)]     // content exactly fills the viewport: nothing to scroll
    [InlineData(5, 10, 0)]      // content shorter than the viewport: nothing to scroll
    [InlineData(20, 0, 19)]     // degenerate height clamps to 1, so max top is lineCount − 1
    [InlineData(0, 10, 0)]      // empty content
    public void MaxTop_LeavesOneScreenfulOrZero(int lineCount, int viewportHeight, int expected)
        => Assert.Equal(expected, DetailScrollModel.MaxTop(lineCount, viewportHeight));

    [Theory]
    [InlineData(5, 10, 100, 1, 6)]    // ↓ from the middle advances one line
    [InlineData(5, 10, 100, -1, 4)]   // ↑ from the middle retreats one line
    [InlineData(0, 10, 100, -1, 0)]   // ↑ at the top is a no-op (saturates, does not go negative)
    [InlineData(90, 10, 100, 1, 90)]  // ↓ at the bottom (max top) is a no-op
    [InlineData(89, 10, 100, 1, 90)]  // ↓ one short of the bottom lands exactly on max top
    [InlineData(3, 10, 5, 1, 0)]      // content shorter than the viewport pins the top to 0
    public void NextTop_MovesOneLineAndSaturatesAtEdges(
        int currentTop, int viewportHeight, int lineCount, int delta, int expected)
        => Assert.Equal(expected, DetailScrollModel.NextTop(currentTop, viewportHeight, lineCount, delta));

    [Theory]
    [InlineData(2, 5, 1, 3)]    // ↓ from the middle selects the next row
    [InlineData(2, 5, -1, 1)]   // ↑ from the middle selects the previous row
    [InlineData(0, 5, -1, 0)]   // ↑ at the first row is a no-op
    [InlineData(4, 5, 1, 4)]    // ↓ at the last row is a no-op
    [InlineData(0, 1, 1, 0)]    // a single-row list never moves
    [InlineData(0, 0, 1, 0)]    // an empty list never moves
    [InlineData(3, 0, -1, 3)]   // an empty list keeps the (stale) index rather than going negative
    public void NextIndex_MovesOneRowAndSaturatesAtEnds(
        int currentIndex, int count, int delta, int expected)
        => Assert.Equal(expected, DetailScrollModel.NextIndex(currentIndex, count, delta));
}
