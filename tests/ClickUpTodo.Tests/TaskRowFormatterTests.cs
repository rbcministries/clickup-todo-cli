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

    // ── Status abbreviation helper (#181) ────────────────────────────────────

    [Theory]
    [InlineData("Won't Do", "(WD)")]        // multi-word: first char of first + last word
    [InlineData("Blocked", "(B )")]         // single word: first letter + trailing space
    [InlineData("In Progress", "(IP)")]     // multi-word, spaces
    [InlineData("in progress", "(IP)")]     // lowercase is uppercased
    [InlineData("to do", "(TD)")]           // the fixture status used across the icon-mode tests
    [InlineData("Ready/Review", "(RR)")]    // '/' separates words
    [InlineData("Ready-For-QA", "(RQ)")]    // '-' separates words (first + last)
    [InlineData("Dev|Prod", "(DP)")]        // '|' separates words
    [InlineData("Won't", "(W )")]           // an apostrophe does NOT split — one word
    [InlineData("A/B/C/D", "(AD)")]         // multiple separators: still first + last word
    [InlineData("done", "(D )")]            // single lowercase word
    [InlineData("3rd Party", "(3P)")]       // a digit is a valid initial
    [InlineData("@blocked", "(B )")]        // a leading symbol is skipped to the first letter
    [InlineData("🔥 Blocked", "(B )")]       // emoji word has no letter — the real word abbreviates
    [InlineData("🔥 In Progress", "(IP)")]   // leading emoji word skipped; real words used
    [InlineData("✅ Done", "(D )")]          // emoji-prefixed single real word
    [InlineData("🔥", "(  )")]               // only a symbol/emoji, no letters/digits
    public void StatusAbbreviation_FollowsTheRules(string statusName, string expected)
    {
        Assert.Equal(expected, TaskRowFormatter.StatusAbbreviation(statusName));
    }

    [Fact]
    public void StatusAbbreviation_EmojiPrefixedName_ProducesWellFormedText_NoLoneSurrogate()
    {
        // Regression for the UTF-16-indexing bug: taking the first *char* of "🚀" grabbed a lone high
        // surrogate. Extracting whole runes (and skipping the symbol) yields the real letters, and the
        // result must be well-formed UTF-16 (no unpaired surrogate).
        var abbrev = TaskRowFormatter.StatusAbbreviation("🚀 Ship It");

        Assert.Equal("(SI)", abbrev);
        Assert.DoesNotContain(abbrev, c => char.IsSurrogate(c)); // no lone (or any) surrogate leaked through
    }

    [Theory]
    [InlineData("Won't Do")]
    [InlineData("Blocked")]
    [InlineData("In Progress")]
    [InlineData("A really long status name")]
    [InlineData("/")]      // all-separators degenerate name
    [InlineData("- | /")]  // all separators + whitespace
    public void StatusAbbreviation_IsAlwaysFourChars(string statusName)
    {
        // Standardised width so short-variant Status badges never mix widths (the coloured span, the
        // gutter, and the chip must all be four columns).
        Assert.Equal(4, TaskRowFormatter.StatusAbbreviation(statusName).Length);
    }

    [Fact]
    public void StatusAbbreviation_AllSeparators_YieldsParenthesisedBlank()
    {
        // A name with no word characters (only separators) has no letters to extract — a defensive
        // "(  )" keeps the four-column invariant rather than throwing.
        Assert.Equal("(  )", TaskRowFormatter.StatusAbbreviation("/-|"));
    }

    [Fact]
    public void StatusAbbreviation_IsParenthesised()
    {
        var abbrev = TaskRowFormatter.StatusAbbreviation("In Progress");
        Assert.StartsWith("(", abbrev);
        Assert.EndsWith(")", abbrev);
    }

    // ── Icon mode: id chip + status (XX) abbrev chip + priority ⚑ chip, id first ─

    [Fact]
    public void IconMode_IdChipLeads_ThenStatusChip_ThenPriorityChip_ThenTitle()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);

        // The id chip (fallback task id "1") leads, then the status abbrev chip ("to do" → "(TD)"), then
        // the priority chip, then the title.
        Assert.StartsWith("1 (TD)" + TaskRowFormatter.PriorityIcon + "Ship it", row.Text);
        Assert.Equal(0, row.CustomIdStart);
        Assert.Equal("1", row.Text.Substring(row.CustomIdStart, row.CustomIdLength));
        // The status/priority spans land exactly on their chips, status before priority, both after the id chip.
        Assert.Equal(row.CustomIdLength + 1, row.StatusStart);
        Assert.Equal("(TD)", row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal(row.StatusStart + row.StatusLength, row.PriorityStart);
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
        Assert.StartsWith("2 (TD)" + TaskRowFormatter.BlankGutter + "Ship it", b.Text);
        // The title starts at the same column whether or not a priority chip is present (both ids are
        // single-char here, so the id chip doesn't shift the title between the two rows).
        Assert.Equal(a.Text.IndexOf("Ship it", StringComparison.Ordinal), b.Text.IndexOf("Ship it", StringComparison.Ordinal));
    }

    [Fact]
    public void IconMode_NoStatus_BlankGutter_PriorityChipStillAligned()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = null, PriorityName = "Urgent" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);

        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(0, row.StatusLength);
        // The id chip leads, then the four-column blank status gutter, then the priority chip.
        Assert.StartsWith("1 " + TaskRowFormatter.StatusGutter + TaskRowFormatter.PriorityIcon + "Ship it", row.Text);
        Assert.Equal(row.CustomIdLength + 1 + TaskRowFormatter.StatusGutter.Length, row.PriorityStart);
        Assert.Equal(TaskRowFormatter.PriorityIcon, row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void IconMode_ChipAndGutterWidths_MatchPerColumn()
    {
        // The grid holds per column: Priority's chip and its blank gutter are three columns; the Status
        // abbrev chip and its (wider) gutter are four columns (#181). A status abbrev is always 4 chars.
        Assert.Equal(3, TaskRowFormatter.PriorityIcon.Length);
        Assert.Equal(3, TaskRowFormatter.BlankGutter.Length);
        Assert.Equal(4, TaskRowFormatter.StatusGutter.Length);
        Assert.Equal(4, TaskRowFormatter.StatusAbbreviation("In Progress").Length);
    }

    [Fact]
    public void IconMode_Indented_ChipsLeadTheRow_TitleShiftsRight()
    {
        var task = new TaskItem { Id = "1", Name = "Subtask", StatusName = "to do", PriorityName = "Low" };

        var flat = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);
        var nested = TaskRowFormatter.Format(task, depth: 2, badges: BadgeDisplay.Icons);

        // The id chip and the status/priority chips stay the leftmost gutter regardless of depth — the
        // indent shifts only the title, which now follows the chips (four-space indent for depth 2).
        Assert.Equal(nested.CustomIdLength + 1, nested.StatusStart);
        Assert.Equal(nested.StatusStart + nested.StatusLength, nested.PriorityStart);
        Assert.StartsWith("1 (TD)" + TaskRowFormatter.PriorityIcon + "    Subtask", nested.Text);
        Assert.Equal(flat.StatusStart, nested.StatusStart);
    }

    [Fact]
    public void IconMode_WithFoldMarker_MarkerFollowsChipsAndIndent()
    {
        var task = new TaskItem { Id = "1", Name = "Roll up sprint", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 1, marker: "▶ ", badges: BadgeDisplay.Icons);

        // The id chip, then the status abbrev chip + blank priority gutter, then indent (2 per depth), then
        // the marker, then the title.
        Assert.StartsWith("1 (TD)" + TaskRowFormatter.BlankGutter + "  ▶ Roll up sprint", row.Text);
        Assert.Equal("(TD)", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    // ── Text mode: id then "○ {status}" "⚑ {priority}", id first (#171 + #162) ─

    [Fact]
    public void TextMode_IdChipLeads_ThenStatusBadge_ThenPriorityBadge_ThenTitle()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        // The id chip (fallback task id "1") leads, then the "{icon} {name}" status badge, then the
        // priority badge, then the title (#171 id chip + #162 glyph badges).
        Assert.StartsWith("1 ○ to do ⚑ High Ship it", row.Text);
        Assert.Equal("1", row.Text.Substring(row.CustomIdStart, row.CustomIdLength));
        Assert.Equal("○ to do", row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal("⚑ High", row.Text.Substring(row.PriorityStart, row.PriorityLength));
        // The id precedes status, status precedes priority, and no coloured span includes a separator space.
        Assert.True(row.StatusStart > row.CustomIdStart + row.CustomIdLength);
        Assert.True(row.PriorityStart > row.StatusStart + row.StatusLength);
    }

    [Fact]
    public void TextMode_BadgesShareTheDetailTitleFormat()
    {
        // The list text badge reads identically to the shared StatusPriorityBadge label the detail
        // title line uses, so the two surfaces can't drift (#162).
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "In Progress", PriorityName = "Urgent" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        Assert.Equal(StatusPriorityBadge.Status("In Progress"), row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal(StatusPriorityBadge.Priority("Urgent"), row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void TextMode_LiteralBadgeInTitle_DoesNotConfuseTheSpan()
    {
        // A "⚑ High" literal in the title must not be mistaken for the leading priority badge span.
        var task = new TaskItem { Id = "1", Name = "Review ⚑ High priority doc", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        // With no status, the priority badge is the first badge after the id chip; the reported span is
        // that badge — not the "⚑ High" literal embedded later in the title.
        Assert.Equal(row.CustomIdLength + 1, row.PriorityStart);
        Assert.Equal("⚑ High", row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void TextMode_NoStatus_PriorityLeadsWithNoStatusGutter()
    {
        var task = new TaskItem { Id = "1", Name = "Work", StatusName = null, PriorityName = "Urgent" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        Assert.Equal(-1, row.StatusStart);
        Assert.Equal(0, row.StatusLength);
        // Text mode is ragged — an absent status leaves no gutter, so the priority badge sits right after
        // the leading id chip (not pushed right by an empty status gutter).
        Assert.Equal(row.CustomIdLength + 1, row.PriorityStart);
        Assert.StartsWith("1 ⚑ Urgent Work", row.Text);
    }

    [Fact]
    public void TextMode_Indented_BadgesLead_TitleShiftsRight()
    {
        var task = new TaskItem { Id = "1", Name = "Subtask", StatusName = "to do", PriorityName = "Low" };

        var row = TaskRowFormatter.Format(task, depth: 2, badges: BadgeDisplay.Text);

        // The id chip ("1 "), then the "{icon} {name}" badges, then two indent units (4 spaces) before the title.
        Assert.StartsWith("1 ○ to do ⚑ Low     Subtask", row.Text);
        Assert.Equal("○ to do", row.Text.Substring(row.StatusStart, row.StatusLength));
        Assert.Equal("⚑ Low", row.Text.Substring(row.PriorityStart, row.PriorityLength));
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

        // Both badges absent ⇒ fully ragged, so the id chip (fallback task id "1") leads, then the title.
        Assert.StartsWith("1 Bare task", row.Text);
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
        // The id chip still leads; the priority chip follows it directly.
        Assert.Equal(-1, row.StatusStart);
        Assert.StartsWith("1 " + TaskRowFormatter.PriorityIcon + "Ship it", row.Text);
        Assert.Equal(row.CustomIdLength + 1, row.PriorityStart);
        Assert.Equal(TaskRowFormatter.PriorityIcon, row.Text.Substring(row.PriorityStart, row.PriorityLength));
    }

    [Fact]
    public void IconMode_GroupedByPriority_DropsPriorityChipEntirely_KeepsStatusChip()
    {
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", PriorityName = "High" };

        var row = TaskRowFormatter.Format(task, groupedBy: TaskField.Priority, badges: BadgeDisplay.Icons);

        Assert.Equal(-1, row.PriorityStart);
        Assert.DoesNotContain('⚑', row.Text);
        Assert.StartsWith("1 (TD)Ship it", row.Text);
        Assert.Equal(row.CustomIdLength + 1, row.StatusStart);
    }

    [Fact]
    public void TextMode_GroupedByStatus_OmitsStatusBadge_KeepsPriority()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Status, badges: BadgeDisplay.Text);

        Assert.Equal(-1, row.StatusStart);
        Assert.DoesNotContain("○ in progress", row.Text);
        Assert.StartsWith("1 ⚑ High Ship the report", row.Text);
    }

    [Fact]
    public void TextMode_GroupedByPriority_OmitsPriorityBadge_KeepsStatus()
    {
        var row = TaskRowFormatter.Format(FullRowTask(), groupedBy: TaskField.Priority, badges: BadgeDisplay.Text);

        Assert.Equal(-1, row.PriorityStart);
        Assert.DoesNotContain("⚑ High", row.Text);
        Assert.StartsWith("1 ○ in progress Ship the report", row.Text);
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
        Assert.Equal("(IP)", row.Text.Substring(row.StatusStart, row.StatusLength)); // "in progress" → (IP)
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
        Assert.Equal("(TD)", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_ForeignSubtask_AppendsNotAssignedMarker()
    {
        var task = new TaskItem { Id = "1", Name = "Teammate's subtask", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 1, isForeignSubtask: true);

        Assert.Contains("(not assigned to you)", row.Text);
        Assert.DoesNotContain("parent —", row.Text); // the child marker, not the context-parent one
        Assert.Equal("(TD)", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_ContextParentWins_OverForeignSubtaskMarker()
    {
        var task = new TaskItem { Id = "1", Name = "P", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, isContextParent: true, isForeignSubtask: true);

        Assert.Contains("(parent — not assigned to you)", row.Text);
    }

    [Fact]
    public void Format_UnassignedSubtask_AppendsUnassignedMarker()
    {
        var task = new TaskItem { Id = "1", Name = "Nobody's subtask", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, depth: 1, isUnassignedSubtask: true);

        Assert.Contains("(unassigned)", row.Text);
        Assert.DoesNotContain("not assigned to you", row.Text); // the unassigned marker, not the foreign one
        Assert.Equal("(TD)", row.Text.Substring(row.StatusStart, row.StatusLength));
    }

    [Fact]
    public void Format_UnassignedSubtaskWins_OverForeignSubtaskMarker()
    {
        // A row is classified as one or the other; unassigned takes precedence over the not-mine marker.
        var task = new TaskItem { Id = "1", Name = "S", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, isUnassignedSubtask: true, isForeignSubtask: true);

        Assert.Contains("(unassigned)", row.Text);
        Assert.DoesNotContain("not assigned to you", row.Text);
    }

    [Fact]
    public void Format_ContextParentWins_OverUnassignedSubtaskMarker()
    {
        var task = new TaskItem { Id = "1", Name = "P", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, isContextParent: true, isUnassignedSubtask: true);

        Assert.Contains("(parent — not assigned to you)", row.Text);
        Assert.DoesNotContain("(unassigned)", row.Text);
    }

    // ── Trailing assignees badge (#161) ──────────────────────────────────────

    private const long Me = 100;
    private const long Teammate = 200;

    private static TaskItem TaskWithAssignees(params TaskAssignee[] assignees) => new()
    {
        Id = "1",
        Name = "Shared work",
        StatusName = "to do",
        Assignees = assignees,
    };

    [Fact]
    public void IconMode_OtherAssignee_AppendsTrailingChip_SpanExact()
    {
        var task = TaskWithAssignees(new TaskAssignee(Teammate, "Jo"));

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons, currentUserId: Me);

        // The 👥 chip trails the title and its reported span lands exactly on the chip.
        Assert.EndsWith(TaskRowFormatter.AssigneesIcon, row.Text);
        Assert.True(row.AssigneesStart > 0);
        Assert.Equal(TaskRowFormatter.AssigneesIcon, row.Text.Substring(row.AssigneesStart, row.AssigneesLength));
        // It follows the title (not a leading gutter chip).
        Assert.True(row.AssigneesStart > row.Text.IndexOf("Shared work", StringComparison.Ordinal));
    }

    [Fact]
    public void IconMode_SoloOwnAssignee_NoBadge()
    {
        var task = TaskWithAssignees(new TaskAssignee(Me, "Me"));

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons, currentUserId: Me);

        Assert.Equal(-1, row.AssigneesStart);
        Assert.Equal(0, row.AssigneesLength);
        Assert.DoesNotContain("👥", row.Text);
    }

    [Fact]
    public void IconMode_Unassigned_NoBadge()
    {
        var row = TaskRowFormatter.Format(TaskWithAssignees(), badges: BadgeDisplay.Icons, currentUserId: Me);

        Assert.Equal(-1, row.AssigneesStart);
        Assert.DoesNotContain("👥", row.Text);
    }

    [Fact]
    public void IconMode_MixedMeAndTeammate_ShowsBadge()
    {
        // The current user is on it too, but a teammate also is — shared work, so the badge shows.
        var task = TaskWithAssignees(new TaskAssignee(Me, "Me"), new TaskAssignee(Teammate, "Jo"));

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons, currentUserId: Me);

        Assert.Contains("👥", row.Text);
        Assert.Equal(TaskRowFormatter.AssigneesIcon, row.Text.Substring(row.AssigneesStart, row.AssigneesLength));
    }

    [Fact]
    public void TextMode_ListsOtherAssigneeNames_ExcludingCurrentUser()
    {
        var task = TaskWithAssignees(
            new TaskAssignee(Me, "Me"), new TaskAssignee(Teammate, "Jo"), new TaskAssignee(300, "Sam"));

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text, currentUserId: Me);

        // White-background chip listing the other assignees' names (current user excluded), span exact.
        var chip = row.Text.Substring(row.AssigneesStart, row.AssigneesLength);
        Assert.Equal(" Jo, Sam ", chip);
    }

    [Fact]
    public void HiddenMode_NoAssigneesBadge()
    {
        var task = TaskWithAssignees(new TaskAssignee(Teammate, "Jo"));

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Hidden, currentUserId: Me);

        Assert.Equal(-1, row.AssigneesStart);
        Assert.Equal(0, row.AssigneesLength);
        Assert.DoesNotContain("👥", row.Text);
    }

    [Fact]
    public void NullCurrentUser_TreatsEveryAssigneeAsOther()
    {
        var task = TaskWithAssignees(new TaskAssignee(Me, "Me"));

        // With an unknown signed-in id, we can't exclude anyone — any assignee shows the badge.
        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons, currentUserId: null);

        Assert.Contains("👥", row.Text);
        Assert.True(row.AssigneesStart > 0);
    }

    [Fact]
    public void GroupedByAssignee_DropsTheBadge()
    {
        var task = TaskWithAssignees(new TaskAssignee(Teammate, "Jo"));

        var row = TaskRowFormatter.Format(
            task, groupedBy: TaskField.Assignee, badges: BadgeDisplay.Icons, currentUserId: Me);

        // The group header already conveys the assignee (#67), so the per-row badge is dropped.
        Assert.Equal(-1, row.AssigneesStart);
        Assert.DoesNotContain("👥", row.Text);
    }

    [Fact]
    public void AssigneesBadge_PrecedesContextMarker_AndLeavesStatusPrioritySpansIntact()
    {
        var task = new TaskItem
        {
            Id = "1",
            Name = "Shared work",
            StatusName = "to do",
            PriorityName = "High",
            ListName = "Personal Tasks",
            DueDateMs = DateTimeOffset.Parse("2026-07-01T12:00:00Z").ToUnixTimeMilliseconds(),
            Assignees = [new TaskAssignee(Teammate, "Jo")],
        };

        var withBadge = TaskRowFormatter.Format(
            task, isForeignSubtask: true, badges: BadgeDisplay.Icons, currentUserId: Me);
        var noBadge = TaskRowFormatter.Format(
            task, isForeignSubtask: true, badges: BadgeDisplay.Icons, currentUserId: Teammate);

        // The 👥 chip follows both the · list and · due segments and precedes the trailing
        // "(not assigned to you)" marker (per the AC, the badge coexists with the context marker).
        var chipIdx = withBadge.Text.IndexOf("👥", StringComparison.Ordinal);
        var listIdx = withBadge.Text.IndexOf("· Personal Tasks", StringComparison.Ordinal);
        var dueIdx = withBadge.Text.IndexOf("· due ", StringComparison.Ordinal);
        var markerIdx = withBadge.Text.IndexOf("(not assigned to you)", StringComparison.Ordinal);
        Assert.True(listIdx < chipIdx && dueIdx < chipIdx && chipIdx < markerIdx);
        // A two-space separator (uncoloured) precedes the coloured chip.
        Assert.Equal("  ", withBadge.Text.Substring(withBadge.AssigneesStart - 2, 2));

        // The leading Status/Priority chips are unaffected by whether the trailing badge is present.
        Assert.Equal(noBadge.StatusStart, withBadge.StatusStart);
        Assert.Equal(noBadge.PriorityStart, withBadge.PriorityStart);
        Assert.Equal(-1, noBadge.AssigneesStart); // Teammate is "me" here, so their own solo assignment shows no badge
    }

    // ── Custom-id / task-id chip ─────────────────────────────────────────────

    [Fact]
    public void IconMode_CustomId_LeadsBadges_SpanExact()
    {
        var task = new TaskItem
        {
            Id = "86xyz",
            CustomId = "ABC-123",
            Name = "Ship it",
            StatusName = "to do",
            PriorityName = "High",
        };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);

        // The custom id chip leads the row as the first badge (#171 follow-up), ahead of the status/priority
        // chips; its reported span lands exactly on the id text (the trailing separator space excluded).
        Assert.StartsWith("ABC-123 (TD)" + TaskRowFormatter.PriorityIcon + "Ship it", row.Text);
        Assert.Equal("ABC-123", row.Text.Substring(row.CustomIdStart, row.CustomIdLength));
        Assert.Equal(0, row.CustomIdStart);
        // The status chip follows the id chip (id text + its trailing separator space).
        Assert.Equal(row.CustomIdLength + 1, row.StatusStart);
    }

    [Fact]
    public void CustomId_FallsBackToTaskId_WhenUnset()
    {
        var task = new TaskItem { Id = "86xyz", CustomId = null, Name = "Ship it", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);

        // No Space custom id ⇒ the plain task id stands in, still leading as the first badge.
        Assert.Equal(0, row.CustomIdStart);
        Assert.Equal("86xyz", row.Text.Substring(row.CustomIdStart, row.CustomIdLength));
        Assert.StartsWith("86xyz (TD)", row.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CustomId_BlankCustomId_FallsBackToTaskId(string? customId)
    {
        var task = new TaskItem { Id = "86xyz", CustomId = customId, Name = "Ship it" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        Assert.Equal("86xyz", row.Text.Substring(row.CustomIdStart, row.CustomIdLength));
    }

    [Fact]
    public void TextMode_CustomId_LeadsBadges()
    {
        var task = new TaskItem
        {
            Id = "1",
            CustomId = "DEV-42",
            Name = "Ship it",
            StatusName = "to do",
            PriorityName = "High",
        };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Text);

        Assert.StartsWith("DEV-42 ○ to do ⚑ High Ship it", row.Text);
        Assert.Equal(0, row.CustomIdStart);
        Assert.Equal("DEV-42", row.Text.Substring(row.CustomIdStart, row.CustomIdLength));
    }

    [Fact]
    public void HiddenMode_OmitsCustomId()
    {
        // Hidden is the decoration-free view — the id rides with the badges, so it's dropped too.
        var task = new TaskItem { Id = "1", CustomId = "ABC-123", Name = "Ship it", StatusName = "to do" };

        var row = TaskRowFormatter.Format(task, badges: BadgeDisplay.Hidden);

        Assert.Equal(-1, row.CustomIdStart);
        Assert.Equal(0, row.CustomIdLength);
        Assert.StartsWith("Ship it", row.Text);
        Assert.DoesNotContain("ABC-123", row.Text);
    }

    [Fact]
    public void CustomId_IsRaggedLikeTextBadges_NoUniformGutter()
    {
        // Custom-id formats vary by Space, so widths are nonstandard. Like variable-length status names
        // in text mode, the id is left ragged — a longer id pushes the title further right, rather than
        // being padded to a uniform gutter.
        var shortId = new TaskItem { Id = "1", CustomId = "A-1", Name = "Ship it", StatusName = "to do" };
        var longId = new TaskItem { Id = "2", CustomId = "LONGPREFIX-9999", Name = "Ship it", StatusName = "to do" };

        var a = TaskRowFormatter.Format(shortId, badges: BadgeDisplay.Icons);
        var b = TaskRowFormatter.Format(longId, badges: BadgeDisplay.Icons);

        Assert.NotEqual(
            a.Text.IndexOf("Ship it", StringComparison.Ordinal),
            b.Text.IndexOf("Ship it", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomId_ShownRegardlessOfGrouping()
    {
        // The id isn't a groupable field, so grouping never drops it (unlike Status/Priority badges, #67).
        var task = new TaskItem
        {
            Id = "1",
            CustomId = "ABC-123",
            Name = "Ship it",
            StatusName = "to do",
            PriorityName = "High",
        };

        foreach (var field in new[] { TaskField.Status, TaskField.Priority, TaskField.Assignee })
        {
            var row = TaskRowFormatter.Format(task, groupedBy: field, badges: BadgeDisplay.Icons);
            Assert.Equal("ABC-123", row.Text.Substring(row.CustomIdStart, row.CustomIdLength));
        }
    }
}
