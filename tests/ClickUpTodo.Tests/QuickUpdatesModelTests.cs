using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

public sealed class QuickUpdatesModelTests
{
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

    [Fact]
    public void FormatPriority_IndentsTheName()
        => Assert.Equal("  Urgent", QuickUpdatesModel.FormatPriority("Urgent"));

    [Fact]
    public void PriorityRows_AreTheCanonicalOrderUrgentToLow()
        => Assert.Equal(
            ["  Urgent", "  High", "  Normal", "  Low"],
            QuickUpdatesModel.PriorityRows());

    [Theory]
    [InlineData(1, 0)] // Urgent
    [InlineData(2, 1)] // High
    [InlineData(3, 2)] // Normal
    [InlineData(4, 3)] // Low
    public void PreselectedPriorityIndex_MapsLevelToRow(int level, int expected)
        => Assert.Equal(expected, QuickUpdatesModel.PreselectedPriorityIndex(level));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5)]
    public void PreselectedPriorityIndex_ReturnsMinusOne_WhenUnsetOrOutOfRange(int? level)
        => Assert.Equal(-1, QuickUpdatesModel.PreselectedPriorityIndex(level));

    [Fact]
    public void AssigneeRows_ListsCurrentAssignees()
    {
        var rows = QuickUpdatesModel.AssigneeRows(
            [new TaskAssignee(1, "Ada"), new TaskAssignee(2, "Grace")]);

        Assert.Equal(["  Ada", "  Grace"], rows);
    }

    [Fact]
    public void AssigneeRows_ShowsPlaceholder_WhenNoAssignees()
        => Assert.Equal(["  (no assignees)"], QuickUpdatesModel.AssigneeRows([]));
}
