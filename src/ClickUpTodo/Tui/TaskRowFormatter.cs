using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui;

/// <summary>
/// Builds the one-line display text for a task row and reports where the Status and Priority badges
/// sit within it, so the list renderer can color exactly those spans. A single Status badge and a
/// single Priority badge lead every row (Status first), rendered per the active
/// <see cref="BadgeDisplay"/>: compact <c>○</c>/<c>⚑</c> icon chips, bracketed <c>[status]</c>/
/// <c>[priority]</c> text, or nothing. When badges show (icon or text), a task-identifier chip — the
/// Space's custom id, or the plain task id as a fallback — follows the badges before the title. Pure
/// (no Terminal.Gui), so the layout and the badge spans are unit-testable.
/// </summary>
public static class TaskRowFormatter
{
    /// <summary>
    /// The display line plus the character spans of the leading Status and Priority badges, the leading
    /// custom-id (or fallback task-id) chip, and the trailing Assignees badge (#161). The Status/Priority
    /// badges lead the row (Status first), then the id chip, ahead of the title — so <paramref name="Text"/>
    /// no longer starts with the title; the ListView's type-ahead searches the decoupled title-only keys
    /// instead (#76). The Assignees badge trails the title/metadata. When a badge is absent (unset, grouped
    /// away, or hidden) its <c>*Length</c> is 0 and its <c>*Start</c> is -1.
    /// </summary>
    public readonly record struct Row(
        string Text,
        int StatusStart, int StatusLength,
        int PriorityStart, int PriorityLength,
        int CustomIdStart, int CustomIdLength,
        int AssigneesStart, int AssigneesLength);

    /// <summary>Two spaces of indent per nesting level in the F4 subtasks view (#46).</summary>
    private const string IndentUnit = "  ";

    /// <summary>
    /// The Status icon chip: a <c>○</c> glyph flanked by a space on each side (coloured with the
    /// status's background by the renderer). The glyph is intentionally a single display column so the
    /// chip and <see cref="BlankGutter"/> occupy the same three columns, giving a grid-like left gutter.
    /// </summary>
    public const string StatusIcon = " ○ ";

    /// <summary>The Priority icon chip: a <c>⚑</c> glyph flanked by a space on each side (coloured with
    /// the priority's background). Same fixed three-column width as <see cref="StatusIcon"/>.</summary>
    public const string PriorityIcon = " ⚑ ";

    /// <summary>The blank gutter used, in icon mode, when a badge is absent — same width as an icon chip
    /// so titles still line up across rows.</summary>
    public const string BlankGutter = "   ";

    /// <summary>The trailing Assignees icon chip (#161): a <c>👥</c> glyph flanked by a space on each
    /// side, coloured with a fixed white background by the renderer. Shown, in icon mode, when a task
    /// carries an assignee other than the current user — surfacing shared/delegated work at a glance.
    /// This is a trailing badge, so (unlike the leading Status/Priority chips) it needs no alignment
    /// gutter and is simply omitted when absent.</summary>
    public const string AssigneesIcon = " 👥 ";

    /// <summary>Trailing marker on a parent shown only as context (its subtask is assigned to me, it isn't).</summary>
    private const string ContextParentMarker = "  · (parent — not assigned to you)";

    /// <summary>Trailing marker on a subtask pulled in under my parent that isn't assigned to me (#70).</summary>
    private const string ForeignSubtaskMarker = "  · (not assigned to you)";

    /// <summary>
    /// Formats a task row, optionally indented for the nested subtasks view.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="depth">Nesting depth; 0 = top level. Each level adds one indent unit.</param>
    /// <param name="isContextParent">
    /// True when the task is a parent pulled in purely as a header (not assigned to the user); appends
    /// a marker so it reads as context rather than actionable work.
    /// </param>
    /// <param name="isForeignSubtask">
    /// True when the task is a subtask pulled in under my parent that isn't assigned to me (#70);
    /// appends a not-mine marker so it reads as context rather than my actionable work.
    /// </param>
    /// <param name="groupedBy">
    /// The active F3 group field, or null when ungrouped. When set, the segment for that field is
    /// omitted from the row because the group header above already conveys it (#67): Status/Priority
    /// drop their badge (reporting the absent sentinel so no colour span is drawn), List drops
    /// <c>· {list}</c>, Due drops <c>· due {date}</c>; Created/LastActivity have no row segment so
    /// grouping by them changes nothing.
    /// </param>
    /// <param name="marker">
    /// A fold marker prefix (#76) — e.g. <c>"▶ "</c>/<c>"▼ "</c> or a blank gutter — inserted right after
    /// the badges and indent, before the title. Default <c>""</c> leaves the row untouched (pre-#76
    /// layout). Because the badge offsets below are captured from the running text length, the marker is
    /// accounted for automatically and the colour spans stay exact.
    /// </param>
    /// <param name="badges">
    /// How the badges render (F6): icon chips, text, or hidden. The trailing Assignees badge (#161)
    /// folds into this same cycle — a 👥 chip in <see cref="BadgeDisplay.Icons"/>, the assignees'
    /// names in <see cref="BadgeDisplay.Text"/>, nothing when <see cref="BadgeDisplay.Hidden"/>.
    /// </param>
    /// <param name="currentUserId">
    /// The signed-in user's ClickUp id, used to decide the trailing Assignees badge (#161): the badge
    /// shows only when the task has an assignee whose id differs from this. When null (unknown user)
    /// every assignee counts as "other". Grouping by <see cref="TaskField.Assignee"/> drops the badge,
    /// mirroring how Status/Priority drop when grouped by their field (#67).
    /// </param>
    public static Row Format(
        TaskItem task, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null,
        string marker = "", bool isForeignSubtask = false, BadgeDisplay badges = BadgeDisplay.Icons,
        long? currentUserId = null)
    {
        var indent = depth > 0 ? string.Concat(Enumerable.Repeat(IndentUnit, depth)) : "";

        // A field's badge is shown unless the list is grouped by it — its group header already conveys
        // it (#67) — and, of course, unless there's no value to show.
        var showStatus = groupedBy != TaskField.Status;
        var showPriority = groupedBy != TaskField.Priority;
        var hasStatus = showStatus && !string.IsNullOrWhiteSpace(task.StatusName);
        var hasPriority = showPriority && !string.IsNullOrWhiteSpace(task.PriorityName);

        // The trailing Assignees badge (#161) shows when someone other than the current user is on the
        // task (whether or not the current user is too). It's dropped when grouped by Assignee — the
        // group header already conveys it (#67, same as Status/Priority). A null current user means the
        // signed-in id is unknown, so every assignee counts as "other".
        var otherAssignees = task.Assignees
            .Where(a => currentUserId is not { } uid || a.Id != uid)
            .ToList();
        var hasOtherAssignees = groupedBy != TaskField.Assignee && otherAssignees.Count > 0;

        // Build the line incrementally, capturing each badge's offset from the running length. This
        // keeps the spans exact regardless of indent, the marker, the title's own '[' characters, or
        // which badges are present — several coloured spans make hand-computed offsets fragile.
        var text = "";
        var (statusStart, statusLength) = (-1, 0);
        var (priorityStart, priorityLength) = (-1, 0);
        var (customIdStart, customIdLength) = (-1, 0);
        var (assigneesStart, assigneesLength) = (-1, 0);

        switch (badges)
        {
            case BadgeDisplay.Icons:
                // Fixed-width chips form a grid-like left gutter (Status first, then Priority). A present
                // badge is a coloured glyph chip; an absent-but-not-grouped badge is a blank chip so titles
                // still line up; a grouped-away badge is dropped entirely (every row drops it uniformly, so
                // the columns still align).
                (statusStart, statusLength) = AppendIconChip(ref text, showStatus, hasStatus, StatusIcon);
                (priorityStart, priorityLength) = AppendIconChip(ref text, showPriority, hasPriority, PriorityIcon);
                break;
            case BadgeDisplay.Text:
                // Bracketed text badges (Status first), each followed by a space. Absent badges are simply
                // omitted — text mode is inherently ragged, so there's no alignment gutter.
                (statusStart, statusLength) = AppendTextBadge(ref text, hasStatus, task.StatusName);
                (priorityStart, priorityLength) = AppendTextBadge(ref text, hasPriority, task.PriorityName);
                break;
            case BadgeDisplay.Hidden:
                // No badges — the row leads straight into the indent/marker/title.
                break;
        }

        // The task-identifier chip rides with the badges (skipped in Hidden mode, which is a
        // decoration-free view): the Space's custom id, or the plain task id when the Space has no
        // custom ids. It follows the Status/Priority gutter and precedes the indent/marker/title.
        if (badges != BadgeDisplay.Hidden)
            (customIdStart, customIdLength) = AppendCustomId(ref text, CustomIdOf(task));

        text += indent + marker + task.Name;

        if (groupedBy != TaskField.List && !string.IsNullOrWhiteSpace(task.ListName))
            text += $"  · {task.ListName}";
        if (groupedBy != TaskField.Due && task.DueDateMs is { } ms)
            text += $"  · due {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MMM d}";

        // The trailing Assignees badge follows the list/due segments but precedes the context/foreign
        // markers, so those parentheticals still read last. Hidden mode appends nothing.
        (assigneesStart, assigneesLength) = AppendAssigneesBadge(ref text, badges, hasOtherAssignees, otherAssignees);

        if (isContextParent)
            text += ContextParentMarker;
        else if (isForeignSubtask)
            text += ForeignSubtaskMarker;

        return new Row(
            text, statusStart, statusLength, priorityStart, priorityLength,
            customIdStart, customIdLength, assigneesStart, assigneesLength);
    }

    /// <summary>The identifier shown on the row: the task's Space-defined custom id when set, else its
    /// plain ClickUp id (#161-style fallback shared with the agent prompt composer and detail header).</summary>
    private static string CustomIdOf(TaskItem task)
        => string.IsNullOrWhiteSpace(task.CustomId) ? task.Id : task.CustomId!;

    /// <summary>
    /// Appends a fixed-width icon chip to <paramref name="text"/>, returning the coloured span of the
    /// glyph chip. A grouped-away badge (<paramref name="show"/> false) appends nothing; an absent one
    /// appends a <see cref="BlankGutter"/> for alignment; both report the <c>(-1, 0)</c> "no span"
    /// sentinel so no colour is drawn.
    /// </summary>
    private static (int Start, int Length) AppendIconChip(ref string text, bool show, bool has, string glyph)
    {
        if (!show)
            return (-1, 0);
        if (!has)
        {
            text += BlankGutter;
            return (-1, 0);
        }
        var start = text.Length;
        text += glyph;
        return (start, glyph.Length);
    }

    /// <summary>
    /// Appends <c>"[label] "</c> to <paramref name="text"/> when the badge is present, returning the
    /// char span (start, length) of the <c>[label]</c> bracket (the trailing separator space is excluded
    /// from the coloured span). Returns <c>(-1, 0)</c> — the "no badge" sentinel — otherwise, leaving
    /// <paramref name="text"/> untouched.
    /// </summary>
    private static (int Start, int Length) AppendTextBadge(ref string text, bool has, string? label)
    {
        if (!has)
            return (-1, 0);
        var start = text.Length;
        var badge = $"[{label}]";
        text += badge + " ";
        return (start, badge.Length);
    }

    /// <summary>
    /// Appends the task-identifier chip — the custom id, or the plain task id as a fallback — after the
    /// leading Status/Priority gutter and before the title, returning the char span of the id (the
    /// trailing separator space excluded, like <see cref="AppendTextBadge"/>). Like the text status
    /// badge, it's inherently ragged: custom-id formats vary by Space, so their widths are nonstandard
    /// and no gutter tries to align the title across rows — the same raggedness variable-length status
    /// names already have in text mode. Returns the <c>(-1, 0)</c> "no id" sentinel when there's nothing
    /// to show (a task with neither id).
    /// </summary>
    private static (int Start, int Length) AppendCustomId(ref string text, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return (-1, 0);
        var start = text.Length;
        text += id + " ";
        return (start, id.Length);
    }

    /// <summary>
    /// Appends the trailing Assignees badge (#161) when <paramref name="show"/> and the mode isn't
    /// <see cref="BadgeDisplay.Hidden"/>: a two-space separator (uncoloured, matching the <c>  · …</c>
    /// trailing segments) followed by a white-background chip — the <see cref="AssigneesIcon"/> glyph
    /// in <see cref="BadgeDisplay.Icons"/> mode, or the space-padded, comma-joined assignee names in
    /// <see cref="BadgeDisplay.Text"/> mode. Returns the char span of the coloured chip (its padding
    /// included), or the <c>(-1, 0)</c> "no badge" sentinel when nothing is appended.
    /// </summary>
    private static (int Start, int Length) AppendAssigneesBadge(
        ref string text, BadgeDisplay badges, bool show, IReadOnlyList<TaskAssignee> others)
    {
        if (!show || badges == BadgeDisplay.Hidden)
            return (-1, 0);

        var chip = badges == BadgeDisplay.Text
            ? $" {string.Join(", ", others.Select(a => a.Name))} "
            : AssigneesIcon;
        text += "  "; // separator (uncoloured), like the other trailing segments
        var start = text.Length;
        text += chip;
        return (start, chip.Length);
    }
}
