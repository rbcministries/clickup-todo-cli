namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure scroll arithmetic for the New Task screen's Custom fields page (#446), factored out of the
/// Terminal.Gui glue so it's unit-testable without a terminal — the same pure-glue split as
/// <see cref="DetailScrollModel"/>. The page stacks one input widget per fillable field top-down; on a
/// short terminal the stack can be taller than the page's viewport, so the glue turns the page into a
/// scroll viewport over that taller content and asks this model where the viewport's top row should sit.
/// <para>
/// All values are content-row coordinates: <c>top</c> is the first content row shown at the top of the
/// viewport, <c>contentHeight</c> is the full stacked height, and <c>viewportHeight</c> is the visible
/// height. The model owns only the clamping and minimal-move logic; the glue reads/writes
/// <c>View.Viewport</c> and the focused widget's <c>Frame</c>.
/// </para>
/// </summary>
public static class NewTaskFieldsScrollModel
{
    /// <summary>
    /// Clamp a desired viewport top to the valid range <c>[0, max(0, contentHeight - viewportHeight)]</c>
    /// — never negative, and never so far down that blank space past the content would show. When the
    /// content fits (<c>contentHeight &lt;= viewportHeight</c>) the only valid top is <c>0</c>.
    /// </summary>
    public static int ClampTop(int desiredTop, int contentHeight, int viewportHeight)
    {
        var max = Math.Max(0, contentHeight - Math.Max(0, viewportHeight));
        return Math.Clamp(desiredTop, 0, max);
    }

    /// <summary>
    /// The minimal new viewport top so the item occupying content rows
    /// <c>[itemTop, itemTop + itemHeight)</c> is fully visible in a viewport of height
    /// <paramref name="viewportHeight"/> currently starting at <paramref name="currentTop"/>:
    /// <list type="bullet">
    /// <item>item above the viewport → scroll up so its top row is the viewport's top;</item>
    /// <item>item below the viewport → scroll down just enough to reveal its bottom row;</item>
    /// <item>an item taller than the viewport → align to its top (so its first row/label shows) rather
    /// than its bottom;</item>
    /// <item>already fully visible → no move.</item>
    /// </list>
    /// The result is clamped to the content bounds via <see cref="ClampTop"/>.
    /// </summary>
    public static int ScrollToShow(int currentTop, int itemTop, int itemHeight, int viewportHeight, int contentHeight)
    {
        var height = Math.Max(1, itemHeight);
        var viewport = Math.Max(1, viewportHeight);

        int desiredTop;
        if (itemTop < currentTop)
            desiredTop = itemTop;
        else if (itemTop + height > currentTop + viewport)
            // Reveal the bottom of the item; if it's taller than the viewport, prefer its top so the
            // label/first row stays on screen instead of scrolling past it.
            desiredTop = height >= viewport ? itemTop : itemTop + height - viewport;
        else
            desiredTop = currentTop;

        return ClampTop(desiredTop, contentHeight, viewportHeight);
    }
}
