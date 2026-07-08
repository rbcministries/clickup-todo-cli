using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

public sealed class TaskRowFormatterTests
{
    // ── Default mode ─────────────────────────────────────────────────────────

    [Fact]
    public void Format_DefaultMode_IsIcons()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        // The default (no badges arg) matches an explicit Icons request.
        Assert.Equal(
            TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons).Text,
            TaskRowFormatter.Format(task).Text);
    }

    // ── Icon mode: status ○ chip + priority ⚑ chip, status first ─────────────

    [Fact]
    public void IconMode_StatusChipLeads_ThenPriorityChip_ThenTitle()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);

        // Status chip, then priority chip, then the title.
        Assert.StartsWith(TaskRowFormatter.StatusIcon + TaskRowFormatter.PriorityIcon + "Ship it", row.Text);
        // Both spans land exactly on their chips, status before priority.
        Assert.Equal(0, row.StatusStart);
        Assert.Equal(TaskRowFormatter.StatusIcon, row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal(TaskRowFormatter.StatusIcon.Length, row.PriorityStart);
        Assert.Equal(TaskRowFormatter.PriorityIcon, row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void IconMode_NoPriority_BlankGutterKeepsAlignment_NoPrioritySpan()
    {
        var withPriority = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };
        var noPriority = new TaskItem { Id = "2", Name = "Ship it", StatusName = "to do", PriorityName = null };

        var a = TaskRowFormatter.Format(withPriority, badges: BadgeDisplay.Icons);
        var b = TaskRowFormatter.Format(noPriority, badges: BadgeDisplay.Icons);

        // No priority ⇒ no coloured span, but a blank gutter keeps the title aligned with priced rows.
        Assert.Equal(-1, b.PriorityStart);
        Assert.Equal(0, b.PriorityLength);
        Assert.StartsWith(TaskRowFormatter.StatusIcon + TaskRowFormatter.BlankGutter + "Ship it", b.Text);
        // The title starts at the same column whether or not a priority chip is present.
        Assert.Equal(a.Text.IndexOf("Ship it", StringComparison.Ordinal), b.Text.IndexOf("Ship it", StringComparison.Ordinal));
    }

    [Fact]
    public void IconMode_NoStatus_BlankGutter_PriorityChipStillAligned()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = null, PriorityName = "Urgent" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);

        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(0, row.StatusLength);
        // Blank status gutter, then the priority chip at the second gutter slot.
        Assert.StartsWith(TaskRowFormatter.BlankGutter + TaskRowFormatter.PriorityIcon + "Ship it", row.Text);
        Assert.Equal(TaskRowFormatter.BlankGutter.Length, row.PriorityStart);
        Assert.Equal(TaskRowFormatter.PriorityIcon, row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void IconMode_ChipsAndBlankGutter_AreTheSameWidth()
    {
        // The grid layout depends on the two icon chips and the blank gutter being the same char width.
        Assert.Equal(TaskRowFormatter.BlankGutter.Length, TaskRowFormatter.StatusIcon.Length);
        Assert.Equal(TaskRowFormatter.BlankGutter.Length, TaskRowFormatter.PriorityIcon.Length);
    }

    [Fact]
    public void IconMode_Indented_ChipsLeadTheRow_TitleShiftsRight()
    {
        var task = new TaskItem { Id = "1", Name = "Subtask", StatusName = "to do", PriorityName = "Low" };

        var flat = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);
        var nested = TaskRowFormatter.Format(task, depth: 2, badges: BadgeDisplay.Icons);

        // Chips stay the leftmost gutter regardless of depth — the indent shifts only the title.
        Assert.Equal(0, nested.StatusStart);
        Assert.Equal(TaskRowFormatter.StatusIcon.Length, nested.PriorityStart);
        Assert.StartsWith(TaskRowFormatter.StatusIcon + TaskRowFormatter.PriorityIcon + "    Subtask", nested.Text);
        Assert.Equal(flat.StatusStart, nested.StatusStart);
    }

    [Fact]
    public void IconMode_WithFoldMarker_MarkerFollowsChipsAndIndent()
    {
        var task = new TaskItem { Id = "1", Name = "Roll up sprint", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 1, marker: "▶ ", badges: BadgeDisplay.Icons);

        // Chips, then indent (2 per depth), then the marker, then the title.
        Assert.StartsWith(TaskRowFormatter.StatusIcon + TaskRowFormatter.BlankGutter + "  ▶ Roll up sprint", row.Text);
        Assert.Equal(TaskRowFormatter.StatusIcon, row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    // ── Text mode: [status] [priority], status first ────────────────────────

    [Fact]
    public void TextMode_StatusBadgeLeads_ThenPriorityBadge_ThenTitle()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        Assert.StartsWith("[to do] [High] Ship it", row.Text);
        Assert.Equal("[to do]", row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal("[High]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
        // Status precedes priority, and neither coloured span includes the separator space.
        Assert.True(row.PriorityStart > row.StatusStart + row.StatusLength);
    }

    [Fact]
    public void TextMode_LiteralBadgeInTitle_DoesNotConfuseTheSpan()
    {
        // A "[High]" literal in the title must not be mistaken for the leading priority badge span.
        var task = new TaskItem { Id = "1", Name = "Review [High] priority doc", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        // With no status, the priority badge leads the row; the reported span is that leading badge.
        Assert.Equal(0, row.PriorityStart);
        Assert.Equal("[High]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void TextMode_NoStatus_PriorityLeadsWithNoStatusGutter()
    {
        var task = new TaskItem { Id = "1", Name = "Work", StatusName = null, PriorityName = "Urgent" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(0, row.StatusLength);
        // Text mode is ragged — an absent status leaves no gutter, so the priority badge is at column 0.
        Assert.Equal(0, row.PriorityStart);
        Assert.StartsWith("[Urgent] Work", row.Text);
    }

    [Fact]
    public void TextMode_Indented_BadgesLead_TitleShiftsRight()
    {
        var task = new TaskItem { Id = "1", Name = "Subtask", StatusName = "to do", PriorityName = "Low" };

        var row = TaskRowFormatter.Format(task, depth: 2, badges: BadgeDisplay.Text);

        Assert.StartsWith("[to do] [Low]     Subtask", row.Text); // two indent units = 4 spaces after the badges
        Assert.Equal("[to do]", row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal("[Low]", row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    // ── Hidden mode: no badges ───────────────────────────────────────────────

    [Fact]
    public void HiddenMode_LeadsWithTitle_AndReportsNoSpans()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Hidden);

        Assert.StartsWith("Ship it", row.Text);
        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(0, row.StatusLength);
        Assert.Equal(-1, row.PriorityStart);
        Assert.Equal(0, row.PriorityLength);
        // No badge brackets or chips leak into the line (the title has none of its own here).
        Assert.DoesNotContain('[', row.Text);
        Assert.DoesNotContain('○', row.Text);
        Assert.DoesNotContain('⚑', row.Text);
    }

    [Fact]
    public void HiddenMode_StillShowsMetadata()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), badges: BadgeDisplay.Hidden);

        Assert.StartsWith("Ship the report", row.Text);
        Assert.Contains("· Personal Tasks", row.Text);
        Assert.Contains("· due ", row.Text);
    }

    [Fact]
    public void HiddenMode_UnderGrouping_StillNoBadges()
    {
        // Hidden ignores grouping too — there are no badges to drop, so the row is unchanged.
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Status, badges: BadgeDisplay.Hidden);

        Assert.StartsWith("Ship the report", row.Text);
        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(-1, row.PriorityStart);
    }

    [Fact]
    public void TextMode_NoStatusNoPriority_TitleLeadsAtColumnZero()
    {
        var task = new TaskItem { Id = "1", Name = "Bare task" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        // Both badges absent ⇒ fully ragged, so the title leads with no gutter.
        Assert.StartsWith("Bare task", row.Text);
        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(-1, row.PriorityStart);
    }

    // ── Grouping drops the grouped field's badge (#67) ───────────────────────

    [Fact]
    public void IconMode_GroupedByStatus_DropsStatusChipEntirely_KeepsPriorityChip()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, groupedBy: TaskField.Status, badges: BadgeDisplay.Icons);

        // No status chip and no gutter for it — every grouped row drops it uniformly, so alignment holds.
        Assert.Equal(-1, row.StatusStart);
        Assert.StartsWith(TaskRowFormatter.PriorityIcon + "Ship it", row.Text);
        Assert.Equal(0, row.PriorityStart);
        Assert.Equal(TaskRowFormatter.PriorityIcon, row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void IconMode_GroupedByPriority_DropsPriorityChipEntirely_KeepsStatusChip()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, groupedBy: TaskField.Priority, badges: BadgeDisplay.Icons);

        Assert.Equal(-1, row.PriorityStart);
        Assert.DoesNotContain('⚑', row.Text);
        Assert.StartsWith(TaskRowFormatter.StatusIcon + "Ship it", row.Text);
        Assert.Equal(0, row.StatusStart);
    }

    [Fact]
    public void TextMode_GroupedByStatus_OmitsStatusBadge_KeepsPriority()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Status, badges: BadgeDisplay.Text);

        Assert.Equal(-1, row.StatusStart);
        Assert.DoesNotContain("[in progress]", row.Text);
        Assert.StartsWith("[High] Ship the report", row.Text);
    }

    [Fact]
    public void TextMode_GroupedByPriority_OmitsPriorityBadge_KeepsStatus()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Priority, badges: BadgeDisplay.Text);

        Assert.Equal(-1, row.PriorityStart);
        Assert.DoesNotContain("[High]", row.Text);
        Assert.StartsWith("[in progress] Ship the report", row.Text);
    }

    // ── Metadata omission (#67) is mode-independent ──────────────────────────

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
    public void Format_Ungrouped_KeepsEverySegment()
    {
        var row = TaskRowFormatter.Format(FullRowTask());

        Assert.Contains("· Personal Tasks", row.Text);
        Assert.Contains("· due ", row.Text);
        Assert.Equal(TaskRowFormatter.StatusIcon, row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal(TaskRowFormatter.PriorityIcon, row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void Format_GroupedByList_OmitsListSegment_KeepsDue()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.List);

        Assert.DoesNotContain("· Personal Tasks", row.Text);
        Assert.Contains("· due ", row.Text);
    }

    [Fact]
    public void Format_GroupedByDue_OmitsDueSegment_KeepsList()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Due);

        Assert.DoesNotContain("· due ", row.Text);
        Assert.Contains("· Personal Tasks", row.Text);
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

    // ── Trailing context markers ─────────────────────────────────────────────

    [Fact]
    public void Format_ContextParent_AppendsMarker_BadgeSpanUnaffected()
    {
        var task = new TaskItem { Id = "1", Name = "Parent not mine", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 0, isContextParent: true);

        Assert.Contains("(parent — not assigned to you)", row.Text);
        Assert.Equal(TaskRowFormatter.StatusIcon, row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_ForeignSubtask_AppendsNotAssignedMarker()
    {
        var task = new TaskItem { Id = "1", Name = "Teammate's subtask", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 1, isForeignSubtask: true);

        Assert.Contains("(not assigned to you)", row.Text);
        Assert.DoesNotContain("parent —", row.Text); // the child marker, not the context-parent one
        Assert.Equal(TaskRowFormatter.StatusIcon, row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_ContextParentWins_OverForeignSubtaskMarker()
    {
        var task = new TaskItem { Id = "1", Name = "P", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, isContextParent: true, isForeignSubtask: true);

        Assert.Contains("(parent — not assigned to you)", row.Text);
    }
}
