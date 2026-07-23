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
}
