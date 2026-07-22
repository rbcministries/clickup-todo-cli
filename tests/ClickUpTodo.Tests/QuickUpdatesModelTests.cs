using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

public sealed class QuickUpdatesModelTests
{
    private static StatusOption Status(string name) => new(name, "#fff");

    [Theory]
    [InlineData(QuickUpdatesPane.Status, QuickUpdatesPane.Priority)]
    [InlineData(QuickUpdatesPane.Priority, QuickUpdatesPane.Assignees)]
    [InlineData(QuickUpdatesPane.Assignees, QuickUpdatesPane.Status)] // wraps
    public void Cycle_Forward_AdvancesStatusPriorityAssigneesAndWraps(
        QuickUpdatesPane current, QuickUpdatesPane expected)
        => Assert.Equal(expected, QuickUpdatesModel.Cycle(current, forward: true));

    [Theory]
    [InlineData(QuickUpdatesPane.Assignees, QuickUpdatesPane.Priority)]
    [InlineData(QuickUpdatesPane.Priority, QuickUpdatesPane.Status)]
    [InlineData(QuickUpdatesPane.Status, QuickUpdatesPane.Assignees)] // wraps
    public void Cycle_Backward_RetreatsAndWraps(
        QuickUpdatesPane current, QuickUpdatesPane expected)
        => Assert.Equal(expected, QuickUpdatesModel.Cycle(current, forward: false));

    [Fact]
    public void PaneCount_MatchesTheEnum()
        => Assert.Equal(QuickUpdatesModel.PaneCount, Enum.GetValues<QuickUpdatesPane>().Length);

    // ── Mark (the shared ✓ prefix) ───────────────────────────────────────────────

    [Fact]
    public void Mark_Current_PrefixesACheckAndAligns()
    {
        Assert.Equal("✓ Urgent", QuickUpdatesModel.Mark("Urgent", current: true));
        Assert.Equal("  Urgent", QuickUpdatesModel.Mark("Urgent", current: false));
        // Both prefixes are the same width so labels stay left-aligned under the mark.
        Assert.Equal(QuickUpdatesModel.CurrentMarker.Length, QuickUpdatesModel.NoMarker.Length);
    }

    // ── Status rows ──────────────────────────────────────────────────────────────

    [Fact]
    public void StatusRows_MarkOnlyTheEffectiveStatus_CaseInsensitive()
    {
        var rows = QuickUpdatesModel.StatusRows(
            [Status("open"), Status("in progress"), Status("done")], effectiveStatus: "IN PROGRESS");

        Assert.Equal(["  open", "✓ in progress", "  done"], rows);
    }

    [Fact]
    public void StatusRows_MarkNothing_WhenCurrentNotInWorkflow()
    {
        var rows = QuickUpdatesModel.StatusRows([Status("open"), Status("done")], effectiveStatus: "archived");

        Assert.Equal(["  open", "  done"], rows);
    }

    [Fact]
    public void StatusRows_MarkNothing_WhenNoCurrentStatus()
    {
        var rows = QuickUpdatesModel.StatusRows([Status("open"), Status("done")], effectiveStatus: null);

        Assert.Equal(["  open", "  done"], rows);
    }

    // ── Priority rows ────────────────────────────────────────────────────────────

    [Fact]
    public void PriorityLabels_AreTheFourPrioritiesThenTheClearRow()
        => Assert.Equal(
            ["Urgent", "High", "Normal", "Low", QuickUpdatesModel.NoPriorityLabel],
            QuickUpdatesModel.PriorityLabels);

    [Fact]
    public void NoPriorityRow_IsTheLastRow()
        => Assert.Equal(4, QuickUpdatesModel.NoPriorityRow);

    [Theory]
    [InlineData(0, 1)] // Urgent
    [InlineData(1, 2)] // High
    [InlineData(2, 3)] // Normal
    [InlineData(3, 4)] // Low
    public void PriorityLevelForRow_MapsThePriorityRowsToLevels(int index, int? expected)
        => Assert.Equal(expected, QuickUpdatesModel.PriorityLevelForRow(index));

    [Theory]
    [InlineData(4)]  // the "(no priority)" clear row
    [InlineData(-1)] // out of range
    [InlineData(9)]
    public void PriorityLevelForRow_ClearRowAndOutOfRange_AreNull(int index)
        => Assert.Null(QuickUpdatesModel.PriorityLevelForRow(index));

    [Theory]
    [InlineData(1, 0)] // Urgent
    [InlineData(2, 1)] // High
    [InlineData(3, 2)] // Normal
    [InlineData(4, 3)] // Low
    public void PriorityRowForLevel_MapsLevelToRow(int level, int expected)
        => Assert.Equal(expected, QuickUpdatesModel.PriorityRowForLevel(level));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5)]
    public void PriorityRowForLevel_UnsetOrOutOfRange_SelectsTheClearRow(int? level)
        => Assert.Equal(QuickUpdatesModel.NoPriorityRow, QuickUpdatesModel.PriorityRowForLevel(level));

    [Theory]
    [InlineData(1, "✓ Urgent")]
    [InlineData(2, "✓ High")]
    [InlineData(3, "✓ Normal")]
    [InlineData(4, "✓ Low")]
    public void PriorityRows_MarkTheEffectiveLevel(int level, string markedRow)
    {
        var rows = QuickUpdatesModel.PriorityRows(level);

        Assert.Equal(markedRow, rows[QuickUpdatesModel.PriorityRowForLevel(level)]);
        Assert.Single(rows, r => r.StartsWith(QuickUpdatesModel.CurrentMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void PriorityRows_MarkTheClearRow_WhenNoPriority()
    {
        var rows = QuickUpdatesModel.PriorityRows(null);

        Assert.Equal(
            ["  Urgent", "  High", "  Normal", "  Low", "✓ " + QuickUpdatesModel.NoPriorityLabel],
            rows);
    }

    // ── RowIndexAt (mouse click → pane row, #288) ────────────────────────────────

    [Theory]
    [InlineData(0, 0, 5, 0)]   // top of an unscrolled list
    [InlineData(4, 0, 5, 4)]   // last row, unscrolled
    [InlineData(1, 3, 8, 4)]   // scrolled down 3: viewport row 1 → absolute row 4
    [InlineData(0, 2, 8, 2)]   // scrolled down 2: viewport row 0 → absolute row 2
    public void RowIndexAt_ResolvesAbsoluteRow(int clickY, int scrollOffset, int rowCount, int expected)
        => Assert.Equal(expected, QuickUpdatesModel.RowIndexAt(clickY, scrollOffset, rowCount));

    [Theory]
    [InlineData(-1, 0, 5)]   // above the first row
    [InlineData(5, 0, 5)]    // one past the last row (empty space beneath a short list)
    [InlineData(10, 0, 5)]   // well past the last row
    [InlineData(3, 3, 5)]    // scrolled: viewport row 3 → absolute row 6, past the end
    [InlineData(0, 0, 0)]    // empty list
    public void RowIndexAt_ReturnsMinusOneOutsideRows(int clickY, int scrollOffset, int rowCount)
        => Assert.Equal(-1, QuickUpdatesModel.RowIndexAt(clickY, scrollOffset, rowCount));
}
