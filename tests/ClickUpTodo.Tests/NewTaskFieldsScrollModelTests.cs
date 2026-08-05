using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure New Task Custom-fields scroll arithmetic (#446): clamping the viewport top
/// to the content bounds and computing the minimal move to reveal a focused widget. The Terminal.Gui
/// glue in <c>NewTaskScreen</c> (setting <c>View.Viewport</c> from a focused widget's <c>Frame</c>) is
/// verified via <c>tui-validate</c> per the repo's TUI rule; this locks the arithmetic it delegates.
/// </summary>
public sealed class NewTaskFieldsScrollModelTests
{
    [Theory]
    // Content fits the viewport (or is shorter) → the only valid top is 0.
    [InlineData(0, 10, 20, 0)]
    [InlineData(5, 10, 20, 0)]
    [InlineData(10, 10, 10, 0)]
    // Content taller than the viewport → clamp into [0, contentHeight - viewportHeight].
    [InlineData(-3, 40, 10, 0)]
    [InlineData(5, 40, 10, 5)]
    [InlineData(30, 40, 10, 30)]   // exactly the max
    [InlineData(100, 40, 10, 30)]  // past the max → clamped to the max
    public void ClampTop_ClampsToContentBounds(int desiredTop, int contentHeight, int viewportHeight, int expected)
        => Assert.Equal(expected, NewTaskFieldsScrollModel.ClampTop(desiredTop, contentHeight, viewportHeight));

    [Fact]
    public void ScrollToShow_ItemAlreadyFullyVisible_DoesNotMove()
    {
        // Viewport rows [10,20); item at [12,15) is inside it → no move.
        Assert.Equal(10, NewTaskFieldsScrollModel.ScrollToShow(currentTop: 10, itemTop: 12, itemHeight: 3, viewportHeight: 10, contentHeight: 100));
    }

    [Fact]
    public void ScrollToShow_ItemAboveViewport_ScrollsUpToItemTop()
    {
        // Item at [3,4) sits above viewport [10,20) → top becomes the item's own top.
        Assert.Equal(3, NewTaskFieldsScrollModel.ScrollToShow(currentTop: 10, itemTop: 3, itemHeight: 1, viewportHeight: 10, contentHeight: 100));
    }

    [Fact]
    public void ScrollToShow_ItemBelowViewport_ScrollsDownJustEnoughToRevealItsBottom()
    {
        // Viewport [0,10); a 1-row item at row 24 → new top 24+1-10 = 15 so row 24 is the last visible row.
        Assert.Equal(15, NewTaskFieldsScrollModel.ScrollToShow(currentTop: 0, itemTop: 24, itemHeight: 1, viewportHeight: 10, contentHeight: 100));
    }

    [Fact]
    public void ScrollToShow_MultiRowItemBelowViewport_RevealsWholeItem()
    {
        // A 6-row drop-down at [20,26); viewport height 10 → new top 26-10 = 16 shows all six rows.
        Assert.Equal(16, NewTaskFieldsScrollModel.ScrollToShow(currentTop: 0, itemTop: 20, itemHeight: 6, viewportHeight: 10, contentHeight: 100));
    }

    [Fact]
    public void ScrollToShow_ItemTallerThanViewport_AlignsToItemTop()
    {
        // A 15-row labels field taller than a 10-row viewport → align to its top (show label + first rows)
        // rather than its bottom, which would scroll the label off screen.
        Assert.Equal(20, NewTaskFieldsScrollModel.ScrollToShow(currentTop: 0, itemTop: 20, itemHeight: 15, viewportHeight: 10, contentHeight: 100));
    }

    [Fact]
    public void ScrollToShow_ResultIsClampedToContentBounds()
    {
        // Revealing the last item's bottom would exceed the max top; clamp keeps it in-bounds.
        // Content 40, viewport 10 → max top 30. Item at [38,40): 40-10 = 30, already the max.
        Assert.Equal(30, NewTaskFieldsScrollModel.ScrollToShow(currentTop: 0, itemTop: 38, itemHeight: 2, viewportHeight: 10, contentHeight: 40));
    }

    [Fact]
    public void ScrollToShow_DegenerateViewportHeight_DoesNotThrowAndStaysInBounds()
    {
        // A zero/one-row viewport is treated as at least one row; the result is still clamped.
        var top = NewTaskFieldsScrollModel.ScrollToShow(currentTop: 0, itemTop: 5, itemHeight: 1, viewportHeight: 0, contentHeight: 20);
        Assert.InRange(top, 0, 20);
    }
}
