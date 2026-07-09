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
}
