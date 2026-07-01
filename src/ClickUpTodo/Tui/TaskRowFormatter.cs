using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// Builds the one-line display text for a task row and reports where the <c>[status]</c> badge
/// sits within it, so the list renderer can color exactly that span. Pure (no Terminal.Gui), so
/// the layout and the badge span are unit-testable.
/// </summary>
public static class TaskRowFormatter
{
    /// <summary>
    /// The display line plus the character span of the <c>[status]</c> badge (brackets included).
    /// <paramref name="Text"/> leads with the title so the ListView's type-ahead matches titles.
    /// When the task has no status, <paramref name="BadgeLength"/> is 0 and <paramref name="BadgeStart"/> is -1.
    /// </summary>
    public readonly record struct Row(string Text, int BadgeStart, int BadgeLength);

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
        var hasStatus = !string.IsNullOrWhiteSpace(task.StatusName);
        var badge = hasStatus ? $"[{task.StatusName}]" : "";
        var statusSegment = hasStatus ? $"  {badge}" : "";
        var list = string.IsNullOrWhiteSpace(task.ListName) ? "" : $"  · {task.ListName}";
        var due = task.DueDateMs is { } ms
            ? $"  · due {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MMM d}"
            : "";
        var marker = isContextParent ? ContextParentMarker : "";

        var text = $"{indent}{task.Name}{statusSegment}{list}{due}{marker}";
        // The badge sits immediately after the indent + title + two spaces, so its offset is exact
        // even when the title itself contains '[' characters.
        var badgeStart = hasStatus ? indent.Length + task.Name.Length + 2 : -1;
        var badgeLength = hasStatus ? badge.Length : 0;
        return new Row(text, badgeStart, badgeLength);
    }
}
