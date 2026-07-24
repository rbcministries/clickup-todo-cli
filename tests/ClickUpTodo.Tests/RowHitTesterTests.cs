using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the mouse row hit-test helper (#286): a viewport-relative click Y plus the list's
/// scroll offset resolves the same row the keyboard cursor would land on, and non-task rows / clicks
/// outside the rows resolve to null so a double-click no-ops exactly like Enter there.
/// </summary>
public sealed class RowHitTesterTests
{
    private static TaskItem Task(string id) => new() { Id = id, Name = id };

    // A header/spacer row carries a null task, exactly like the host's _rows array.
    private static readonly IReadOnlyList<TaskItem?> Rows =
    [
        null,          // 0: header
        Task("a"),     // 1: task
        Task("b"),     // 2: task
        null,          // 3: spacer
        Task("c"),     // 4: task
    ];

    [Theory]
    [InlineData(0, 0, 5, 0)]   // top of an unscrolled list
    [InlineData(4, 0, 5, 4)]   // last row, unscrolled
    [InlineData(1, 3, 5, 4)]   // scrolled down 3: viewport row 1 → absolute row 4
    [InlineData(0, 2, 5, 2)]   // scrolled down 2: viewport row 0 → absolute row 2
    public void RowIndexAt_ResolvesAbsoluteRow(int clickY, int scrollOffset, int rowCount, int expected)
        => Assert.Equal(expected, RowHitTester.RowIndexAt(clickY, scrollOffset, rowCount));

    [Theory]
    [InlineData(-1, 0, 5)]   // above the first row
    [InlineData(5, 0, 5)]    // one past the last row (empty space beneath a short list)
    [InlineData(10, 0, 5)]   // well past the last row
    [InlineData(3, 3, 5)]    // scrolled: viewport row 3 → absolute row 6, past the end
    [InlineData(0, 0, 0)]    // empty list
    public void RowIndexAt_ReturnsMinusOneOutsideRows(int clickY, int scrollOffset, int rowCount)
        => Assert.Equal(-1, RowHitTester.RowIndexAt(clickY, scrollOffset, rowCount));

    [Fact]
    public void TaskAt_ReturnsTaskOnTaskRow()
    {
        Assert.Equal("a", RowHitTester.TaskAt(1, 0, Rows)?.Id);
        Assert.Equal("c", RowHitTester.TaskAt(4, 0, Rows)?.Id);
    }

    [Fact]
    public void TaskAt_ReturnsTaskOnTaskRow_WhenScrolled()
    {
        // Scrolled down 3 rows: viewport row 1 is absolute row 4 → task "c".
        Assert.Equal("c", RowHitTester.TaskAt(1, 3, Rows)?.Id);
    }

    [Fact]
    public void TaskAt_ReturnsNullOnHeaderOrSpacerRow()
    {
        Assert.Null(RowHitTester.TaskAt(0, 0, Rows));   // header
        Assert.Null(RowHitTester.TaskAt(3, 0, Rows));   // spacer
    }

    [Fact]
    public void TaskAt_ReturnsNullOutsideRows()
    {
        Assert.Null(RowHitTester.TaskAt(-1, 0, Rows));  // above the first row
        Assert.Null(RowHitTester.TaskAt(5, 0, Rows));   // below the last row
        Assert.Null(RowHitTester.TaskAt(0, 0, []));     // empty list
    }

    // ── Fold-arrow column hit-test (#287) ─────────────────────────────────────
    // The marker "▶ "/"▼ " sits at a known char offset; a click's X is a terminal column, so the helper
    // converts via the injected column measure (the renderer's GetColumns in the app, a fake here).

    // ASCII rows: char offset == column, so an identity measure ("s => s.Length") suffices. The marker
    // spans two columns [markerStart, markerStart+2): the arrow and its trailing space.
    private static int Ascii(string s) => s.Length;

    [Theory]
    [InlineData(6, true)]    // the ▶ arrow column itself
    [InlineData(7, true)]    // the marker's trailing space — still a toggle target (forgiving gutter)
    [InlineData(5, false)]   // one column left of the marker (the indent/badge gutter)
    [InlineData(8, false)]   // the title's first column, just right of the marker
    [InlineData(0, false)]   // far left
    public void IsWithinFoldMarker_ResolvesTheArrowColumns(int clickX, bool expected)
    {
        // "(TD)  ▶ Roll" → marker "▶ " starts at char index 6, length 2.
        const string text = "(TD)  ▶ Roll up sprint";
        Assert.Equal(expected, RowHitTester.IsWithinFoldMarker(clickX, text, markerStart: 6, markerLength: 2, Ascii));
    }

    [Fact]
    public void IsWithinFoldMarker_FalseWhenRowHasNoMarker()
    {
        // A leaf/header row reports the (-1, 0) "no marker" sentinel — no column is ever a hit.
        Assert.False(RowHitTester.IsWithinFoldMarker(0, "a leaf row", markerStart: -1, markerLength: 0, Ascii));
        Assert.False(RowHitTester.IsWithinFoldMarker(3, "a leaf row", markerStart: 3, markerLength: 0, Ascii));
    }

    [Fact]
    public void IsWithinFoldMarker_FalseWhenSpanExceedsText()
    {
        // Defensive: a span past the end of the text can't be a hit (never happens for real rows).
        Assert.False(RowHitTester.IsWithinFoldMarker(2, "ab", markerStart: 1, markerLength: 5, Ascii));
    }

    [Fact]
    public void IsWithinFoldMarker_WideRunesAheadShiftTheArrowColumn()
    {
        // A wide (2-column) rune ahead of the arrow means char offset != column: the fake measure counts
        // '#' as two columns (standing in for an emoji badge glyph). Marker "▶ " is at char 1, so its
        // column start is 2 (past the 2-column '#'), and it occupies columns [2, 4).
        int WideHash(string s) => s.Sum(c => c == '#' ? 2 : 1);
        const string text = "#▶ Task";
        Assert.True(RowHitTester.IsWithinFoldMarker(2, text, markerStart: 1, markerLength: 2, WideHash));  // arrow column
        Assert.True(RowHitTester.IsWithinFoldMarker(3, text, markerStart: 1, markerLength: 2, WideHash));  // trailing space
        Assert.False(RowHitTester.IsWithinFoldMarker(1, text, markerStart: 1, markerLength: 2, WideHash)); // inside the wide '#'
        Assert.False(RowHitTester.IsWithinFoldMarker(4, text, markerStart: 1, markerLength: 2, WideHash)); // the title 'T'
    }
}
