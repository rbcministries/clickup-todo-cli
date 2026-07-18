using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Maps a mouse click in the main task list to the row it landed on, so mouse gestures (#283) resolve
/// the same rows the keyboard cursor navigates. Pure (no Terminal.Gui, no host state) so the mapping is
/// unit-testable: the host supplies the <c>ListView</c>'s scroll offset (<c>Viewport.Y</c>) and its
/// row-parallel arrays. Reused by double-click-to-open (A, #286), fold-arrow click (B, #287), and the
/// Task Tree tab (F, #291), so it stays action-agnostic — it resolves a row, callers decide what to do.
/// </summary>
public static class RowHitTester
{
    /// <summary>
    /// The absolute row index a viewport-relative click at <paramref name="clickY"/> lands on, given the
    /// list's current vertical <paramref name="scrollOffset"/> (the index of its topmost displayed row)
    /// and total <paramref name="rowCount"/>. Returns <c>-1</c> when the click is above the first row
    /// (<paramref name="clickY"/> &lt; 0) or below the last — i.e. the empty space beneath a list shorter
    /// than the viewport.
    /// </summary>
    public static int RowIndexAt(int clickY, int scrollOffset, int rowCount)
    {
        if (clickY < 0)
            return -1;
        var index = scrollOffset + clickY;
        return index >= 0 && index < rowCount ? index : -1;
    }

    /// <summary>
    /// The task on the row a click landed on, or <c>null</c> for a header/spacer row (a null entry in
    /// <paramref name="rows"/>) or a click outside the rows. Mirrors the host's <c>CurrentTask()</c>, so
    /// a double-click on a non-task row no-ops exactly like Enter does there.
    /// </summary>
    public static TaskItem? TaskAt(int clickY, int scrollOffset, IReadOnlyList<TaskItem?> rows)
    {
        var index = RowIndexAt(clickY, scrollOffset, rows.Count);
        return index >= 0 ? rows[index] : null;
    }
}
