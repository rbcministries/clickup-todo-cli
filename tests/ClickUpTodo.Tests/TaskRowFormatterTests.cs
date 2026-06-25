using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

public sealed class TaskRowFormatterTests
{
    [Fact]
    public void Format_TitleLeads_AndIncludesStatusListAndDue()
    {
        var task = new TaskItem
        {
            Id = "1",
            Name = "Ship the report",
            StatusName = "in progress",
            ListName = "Personal Tasks",
            DueDateMs = DateTimeOffset.Parse("2026-07-01T12:00:00Z").ToUnixTimeMilliseconds(),
        };

        var row = TaskRowFormatter.Format(task);

        Assert.StartsWith("Ship the report", row.Text);
        Assert.Contains("[in progress]", row.Text);
        Assert.Contains("· Personal Tasks", row.Text);
        Assert.Contains("· due ", row.Text);
    }

    [Fact]
    public void Format_BadgeSpan_ExactlyCoversTheStatusBracket()
    {
        var task = new TaskItem { Id = "1", Name = "Reply to vendor [urgent]", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task);

        // The span must land on the status badge, NOT the literal "[urgent]" inside the title.
        Assert.True(row.BadgeLength > 0);
        var span = row.Text.Substring(row.BadgeStart, row.BadgeLength);
        Assert.Equal("[to do]", span);
    }

    [Fact]
    public void Format_NoStatus_ProducesNoBadge()
    {
        var task = new TaskItem { Id = "1", Name = "Untitled work", StatusName = null };

        var row = TaskRowFormatter.Format(task);

        Assert.Equal(0, row.BadgeLength);
        Assert.Equal(-1, row.BadgeStart);
        Assert.DoesNotContain('[', row.Text);
    }

    [Fact]
    public void Format_BlankStatus_ProducesNoBadge()
    {
        var task = new TaskItem { Id = "1", Name = "Work", StatusName = "   " };

        var row = TaskRowFormatter.Format(task);

        Assert.Equal(0, row.BadgeLength);
    }
}
