using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui;

/// <summary>
/// Builds the one-line display text for a task row and reports where the Status and Priority badges
/// sit within it, so the list renderer can color exactly those spans. A single Status badge and a
/// single Priority badge lead every row (Status first), rendered per the active
/// <see cref="BadgeDisplay"/>: compact <c>○</c>/<c>⚑</c> icon chips, bracketed <c>[status]</c>/
/// <c>[priority]</c> text, or nothing. Pure (no Terminal.Gui), so the layout and the badge spans are
/// unit-testable.
/// </summary>
public static class TaskRowFormatter
{
    /// <summary>
    /// The display line plus the character spans of the leading Status and Priority badges. Both badges
    /// lead the row (Status first), ahead of the title — so <paramref name="Text"/> no longer starts with
    /// the title; the ListView's type-ahead searches the decoupled title-only keys instead (#76). When a
    /// badge is absent (unset, grouped away, or hidden) its <c>*Length</c> is 0 and its <c>*Start</c> is -1.
    /// </summary>
    public readonly record struct Row(
        string Text,
        int StatusStart, int StatusLength,
        int PriorityStart, int PriorityLength);

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
    /// How the leading Status/Priority badges render (F6): icon chips, bracketed text, or hidden.
    /// </param>
    public static Row Format(
        TaskItem task, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null,
        string marker = "", bool isForeignSubtask = false, BadgeDisplay badges = BadgeDisplay.Icons)
    {
        var indent = depth > 0 ? string.Concat(Enumerable.Repeat(IndentUnit, depth)) : "";

        // A field's badge is shown unless the list is grouped by it — its group header already conveys
        // it (#67) — and, of course, unless there's no value to show.
        var showStatus = groupedBy != TaskField.Status;
        var showPriority = groupedBy != TaskField.Priority;
        var hasStatus = showStatus && !string.IsNullOrWhiteSpace(task.StatusName);
        var hasPriority = showPriority && !string.IsNullOrWhiteSpace(task.PriorityName);

        // Build the line incrementally, capturing each badge's offset from the running length. This
        // keeps the spans exact regardless of indent, the marker, the title's own '[' characters, or
        // which badges are present — several coloured spans make hand-computed offsets fragile.
        var text = "";
        var (statusStart, statusLength) = (-1, 0);
        var (priorityStart, priorityLength) = (-1, 0);

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

        text += indent + marker + task.Name;

        if (groupedBy != TaskField.List && !string.IsNullOrWhiteSpace(task.ListName))
            text += $"  · {task.ListName}";
        if (groupedBy != TaskField.Due && task.DueDateMs is { } ms)
            text += $"  · due {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MMM d}";
        if (isContextParent)
            text += ContextParentMarker;
        else if (isForeignSubtask)
            text += ForeignSubtaskMarker;

        return new Row(text, statusStart, statusLength, priorityStart, priorityLength);
    }

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
}
