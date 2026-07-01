using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// A planned row in the to-do list section: either a header (<see cref="Task"/> is null) or a task
/// row carrying its nesting <paramref name="Depth"/> and whether it's a context parent (a parent not
/// assigned to the user, shown only so its subtask can nest — see <see cref="SubtaskArranger"/>).
/// </summary>
public readonly record struct LayoutRow(string? HeaderText, TaskItem? Task, int Depth, bool IsContextParent)
{
    public bool IsHeader => Task is null;
}

/// <summary>
/// Lays out the to-do section (headers + task rows) for the dashboard's single sectioned list. Pure
/// (no Terminal.Gui) so the interplay of F3 grouping and the F4 nested-subtasks view is unit-testable;
/// <see cref="Tui.TodoApp"/> just materialises the returned <see cref="LayoutRow"/>s into its
/// <c>_rows</c>/<c>_display</c>/<c>_badges</c>/<c>_depths</c> arrays.
/// <para>
/// Grouping and nesting <b>compose</b> (#57): when <paramref name="nest"/> is on, each group is
/// arranged independently via <see cref="SubtaskArranger.Arrange"/>, so a subtask nests under its
/// parent when both fall in the same group; a subtask whose parent lands in a different group renders
/// flat within its own group; and a not-assigned context parent is injected as a header wherever its
/// children appear (it has no group value of its own, so it rides along with them).
/// </para>
/// </summary>
public static class SectionLayout
{
    /// <param name="groups">The non-pinned tasks, already filtered/sorted/grouped by <see cref="TaskView.Apply"/>.</param>
    /// <param name="contextParents">Not-in-snapshot parents to inject as headers (empty to disable).</param>
    /// <param name="grouped">True when an F3 group field is active (a header precedes each group).</param>
    /// <param name="nest">True when the F4 subtasks view is on (arrange each group's subtasks under their parent).</param>
    /// <param name="ungroupedTasksHeader">
    /// The single tasks-section header to emit when <b>not</b> grouped (used only to separate the
    /// to-do rows from a pinned section above); pass null to omit it.
    /// </param>
    public static IReadOnlyList<LayoutRow> BuildTodoSection(
        IReadOnlyList<TaskGroup> groups,
        IReadOnlyDictionary<string, TaskItem> contextParents,
        bool grouped,
        bool nest,
        string? ungroupedTasksHeader)
    {
        var rows = new List<LayoutRow>();
        foreach (var group in groups)
        {
            // A header per named group when grouping; otherwise a single tasks header, and only when a
            // pinned section sits above (signalled by a non-null ungroupedTasksHeader). Header counts use
            // the real group members — an injected context parent is a header, not a counted task row.
            if (grouped)
                rows.Add(new LayoutRow($"─ {(group.Label ?? "").ToUpperInvariant()} ({group.Tasks.Count}) ─", null, 0, false));
            else if (ungroupedTasksHeader is not null)
                rows.Add(new LayoutRow(ungroupedTasksHeader, null, 0, false));

            if (nest)
                foreach (var row in SubtaskArranger.Arrange(group.Tasks, contextParents))
                    rows.Add(new LayoutRow(null, row.Task, row.Depth, row.IsContextParent));
            else
                foreach (var t in group.Tasks)
                    rows.Add(new LayoutRow(null, t, 0, false));
        }
        return rows;
    }
}
