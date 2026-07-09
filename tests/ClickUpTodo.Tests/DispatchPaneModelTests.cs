using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure Dispatch-pane navigation/routing model (issue #93): key→action routing,
/// wraparound focus cycling, and pane sizing. The Terminal.Gui glue in <c>TaskDetailScreen</c> is
/// verified by build + reasoning per the repo's TUI rule; this locks the decisions it delegates.
/// </summary>
public sealed class DispatchPaneModelTests
{
    [Theory]
    [InlineData(DispatchPaneModel.PaneKey.Enter, DispatchPaneModel.PaneAction.Submit)]
    [InlineData(DispatchPaneModel.PaneKey.Escape, DispatchPaneModel.PaneAction.Cancel)]
    [InlineData(DispatchPaneModel.PaneKey.Tab, DispatchPaneModel.PaneAction.FocusNext)]
    [InlineData(DispatchPaneModel.PaneKey.BackTab, DispatchPaneModel.PaneAction.FocusPrevious)]
    [InlineData(DispatchPaneModel.PaneKey.PageUp, DispatchPaneModel.PaneAction.ScrollUnderlyingPageUp)]
    [InlineData(DispatchPaneModel.PaneKey.PageDown, DispatchPaneModel.PaneAction.ScrollUnderlyingPageDown)]
    [InlineData(DispatchPaneModel.PaneKey.Other, DispatchPaneModel.PaneAction.PassThrough)]
    public void Route_MapsEachKeyToItsAction(DispatchPaneModel.PaneKey key, DispatchPaneModel.PaneAction expected)
        => Assert.Equal(expected, DispatchPaneModel.Route(key));

    [Theory]
    [InlineData(0, 4, true, 1)]
    [InlineData(1, 4, true, 2)]
    [InlineData(3, 4, true, 0)] // forward wraps past the last control
    [InlineData(0, 4, false, 3)] // back wraps before the first control
    [InlineData(2, 4, false, 1)]
    public void NextFocus_WrapsBothDirections(int current, int count, bool forward, int expected)
        => Assert.Equal(expected, DispatchPaneModel.NextFocus(current, count, forward));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NextFocus_SingleControl_StaysPut(bool forward)
        => Assert.Equal(0, DispatchPaneModel.NextFocus(0, 1, forward));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NextFocus_NonPositiveCount_ReturnsZero(int count)
        => Assert.Equal(0, DispatchPaneModel.NextFocus(0, count, forward: true));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 6)]
    [InlineData(-1, 2)] // clamped to zero controls → just the border
    public void PreferredHeight_IsControlsPlusBorder(int controlCount, int expected)
        => Assert.Equal(expected, DispatchPaneModel.PreferredHeight(controlCount));

    [Fact]
    public void ClampHeight_ReturnsPreferred_WhenTerminalHasRoom()
        => Assert.Equal(6, DispatchPaneModel.ClampHeight(preferred: 6, availableHeight: 24, minTabRows: 3));

    [Fact]
    public void ClampHeight_CapsHeight_ToKeepTabRowsVisible()
        // 10 rows available, keep 5 for the tab → pane may be at most 5.
        => Assert.Equal(5, DispatchPaneModel.ClampHeight(preferred: 6, availableHeight: 10, minTabRows: 5));

    [Fact]
    public void ClampHeight_NeverBelowPromptMinimum_OnTinyTerminals()
        // Even when there's no room to spare, the prompt row + borders (3) survive.
        => Assert.Equal(3, DispatchPaneModel.ClampHeight(preferred: 6, availableHeight: 4, minTabRows: 5));

    [Fact]
    public void ClampHeight_DoesNotGrowBeyondPreferred_WhenPreferredIsSmall()
        => Assert.Equal(3, DispatchPaneModel.ClampHeight(preferred: 3, availableHeight: 40, minTabRows: 3));

    [Theory]
    [InlineData(4, 5, 1, 12)] // the #95 layout: 4 rows above + 5 browser rows + 1 below + 2 border
    [InlineData(0, 1, 0, 3)]  // minimum: a single browser row + border
    [InlineData(2, 0, 1, 6)]  // browser rows floored to 1 even when asked for 0: 2+1+1+2
    [InlineData(-1, -1, -1, 3)] // negatives floored (0 above, 1 browser, 0 below, +2 border)
    public void PreferredHeightWithBrowser_SumsRowsPlusBorder(
        int above, int browser, int below, int expected)
        => Assert.Equal(expected, DispatchPaneModel.PreferredHeightWithBrowser(above, browser, below));
}
