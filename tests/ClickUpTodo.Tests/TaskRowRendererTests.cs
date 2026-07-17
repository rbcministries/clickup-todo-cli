using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for <see cref="TaskRowRenderer"/> — the shared component (#284) that folds
/// <see cref="TaskRowFormatter.Format"/> (text + char spans) and the per-span colour overlay into one
/// call, so a non-host caller (the Task Tree tab, #291) renders rows identically to the main list.
/// The pairing under test is "which colour tints which span"; the pure text/layout is already covered by
/// <see cref="TaskRowFormatterTests"/>, and the colour math by <see cref="StatusBadgeListSourceTests"/>.
/// </summary>
public sealed class TaskRowRendererTests
{
    // Distinct field colours so a status↔priority mix-up (colouring the wrong span) is caught, not masked
    // by identical colours. All three fixed/field colours differ.
    private const string StatusHex = "aabbcc";
    private const string PriorityHex = "112233";

    private static TaskItem FullTask() => new()
    {
        Id = "T1",
        CustomId = "ABC-1",
        Name = "Ship it",
        StatusName = "In Progress",
        StatusColor = StatusHex,
        PriorityName = "Urgent",
        PriorityColor = PriorityHex,
        Assignees = [new TaskAssignee(99, "Casey")],
    };

    // ── Text parity: the renderer's line is exactly the formatter's ──────────────────────────

    [Theory]
    [InlineData(BadgeDisplay.Icons)]
    [InlineData(BadgeDisplay.Text)]
    [InlineData(BadgeDisplay.Hidden)]
    public void Render_Text_EqualsFormatterText(BadgeDisplay mode)
    {
        var task = FullTask();
        // currentUserId 1 ≠ the assignee (99), so the assignees badge shows.
        var expected = TaskRowFormatter.Format(task, badges: mode, currentUserId: 1);

        var rendered = TaskRowRenderer.Render(task, mode, currentUserId: 1);

        Assert.Equal(expected.Text, rendered.Text);
    }

    // ── Colour mapping: each span gets its own colour, on the right span ─────────────────────

    [Fact]
    public void Render_Icons_PairsEachSpanWithItsColour()
    {
        var task = FullTask();
        var fmt = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons, currentUserId: 1);

        var badges = TaskRowRenderer.Render(task, BadgeDisplay.Icons, currentUserId: 1).Badges;

        // Status span tinted by StatusColor, Priority span by PriorityColor, custom-id by the fixed gray,
        // assignees by the fixed white — each asserted against the canonical TryCreate factory so a
        // swapped span/colour pairing fails. Badge is a record struct → value equality.
        Assert.Contains(StatusBadgeListSource.TryCreate(fmt.StatusStart, fmt.StatusLength, StatusHex)!.Value, badges);
        Assert.Contains(StatusBadgeListSource.TryCreate(fmt.PriorityStart, fmt.PriorityLength, PriorityHex)!.Value, badges);
        Assert.Contains(StatusBadgeListSource.TryCreate(fmt.CustomIdStart, fmt.CustomIdLength, TaskRowRenderer.CustomIdBadgeColor)!.Value, badges);
        Assert.Contains(StatusBadgeListSource.TryCreate(fmt.AssigneesStart, fmt.AssigneesLength, TaskRowRenderer.AssigneesBadgeColor)!.Value, badges);
        Assert.Equal(4, badges.Count);
    }

    [Fact]
    public void Render_StatusAndPriorityColours_AreNotSwapped()
    {
        var task = FullTask();
        var fmt = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons, currentUserId: 1);

        var badges = TaskRowRenderer.Render(task, BadgeDisplay.Icons, currentUserId: 1).Badges;

        // The badge sitting on the status span must carry the status colour (not the priority colour).
        var onStatusSpan = badges.Single(b => b.Start == fmt.StatusStart);
        var onPrioritySpan = badges.Single(b => b.Start == fmt.PriorityStart);
        Assert.Equal(StatusBadgeListSource.TryCreate(fmt.StatusStart, fmt.StatusLength, StatusHex)!.Value.Attr, onStatusSpan.Attr);
        Assert.Equal(StatusBadgeListSource.TryCreate(fmt.PriorityStart, fmt.PriorityLength, PriorityHex)!.Value.Attr, onPrioritySpan.Attr);
        Assert.NotEqual(onStatusSpan.Attr, onPrioritySpan.Attr); // distinct field colours ⇒ distinct attrs
    }

    // ── Absent / hidden spans emit no badge ─────────────────────────────────────────────────

    [Fact]
    public void Render_NoPriority_EmitsNoPriorityBadge()
    {
        // A task with a status but no priority: the priority span is the (-1,0) sentinel, so TryCreate
        // returns null and no over-shaded badge is produced.
        var task = new TaskItem { Id = "T2", Name = "No priority", StatusName = "Done", StatusColor = StatusHex };
        var fmt = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons);

        var badges = TaskRowRenderer.Render(task, BadgeDisplay.Icons, currentUserId: null).Badges;

        Assert.Equal(-1, fmt.PriorityStart);
        Assert.DoesNotContain(badges, b => b.Length > 0 && b.Start == fmt.PriorityStart);
    }

    [Fact]
    public void Render_HiddenBadges_EmitsNoBadges()
    {
        // Hidden mode carries no id chip, no status/priority chips, no assignees badge — nothing to shade.
        var badges = TaskRowRenderer.Render(FullTask(), BadgeDisplay.Hidden, currentUserId: 1).Badges;

        Assert.Empty(badges);
    }

    [Fact]
    public void Render_AssigneeIsCurrentUser_EmitsNoAssigneesBadge()
    {
        // The only assignee is the current user, so the "someone else is on this" badge is dropped (#161).
        var task = FullTask(); // assignee id 99
        var fmt = TaskRowFormatter.Format(task, badges: BadgeDisplay.Icons, currentUserId: 99);

        var badges = TaskRowRenderer.Render(task, BadgeDisplay.Icons, currentUserId: 99).Badges;

        Assert.Equal(-1, fmt.AssigneesStart);
        Assert.DoesNotContain(badges, b => b.Start == fmt.AssigneesStart && b.Length > 0);
    }

    // ── Consumable standalone by a non-host caller (#284 acceptance) ─────────────────────────

    [Fact]
    public void Render_AncestrySet_IndentsAndColoursWithoutTodoApp()
    {
        // Build a parent + one depth-1 child purely as TaskItems and render each — no TodoApp, no row
        // arrays — proving the component is drivable over an arbitrary ancestry set (the #284 criterion,
        // and exactly what the Task Tree tab (#291) needs).
        var parent = new TaskItem { Id = "P", Name = "Parent", StatusName = "Open", StatusColor = StatusHex };
        var child = new TaskItem { Id = "C", Name = "Child", ParentId = "P", StatusName = "Open", StatusColor = StatusHex };

        var parentRow = TaskRowRenderer.Render(parent, BadgeDisplay.Icons, currentUserId: 1, depth: 0);
        var childRow = TaskRowRenderer.Render(child, BadgeDisplay.Icons, currentUserId: 1, depth: 1);

        // The child renders identically to the formatter at the same depth (indentation preserved), and
        // its badge spans line up with what the formatter reported — no host state involved.
        var childFmt = TaskRowFormatter.Format(child, depth: 1, badges: BadgeDisplay.Icons, currentUserId: 1);
        Assert.Equal(childFmt.Text, childRow.Text);
        Assert.Contains("Child", childRow.Text);
        Assert.Contains(StatusBadgeListSource.TryCreate(childFmt.StatusStart, childFmt.StatusLength, StatusHex)!.Value, childRow.Badges);
        // The depth-1 child's title is indented past the depth-0 parent's title.
        Assert.True(childRow.Text.IndexOf("Child", StringComparison.Ordinal)
                    > parentRow.Text.IndexOf("Parent", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderedRow_Deconstructs_ToTextAndBadges()
    {
        // The positional record struct deconstructs at the call site, matching the host's `var (text,
        // badges) = ...` usage that replaced the old TodoApp.BuildRow.
        var (text, badges) = TaskRowRenderer.Render(FullTask(), BadgeDisplay.Icons, currentUserId: 1);

        Assert.NotEmpty(text);
        Assert.NotEmpty(badges);
    }
}
