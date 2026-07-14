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

    // ── TaskItemFromDetail (#159): seed Quick Updates from a detail when the task isn't in the list ──

    [Fact]
    public void TaskItemFromDetail_MapsIdentityStatusAndListAndDerivesPriorityLevel()
    {
        var detail = new TaskDetail
        {
            Id = "abc",
            CustomId = "ENG-7",
            Name = "Fix the thing",
            Url = "https://app.clickup.com/t/abc",
            StatusName = "in progress",
            StatusColor = "#00ff00",
            ListId = "list-1",
            ListName = "Sprint",
            Priority = "high", // ClickUp returns the raw priority lowercase
            PriorityColor = "#ffcc00",
            DueDateMs = 111,
            CreatedMs = 222,
            UpdatedMs = 333,
            Assignees = ["Ada", "Grace"],
        };

        var item = QuickUpdatesModel.TaskItemFromDetail(detail);

        Assert.Equal("abc", item.Id);
        Assert.Equal("ENG-7", item.CustomId);
        Assert.Equal("Fix the thing", item.Name);
        Assert.Equal("https://app.clickup.com/t/abc", item.Url);
        Assert.Equal("in progress", item.StatusName);
        Assert.Equal("#00ff00", item.StatusColor);
        Assert.Equal("list-1", item.ListId);
        Assert.Equal("Sprint", item.ListName);
        Assert.Equal(2, item.PriorityLevel); // "high" → level 2
        Assert.Equal("High", item.PriorityName); // derived canonical name, not the raw lowercase input
        Assert.Equal("#ffcc00", item.PriorityColor);
        Assert.Equal(111, item.DueDateMs);
        Assert.Equal(222, item.CreatedMs);
        Assert.Equal(333, item.UpdatedMs);
    }

    [Fact]
    public void TaskItemFromDetail_CarriesAssigneeNames_WithPlaceholderIds()
    {
        var detail = new TaskDetail { Id = "x", Name = "T", Assignees = ["Ada", "Grace"] };

        var item = QuickUpdatesModel.TaskItemFromDetail(detail);

        // The detail exposes names only, so ids are a placeholder (0) — names still render in the pane.
        Assert.Equal(["Ada", "Grace"], item.Assignees.Select(a => a.Name));
        Assert.All(item.Assignees, a => Assert.Equal(0, a.Id));
    }

    [Fact]
    public void TaskItemFromDetail_NoPriority_IsNullLevel_AndNoAssignees_IsEmpty()
    {
        var detail = new TaskDetail { Id = "x", Name = "T", Priority = null };

        var item = QuickUpdatesModel.TaskItemFromDetail(detail);

        Assert.Null(item.PriorityLevel);
        Assert.Null(item.PriorityName);
        Assert.Empty(item.Assignees);
    }
}
