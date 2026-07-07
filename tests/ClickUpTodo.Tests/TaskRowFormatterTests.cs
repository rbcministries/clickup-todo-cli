using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
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

        // Rows lead with the fixed-width priority gutter (blank here — no priority) then the title.
        Assert.StartsWith(TaskRowFormatter.LeadingBlank + "Ship the report", row.Text);
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
    public void Format_WithFoldMarker_PrependsMarkerAfterIndent_AndKeepsBadgeSpanExact()
    {
        var task = new TaskItem { Id = "1", Name = "Roll up sprint", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 1, marker: "▶ ");

        // Leading gutter (blank — no priority), then indent (2 per depth), then the marker, then the title.
        Assert.StartsWith(TaskRowFormatter.LeadingBlank + "  ▶ Roll up sprint", row.Text);
        // The badge span still lands exactly on the status bracket despite the marker shifting offsets.
        Assert.Equal("[to do]", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_DefaultMarker_IsEmpty_LayoutUnchanged()
    {
        var task = new TaskItem { Id = "1", Name = "Plain row", StatusName = "to do" };

        Assert.Equal(TaskRowFormatter.Format(task).Text, TaskRowFormatter.Format(task, marker: "").Text);
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

        // The leading gutter precedes the depth indent (blank here — the task has no priority).
        Assert.StartsWith(TaskRowFormatter.LeadingBlank + "  Subtask", TaskRowFormatter.Format(task, depth: 1).Text);
        Assert.StartsWith(TaskRowFormatter.LeadingBlank + "    Subtask", TaskRowFormatter.Format(task, depth: 2).Text);
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

    [Fact]
    public void Format_ForeignSubtask_AppendsNotAssignedMarker()
    {
        var task = new TaskItem { Id = "1", Name = "Teammate's subtask", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 1, isForeignSubtask: true);

        Assert.Contains("(not assigned to you)", row.Text);
        Assert.DoesNotContain("parent —", row.Text); // the child marker, not the context-parent one
        Assert.StartsWith(TaskRowFormatter.LeadingBlank + "  Teammate's subtask", row.Text); // still indented as a nested row
        Assert.Equal("[to do]", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_ContextParentWins_OverForeignSubtaskMarker()
    {
        var task = new TaskItem { Id = "1", Name = "P", StatusName = "to do" };

        // If both flags are ever set, the context-parent marker takes precedence (a parent row).
        var row = TaskRowFormatter.Format(task, isContextParent: true, isForeignSubtask: true);

        Assert.Contains("(parent — not assigned to you)", row.Text);
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
        // With no status, the priority badge sits right after the leading flag + title + two spaces.
        Assert.Equal(TaskRowFormatter.LeadingFlag.Length + task.Name.Length + 2, row.PriorityStart);
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

    // ── Leading priority flag gutter ─────────────────────────────────────────

    [Fact]
    public void Format_WithPriority_LeadsWithFlagBadge_AndReportsItsSpan()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", PriorityName = "Urgent" };

        var row = TaskRowFormatter.Format(task);

        // The row leads with the flag gutter, and the reported span covers exactly those three chars.
        Assert.StartsWith(TaskRowFormatter.LeadingFlag, row.Text);
        Assert.Equal(0, row.PriorityFlagStart);
        Assert.Equal(TaskRowFormatter.LeadingFlag.Length, row.PriorityFlagLength);
        Assert.Equal(TaskRowFormatter.LeadingFlag, row.Text.Substring(row.PriorityFlagStart, row.PriorityFlagLength));
        // The title follows the gutter.
        Assert.StartsWith(TaskRowFormatter.LeadingFlag + "Ship it", row.Text);
    }

    [Fact]
    public void Format_WithoutPriority_LeadsWithBlankGutter_AndReportsNoFlagSpan()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", PriorityName = null };

        var row = TaskRowFormatter.Format(task);

        // The blank gutter keeps the grid alignment but carries no colour span.
        Assert.StartsWith(TaskRowFormatter.LeadingBlank, row.Text);
        Assert.Equal(-1, row.PriorityFlagStart);
        Assert.Equal(0, row.PriorityFlagLength);
    }

    [Fact]
    public void Format_FlagAndBlankGutter_AreTheSameWidth()
    {
        // Grid-like layout depends on the set and unset gutters occupying the same number of chars.
        Assert.Equal(TaskRowFormatter.LeadingBlank.Length, TaskRowFormatter.LeadingFlag.Length);
    }

    [Fact]
    public void Format_GroupedByPriority_KeepsLeadingFlag_ThoughInlineBadgeIsDropped()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, groupedBy: TaskField.Priority);

        // The inline [priority] badge is dropped (its header conveys it), but the structural flag stays.
        Assert.Equal(-1, row.PriorityStart);
        Assert.DoesNotContain("[High]", row.Text);
        Assert.Equal(0, row.PriorityFlagStart);
        Assert.Equal(TaskRowFormatter.LeadingFlag, row.Text.Substring(row.PriorityFlagStart, row.PriorityFlagLength));
    }

    // ── Omit the grouped field from the row (#67) ────────────────────────────

    private static TaskItem FullRowTask() => new()
    {
        Id = "1",
        Name = "Ship the report",
        StatusName = "in progress",
        PriorityName = "High",
        ListName = "Personal Tasks",
        DueDateMs = DateTimeOffset.Parse("2026-07-01T12:00:00Z").ToUnixTimeMilliseconds(),
    };

    [Fact]
    public void Format_GroupedByStatus_OmitsStatusBadge_KeepsOthers()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Status);

        // Status badge is omitted, reporting the absent sentinel so no colour span is drawn.
        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(0, row.StatusLength);
        Assert.DoesNotContain("[in progress]", row.Text);
        // Every other segment is intact.
        Assert.Equal("[High]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
        Assert.Contains("· Personal Tasks", row.Text);
        Assert.Contains("· due ", row.Text);
    }

    [Fact]
    public void Format_GroupedByPriority_OmitsPriorityBadge_KeepsStatus()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Priority);

        Assert.Equal(-1, row.PriorityStart);
        Assert.Equal(0, row.PriorityLength);
        Assert.DoesNotContain("[High]", row.Text);
        // Status badge still present and its span still exact.
        Assert.Equal("[in progress]", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_GroupedByList_OmitsListSegment_KeepsDue()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.List);

        Assert.DoesNotContain("· Personal Tasks", row.Text);
        Assert.Contains("· due ", row.Text);
        // Badges are unaffected.
        Assert.Equal("[in progress]", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_GroupedByDue_OmitsDueSegment_KeepsList()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Due);

        Assert.DoesNotContain("· due ", row.Text);
        Assert.Contains("· Personal Tasks", row.Text);
    }

    [Fact]
    public void Format_GroupedByStatus_PriorityBadgeShiftsLeftToStayExact()
    {
        var task = FullRowTask();
        var ungrouped = TaskRowFormatter.Format(task);
        var grouped = TaskRowFormatter.Format(task, groupedBy: TaskField.Status);

        // With the status badge gone, the priority badge sits right after the leading flag + title +
        // two spaces, and its reported span still lands exactly on the bracket.
        Assert.Equal("[High]", grouped.Text.Substring(grouped.PriorityStart, grouped.PriorityLength));
        Assert.Equal(TaskRowFormatter.LeadingFlag.Length + task.Name.Length + 2, grouped.PriorityStart);
        Assert.True(grouped.PriorityStart < ungrouped.PriorityStart);
    }

    [Theory]
    [InlineData(TaskField.Created)]
    [InlineData(TaskField.LastActivity)]
    public void Format_GroupedByRowlessField_LeavesRowUnchanged(TaskField field)
    {
        var task = FullRowTask();

        var grouped = TaskRowFormatter.Format(task, groupedBy: field);
        var ungrouped = TaskRowFormatter.Format(task);

        // Created / LastActivity have no row segment, so grouping by them changes nothing.
        Assert.Equal(ungrouped.Text, grouped.Text);
        Assert.Equal(ungrouped.StatusStart, grouped.StatusStart);
        Assert.Equal(ungrouped.PriorityStart, grouped.PriorityStart);
    }

    [Fact]
    public void Format_Ungrouped_KeepsEverySegment()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: null);

        Assert.Equal("[in progress]", row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal("[High]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
        Assert.Contains("· Personal Tasks", row.Text);
        Assert.Contains("· due ", row.Text);
    }
}
