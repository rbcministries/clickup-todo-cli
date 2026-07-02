using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// Builds the one-line display text for a task row and reports where the <c>[status]</c> and
/// <c>[priority]</c> badges sit within it, so the list renderer can color exactly those spans. Pure
/// (no Terminal.Gui), so the layout and the badge spans are unit-testable.
/// </summary>
public static class TaskRowFormatter
{
    /// <summary>
    /// The display line plus the character spans of the <c>[status]</c> and <c>[priority]</c> badges
    /// (brackets included). <paramref name="Text"/> leads with the title so the ListView's type-ahead
    /// matches titles. When a badge is absent its <c>*Length</c> is 0 and its <c>*Start</c> is -1.
    /// </summary>
    public readonly record struct Row(
        string Text, int StatusStart, int StatusLength, int PriorityStart, int PriorityLength);

    /// <summary>Two spaces of indent per nesting level in the F4 subtasks view (#46).</summary>
    private const string IndentUnit = "  ";

    /// <summary>Trailing marker on a parent shown only as context (its subtask is assigned to me, it isn't).</summary>
    private const string ContextParentMarker = "  · (parent — not assigned to you)";

    /// <summary>
    /// Formats a task row, optionally indented for the nested subtasks view.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="depth">Nesting depth; 0 = top level. Each level adds one indent unit.</param>
    /// <param name="isContextParent">
    /// True when the task is a parent pulled in purely as a header (not assigned to the user); appends
    /// a marker so it reads as context rather than actionable work.
    /// </param>
    public static Row Format(TaskItem task, int depth = 0, bool isContextParent = false)
    {
        var indent = depth > 0 ? string.Concat(Enumerable.Repeat(IndentUnit, depth)) : "";

        // Build the line incrementally, capturing each badge's offset from the running length. This
        // keeps the spans exact regardless of indent, the title's own '[' characters, or which badges
        // are present — two coloured badges make hand-computed offsets fragile.
        var text = indent + task.Name;

        var (statusStart, statusLength) = AppendBadge(ref text, task.StatusName);
        var (priorityStart, priorityLength) = AppendBadge(ref text, task.PriorityName);

        if (!string.IsNullOrWhiteSpace(task.ListName))
            text += $"  · {task.ListName}";
        if (task.DueDateMs is { } ms)
            text += $"  · due {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MMM d}";
        if (isContextParent)
            text += ContextParentMarker;

        return new Row(text, statusStart, statusLength, priorityStart, priorityLength);
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
