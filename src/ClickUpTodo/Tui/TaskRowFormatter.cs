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

    public static Row Format(TaskItem task)
    {
        var hasStatus = !string.IsNullOrWhiteSpace(task.StatusName);
        var badge = hasStatus ? $"[{task.StatusName}]" : "";
        var statusSegment = hasStatus ? $"  {badge}" : "";
        var list = string.IsNullOrWhiteSpace(task.ListName) ? "" : $"  · {task.ListName}";
        var due = task.DueDateMs is { } ms
            ? $"  · due {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MMM d}"
            : "";

        var text = $"{task.Name}{statusSegment}{list}{due}";
        // The badge always sits immediately after the title + two spaces, so its offset is exact
        // even when the title itself contains '[' characters.
        var badgeStart = hasStatus ? task.Name.Length + 2 : -1;
        var badgeLength = hasStatus ? badge.Length : 0;
        return new Row(text, badgeStart, badgeLength);
    }
}
