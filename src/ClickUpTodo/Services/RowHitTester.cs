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

    /// <summary>
    /// Whether a click at column <paramref name="clickX"/> lands within a row's fold marker (the
    /// <c>▶</c>/<c>▼</c> arrow, #287) — the mouse target that toggles a parent's subtasks. The marker's
    /// char span (<paramref name="markerStart"/>/<paramref name="markerLength"/>) comes from
    /// <see cref="ClickUpTodo.Tui.TaskRowFormatter.Row"/>; because a click's X is a terminal <em>column</em> while
    /// those are <em>char</em> offsets, the char prefix and the marker are converted to columns via
    /// <paramref name="columnWidth"/> — the caller passes the same grapheme/column-aware measure the
    /// renderer uses (<c>StringExtensions.GetColumns</c>), so wide/emoji runes ahead of the arrow don't
    /// skew the target. Pure (the measure is injected), mirroring <see cref="ClickUpTodo.Tui.Screens.HelpLine.HitTest"/>.
    /// Returns <c>false</c> for a row with no marker (<paramref name="markerLength"/> &lt;= 0) or a click
    /// left of / right of the arrow. The caller still gates on the row's <c>FoldState</c> so only a
    /// genuinely foldable parent toggles.
    /// </summary>
    public static bool IsWithinFoldMarker(
        int clickX, string rowText, int markerStart, int markerLength, Func<string, int> columnWidth)
    {
        if (markerLength <= 0 || markerStart < 0 || markerStart + markerLength > rowText.Length)
            return false;
        var startCol = columnWidth(rowText[..markerStart]);
        var endCol = startCol + columnWidth(rowText.Substring(markerStart, markerLength));
        return clickX >= startCol && clickX < endCol;
    }
}
