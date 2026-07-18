using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure Task Detail tab-navigation model (issue #315): the Ctrl+→/Ctrl+← chord
/// routing, its inert-while-Dispatch-prompt-open guard, and the wraparound tab index. The
/// Terminal.Gui glue in <c>TaskDetailScreen</c> (classifying the key, calling <c>CycleTab</c>) is
/// verified by build + reasoning per the repo's TUI rule; this locks the decisions it delegates.
/// </summary>
public sealed class DetailTabNavTests
{
    [Theory]
    [InlineData(DetailTabNav.NavKey.CtrlRight, DetailTabNav.NavAction.CycleForward)]
    [InlineData(DetailTabNav.NavKey.CtrlLeft, DetailTabNav.NavAction.CycleBackward)]
    [InlineData(DetailTabNav.NavKey.Other, DetailTabNav.NavAction.None)]
    public void Route_MapsEachChordToItsAction_WhenPromptClosed(DetailTabNav.NavKey key, DetailTabNav.NavAction expected)
        => Assert.Equal(expected, DetailTabNav.Route(key, promptOpen: false));

    [Theory]
    [InlineData(DetailTabNav.NavKey.CtrlRight)]
    [InlineData(DetailTabNav.NavKey.CtrlLeft)]
    [InlineData(DetailTabNav.NavKey.Other)]
    public void Route_IsInert_WhileDispatchPromptOpen(DetailTabNav.NavKey key)
        => Assert.Equal(DetailTabNav.NavAction.None, DetailTabNav.Route(key, promptOpen: true));

    [Theory]
    [InlineData(0, 4, true, 1)]
    [InlineData(1, 4, true, 2)]
    [InlineData(3, 4, true, 0)]  // forward wraps past the last tab
    [InlineData(0, 4, false, 3)] // back wraps before the first tab
    [InlineData(2, 4, false, 1)]
    public void NextTab_WrapsBothDirections(int current, int count, bool forward, int expected)
        => Assert.Equal(expected, DetailTabNav.NextTab(current, count, forward));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NextTab_SingleTab_StaysPut(bool forward)
        => Assert.Equal(0, DetailTabNav.NextTab(0, 1, forward));

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void NextTab_NonPositiveCount_ReturnsZero(int count)
        => Assert.Equal(0, DetailTabNav.NextTab(0, count, forward: true));
}
