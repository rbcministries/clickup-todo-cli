using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui;

/// <summary>
/// Builds the one-line display text for a task row and reports where the <c>[status]</c> and
/// <c>[priority]</c> badges sit within it, so the list renderer can color exactly those spans. Pure
/// (no Terminal.Gui), so the layout and the badge spans are unit-testable.
/// </summary>
public static class TaskRowFormatter
{
    /// <summary>
    /// The display line plus the character spans of the leading priority flag and the <c>[status]</c>
    /// and <c>[priority]</c> badges (brackets included). Every row now leads with the fixed-width
    /// priority gutter (see <see cref="LeadingFlag"/>), so <paramref name="Text"/> no longer starts with
    /// the title — the ListView's type-ahead searches the decoupled title-only keys instead (#76). When a
    /// badge/flag is absent its <c>*Length</c> is 0 and its <c>*Start</c> is -1.
    /// </summary>
    public readonly record struct Row(
        string Text,
        int StatusStart, int StatusLength,
        int PriorityStart, int PriorityLength,
        int PriorityFlagStart, int PriorityFlagLength);

    /// <summary>Two spaces of indent per nesting level in the F4 subtasks view (#46).</summary>
    private const string IndentUnit = "  ";

    /// <summary>
    /// The leading priority badge every task row starts with: a flag glyph flanked by a space on each
    /// side (coloured with the priority's background by the renderer) when a priority is set, or three
    /// blank spaces (no colour span) when it isn't. Keeping both states the same width gives the list a
    /// grid-like left gutter. The glyph is intentionally a single display column so <c>" ⚑ "</c> and
    /// <c>"   "</c> occupy the same three columns.
    /// </summary>
    public const string LeadingFlag = " ⚑ ";

    /// <summary>The blank gutter used when a task has no priority — same width as <see cref="LeadingFlag"/>.</summary>
    public const string LeadingBlank = "   ";

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
    /// the indent, before the title. Default <c>""</c> leaves the row untouched (pre-#76 layout). Because
    /// the badge offsets below are captured from the running text length, the marker is accounted for
    /// automatically and the colour spans stay exact.
    /// </param>
    public static Row Format(TaskItem task, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null, string marker = "", bool isForeignSubtask = false)
    {
        var indent = depth > 0 ? string.Concat(Enumerable.Repeat(IndentUnit, depth)) : "";

        // Every row leads with a fixed-width priority gutter: a coloured flag when a priority is set,
        // else three blank spaces so undated rows still line up (a grid-like left edge). It sits ahead
        // of the indent/marker so the flags share one column regardless of nesting depth. The flag stays
        // even when grouped by priority — it's structural padding, not the (droppable) inline badge.
        var hasPriority = !string.IsNullOrWhiteSpace(task.PriorityName);
        var (flagStart, flagLength) = hasPriority ? (0, LeadingFlag.Length) : (-1, 0);
        var leading = hasPriority ? LeadingFlag : LeadingBlank;

        // Build the line incrementally, capturing each badge's offset from the running length. This
        // keeps the spans exact regardless of indent, the marker, the title's own '[' characters, or
        // which badges are present — several coloured spans make hand-computed offsets fragile.
        var text = leading + indent + marker + task.Name;

        var (statusStart, statusLength) = groupedBy == TaskField.Status
            ? (-1, 0)
            : AppendBadge(ref text, task.StatusName);
        var (priorityStart, priorityLength) = groupedBy == TaskField.Priority
            ? (-1, 0)
            : AppendBadge(ref text, task.PriorityName);

        if (groupedBy != TaskField.List && !string.IsNullOrWhiteSpace(task.ListName))
            text += $"  · {task.ListName}";
        if (groupedBy != TaskField.Due && task.DueDateMs is { } ms)
            text += $"  · due {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MMM d}";
        if (isContextParent)
            text += ContextParentMarker;
        else if (isForeignSubtask)
            text += ForeignSubtaskMarker;

        return new Row(text, statusStart, statusLength, priorityStart, priorityLength, flagStart, flagLength);
    }

    /// <summary>
    /// Appends <c>"  [label]"</c> to <paramref name="text"/> when the label is non-blank, returning the
    /// char span (start, length) of the <c>[label]</c> bracket. Returns <c>(-1, 0)</c> — the "no badge"
    /// sentinel — when the label is absent, leaving <paramref name="text"/> untouched.
    /// </summary>
    private static (int Start, int Length) AppendBadge(ref string text, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return (-1, 0);
        text += "  ";
        var start = text.Length;
        var badge = $"[{label}]";
        text += badge;
        return (start, badge.Length);
    }
}
