using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// Pure text formatting for the task detail view (issue #17). Builds the header line and the body of
/// each tab from the domain DTOs, with no Terminal.Gui dependency, so the layout logic is unit-tested
/// while the (untestable) Terminal.Gui glue in <see cref="TaskDetailView"/> stays thin.
/// </summary>
public static class TaskDetailFormatter
{
    /// <summary>Header shown above the tabs: title, then tags and assignees when present.</summary>
    public static string Header(TaskDetail task)
    {
        var sb = new StringBuilder();
        sb.Append(task.Name);
        if (!string.IsNullOrWhiteSpace(task.CustomId))
            sb.Append("  (").Append(task.CustomId).Append(')');
        sb.Append('\n');

        if (task.Tags.Count > 0)
            sb.Append("Tags: ").Append(string.Join(", ", task.Tags)).Append('\n');
        sb.Append("Assignees: ")
          .Append(task.Assignees.Count > 0 ? string.Join(", ", task.Assignees) : "(none)");
        return sb.ToString();
    }

    /// <summary>The Description tab body.</summary>
    public static string Description(TaskDetail task)
        => string.IsNullOrWhiteSpace(task.Description) ? "(no description)" : task.Description!.Trim();

    /// <summary>The Comments tab body: one block per comment, in the order ClickUp returns them.</summary>
    public static string Comments(IReadOnlyList<CommentItem> comments)
    {
        if (comments.Count == 0)
            return "(no comments)";

        var sb = new StringBuilder();
        for (var i = 0; i < comments.Count; i++)
        {
            var c = comments[i];
            if (i > 0)
                sb.Append('\n');
            sb.Append(string.IsNullOrWhiteSpace(c.Author) ? "(unknown)" : c.Author);
            if (c.DateMs is { } ms)
                sb.Append("  ·  ").Append(FormatDate(ms));
            if (c.Resolved)
                sb.Append("  ·  [resolved]");
            sb.Append('\n');
            sb.Append(string.IsNullOrWhiteSpace(c.Text) ? "(empty comment)" : c.Text.Trim());
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The Other-attributes tab body: dates, list, priority, custom fields.</summary>
    public static string OtherAttributes(TaskDetail task)
    {
        var sb = new StringBuilder();
        sb.Append("List:          ").Append(Coalesce(task.ListName)).Append('\n');
        // ClickUp's "Tasks in Multiple Lists": show the full membership only when the task belongs to
        // more than its home list; otherwise the single "List:" line above already covers it.
        var lists = ListMembership(task);
        if (lists.Count > 1)
            sb.Append("Lists:         ").Append(string.Join(", ", lists)).Append('\n');
        sb.Append("Priority:      ").Append(Coalesce(task.Priority)).Append('\n');
        sb.Append("Status:        ").Append(Coalesce(task.StatusName)).Append('\n');
        sb.Append("Created:       ").Append(FormatDateOrDash(task.CreatedMs)).Append('\n');
        sb.Append("Last activity: ").Append(FormatDateOrDash(task.UpdatedMs)).Append('\n');
        sb.Append("Due:           ").Append(FormatDateOrDash(task.DueDateMs)).Append('\n');

        sb.Append('\n').Append("Custom fields:").Append('\n');
        if (task.CustomFields.Count == 0)
            sb.Append("  (none)");
        else
            foreach (var f in task.CustomFields)
            {
                sb.Append("  • ").Append(f.Name);
                if (!string.IsNullOrWhiteSpace(f.Type))
                    sb.Append("  (").Append(f.Type).Append(')');
                sb.Append('\n');
            }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The task's full list membership: the home list unioned with its <c>locations</c>
    /// (ClickUp multiple-lists), home-first and de-duplicated by id (falling back to name when a
    /// location carries no id, so genuinely distinct same-named lists are both kept). Robust to
    /// whether ClickUp includes the home list in <c>locations</c>.
    /// </summary>
    private static IReadOnlyList<string> ListMembership(TaskDetail task)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        void Add(string? id, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            if (!string.IsNullOrEmpty(id))
            {
                if (!seenIds.Add(id))
                    return;
            }
            else if (!seenNames.Add(name!))
            {
                return;
            }
            names.Add(name!);
        }

        Add(task.ListId, task.ListName);
        foreach (var l in task.Lists)
            Add(l.Id, l.Name);
        return names;
    }

    private static string Coalesce(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value!;

    private static string FormatDateOrDash(long? ms) => ms is { } v ? FormatDate(v) : "—";

    private static string FormatDate(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MMM d, yyyy HH:mm");
}
