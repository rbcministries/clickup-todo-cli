using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui;

/// <summary>
/// Renders a task list row in one call: the display text <b>and</b> its colour badge spans, folding
/// together the currently-split <see cref="TaskRowFormatter.Format"/> (pure text + char spans) and the
/// per-span colour overlay (which hex colour tints each span). A single consumer — the main list today,
/// the Task Tree tab (#291) tomorrow — needs only <see cref="Render"/> to produce rows byte-for-byte
/// identical to the main list, rather than forking the "which colour goes on which span" knowledge that
/// previously lived inside <see cref="TodoApp"/> (#284).
/// <para>
/// Pure and host-independent: it takes a <see cref="TaskItem"/> plus the row's depth/fold-marker/badge
/// mode as arguments, so a caller with an arbitrary set of ancestry/child rows can drive it without any
/// dependency on <see cref="TodoApp"/>'s row arrays. The colour math and the
/// <see cref="StatusBadgeListSource.Badge"/> contract stay on <see cref="StatusBadgeListSource"/> — this
/// component composes <see cref="StatusBadgeListSource.TryCreate"/> exactly as the host did.
/// </para>
/// </summary>
public static class TaskRowRenderer
{
    /// <summary>Fixed white background for the trailing assignees badge (#161) — not tinted by a
    /// ClickUp field colour like Status/Priority; the readable dark foreground follows from
    /// <see cref="StatusBadgeColor.PreferDarkText"/> (black on white).</summary>
    public const string AssigneesBadgeColor = "ffffff";

    /// <summary>Fixed muted-gray background for the leading custom-id (or fallback task-id) chip — a
    /// neutral identifier tint, deliberately not a ClickUp field colour, so the id reads as metadata
    /// beside the Status/Priority badges rather than as another status. The light foreground follows
    /// from <see cref="StatusBadgeColor.PreferDarkText"/> (white on dark gray).</summary>
    public const string CustomIdBadgeColor = "5a5a5a";

    /// <summary>The rendered row: the display line, its zero-or-more colour badge spans (ready to feed a
    /// <see cref="StatusBadgeListSource"/> — parallel <c>_display</c>/<c>_badges</c> entries), and the
    /// char span of the leading fold marker within <c>Text</c> (<c>(-1, 0)</c> when none), so a mouse
    /// hit-test can resolve the arrow column (#287). A positional record struct, so it deconstructs to
    /// <c>(text, badges, markerStart, markerLength)</c> at the call site.</summary>
    public readonly record struct RenderedRow(
        string Text, IReadOnlyList<StatusBadgeListSource.Badge> Badges,
        int MarkerStart, int MarkerLength);

    /// <summary>
    /// Formats a task row and pairs each badge span with its colour. The text and spans come from
    /// <see cref="TaskRowFormatter.Format"/>; each present span is coloured — Status by
    /// <see cref="TaskItem.StatusColor"/>, Priority by <see cref="TaskItem.PriorityColor"/>, the leading
    /// custom-id/task-id chip by the fixed <see cref="CustomIdBadgeColor"/>, and the trailing assignees
    /// badge by the fixed <see cref="AssigneesBadgeColor"/> (#161). An absent, grouped-away, or hidden
    /// badge carries no span, so <see cref="StatusBadgeListSource.TryCreate"/> returns null and nothing
    /// is shaded.
    /// </summary>
    /// <param name="task">The task to render.</param>
    /// <param name="badgeDisplay">How badges render (F6): icon chips, text, or hidden.</param>
    /// <param name="currentUserId">The signed-in user's ClickUp id (or null when unknown), deciding the
    /// trailing Assignees badge — shown when a non-current user is assigned (#161).</param>
    /// <param name="depth">Nesting depth for the subtasks view; 0 = top level (#46).</param>
    /// <param name="isContextParent">Parent pulled in purely as a header, not assigned to the user (#46).</param>
    /// <param name="groupedBy">The active F3 group field, or null when ungrouped; its segment is omitted
    /// because the group header already conveys it (#67).</param>
    /// <param name="marker">The leading ▶/▼ fold marker or gutter (#76).</param>
    /// <param name="isForeignSubtask">Subtask pulled in under my parent that isn't assigned to me (#70).</param>
    /// <param name="isUnassignedSubtask">Pulled-in subtask with no assignee, shown in the F4
    /// "mine + unassigned" state (#179). Takes precedence over <paramref name="isForeignSubtask"/>.</param>
    public static RenderedRow Render(
        TaskItem task, BadgeDisplay badgeDisplay, long? currentUserId, int depth = 0,
        bool isContextParent = false, TaskField? groupedBy = null, string marker = "",
        bool isForeignSubtask = false, bool isUnassignedSubtask = false)
    {
        var row = TaskRowFormatter.Format(task, depth, isContextParent, groupedBy, marker, isForeignSubtask, badgeDisplay, currentUserId, isUnassignedSubtask);
        var badges = new List<StatusBadgeListSource.Badge>(4);
        // The Status/Priority badges (icon chip or bracketed text) are tinted with their field colours;
        // an absent/hidden badge carries no span, so TryCreate returns null and nothing is shaded.
        if (StatusBadgeListSource.TryCreate(row.StatusStart, row.StatusLength, task.StatusColor) is { } status)
            badges.Add(status);
        if (StatusBadgeListSource.TryCreate(row.PriorityStart, row.PriorityLength, task.PriorityColor) is { } priority)
            badges.Add(priority);
        // The leading custom-id (or fallback task-id) chip is muted-gray, not field-tinted; a hidden-mode
        // row carries no span, so TryCreate returns null and nothing is shaded.
        if (StatusBadgeListSource.TryCreate(row.CustomIdStart, row.CustomIdLength, CustomIdBadgeColor) is { } customId)
            badges.Add(customId);
        // The trailing assignees badge (#161) is white-backed, not field-tinted; the same absent/hidden
        // span sentinel makes TryCreate return null so nothing is shaded when it's not shown.
        if (StatusBadgeListSource.TryCreate(row.AssigneesStart, row.AssigneesLength, AssigneesBadgeColor) is { } assignees)
            badges.Add(assignees);
        return new RenderedRow(row.Text, badges, row.MarkerStart, row.MarkerLength);
    }
}
