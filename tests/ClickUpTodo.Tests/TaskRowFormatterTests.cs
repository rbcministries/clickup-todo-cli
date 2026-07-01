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

    [Fact]
    public void Format_Indented_PrefixesTwoSpacesPerDepth()
    {
        var task = new TaskItem { Id = "1", Name = "Subtask" };

        Assert.StartsWith("  Subtask", TaskRowFormatter.Format(task, depth: 1).Text);
        Assert.StartsWith("    Subtask", TaskRowFormatter.Format(task, depth: 2).Text);
    }

    [Fact]
    public void Format_Indented_BadgeSpanShiftsToStayExact()
    {
        var task = new TaskItem { Id = "1", Name = "Subtask", StatusName = "to do" };

        var flat = TaskRowFormatter.Format(task);
        var nested = TaskRowFormatter.Format(task, depth: 2);

        // The badge span must still land exactly on the status bracket after indenting.
        Assert.Equal("[to do]", nested.Text.Substring(nested.BadgeStart, nested.BadgeLength));
        Assert.Equal(flat.BadgeStart + 4, nested.BadgeStart); // two indent units = 4 chars
    }

    [Fact]
    public void Format_ContextParent_AppendsMarker()
    {
        var task = new TaskItem { Id = "1", Name = "Parent not mine", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 0, isContextParent: true);

        Assert.Contains("(parent — not assigned to you)", row.Text);
        // Marker sits after the row body, so the badge span is unaffected.
        Assert.Equal("[to do]", row.Text.Substring(row.BadgeStart, row.BadgeLength));
    }
}
