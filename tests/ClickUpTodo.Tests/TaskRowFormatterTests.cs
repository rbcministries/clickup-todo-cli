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
        Assert.True(row.StatusLength > 0);
        var span = row.Text.Substring(row.StatusStart, row.StatusLength);
        Assert.Equal("[to do]", span);
    }

    [Fact]
    public void Format_NoStatus_ProducesNoBadge()
    {
        var task = new TaskItem { Id = "1", Name = "Untitled work", StatusName = null };

        var row = TaskRowFormatter.Format(task);

        Assert.Equal(0, row.StatusLength);
        Assert.Equal(-1, row.StatusStart);
        Assert.DoesNotContain('[', row.Text);
    }

    [Fact]
    public void Format_BlankStatus_ProducesNoBadge()
    {
        var task = new TaskItem { Id = "1", Name = "Work", StatusName = "   " };

        var row = TaskRowFormatter.Format(task);

        Assert.Equal(0, row.StatusLength);
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
        Assert.Equal("[to do]", nested.Text.Substring(nested.StatusStart, nested.StatusLength));
        Assert.Equal(flat.StatusStart + 4, nested.StatusStart); // two indent units = 4 chars
    }

    [Fact]
    public void Format_ContextParent_AppendsMarker()
    {
        var task = new TaskItem { Id = "1", Name = "Parent not mine", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 0, isContextParent: true);

        Assert.Contains("(parent — not assigned to you)", row.Text);
        // Marker sits after the row body, so the badge span is unaffected.
        Assert.Equal("[to do]", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    // ── Priority badge (#55) ─────────────────────────────────────────────────

    [Fact]
    public void Format_PrioritySpan_ExactlyCoversThePriorityBracket()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task);

        Assert.True(row.PriorityLength > 0);
        Assert.Equal("[High]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
        // Status and priority spans are distinct and non-overlapping; priority follows status.
        Assert.Equal("[to do]", row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.True(row.PriorityStart > row.StatusStart + row.StatusLength);
        Assert.Contains("[to do]  [High]", row.Text);
    }

    [Fact]
    public void Format_LiteralPriorityInTitle_DoesNotConfuseTheSpan()
    {
        // A "[High]" literal in the title must not be mistaken for the priority badge span.
        var task = new TaskItem { Id = "1", Name = "Review [High] priority doc", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task);

        // The reported span is the trailing badge, not the title occurrence.
        Assert.Equal("[High]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
        Assert.True(row.PriorityStart > task.Name.Length);
    }

    [Fact]
    public void Format_NoPriority_ProducesNoPriorityBadge()
    {
        var task = new TaskItem { Id = "1", Name = "Work", StatusName = "to do", PriorityName = null };

        var row = TaskRowFormatter.Format(task);

        Assert.Equal(0, row.PriorityLength);
        Assert.Equal(-1, row.PriorityStart);
    }

    [Fact]
    public void Format_PriorityWithoutStatus_PrioritySpanStillExact()
    {
        var task = new TaskItem { Id = "1", Name = "Work", StatusName = null, PriorityName = "Urgent" };

        var row = TaskRowFormatter.Format(task);

        Assert.Equal(0, row.StatusLength);
        Assert.Equal(-1, row.StatusStart);
        Assert.Equal("[Urgent]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
        // With no status, the priority badge sits right after the title + two spaces.
        Assert.Equal(task.Name.Length + 2, row.PriorityStart);
    }

    [Fact]
    public void Format_Indented_PrioritySpanShiftsToStayExact()
    {
        var task = new TaskItem { Id = "1", Name = "Subtask", StatusName = "to do", PriorityName = "Low" };

        var flat = TaskRowFormatter.Format(task);
        var nested = TaskRowFormatter.Format(task, depth: 2);

        Assert.Equal("[Low]", nested.Text.Substring(nested.PriorityStart, nested.PriorityLength));
        Assert.Equal(flat.PriorityStart + 4, nested.PriorityStart); // two indent units = 4 chars
    }
}
