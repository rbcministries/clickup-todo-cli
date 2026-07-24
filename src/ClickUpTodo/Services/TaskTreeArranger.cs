using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>A row of the Task Tree tab (#291): the <paramref name="Task"/>, its indent
/// <paramref name="Depth"/> (0 = the top-most ancestor, or the current task itself when it has no
/// ancestry), and whether it is the task the detail screen is currently showing
/// (<paramref name="IsCurrent"/> — the row that navigation treats as a no-op).</summary>
public readonly record struct TaskTreeRow(TaskItem Task, int Depth, bool IsCurrent);

/// <summary>
/// Assembles the current task's <b>ancestry chain + the task itself + its descendants</b> into one
/// ordered, indented list of <see cref="TaskTreeRow"/> for the Task Tree tab (F, #291). Pure (no
/// Terminal.Gui, no I/O) so the tree shape is unit-testable; the fetching lives in
/// <see cref="TaskService.GetTaskTreeAsync"/>.
/// <para>
/// The nesting itself is delegated to the already-tested <see cref="SubtaskArranger.Arrange"/> rather
/// than re-derived: the ancestors (each the parent of the next), the current task, and its descendants
/// all carry a consistent <see cref="TaskItem.ParentId"/>, so feeding them as one ordered list —
/// top-most ancestor first — makes <see cref="SubtaskArranger"/> emit the whole chain from its single
/// top-level anchor, indenting the current task under its nearest ancestor and each descendant under its
/// own parent. Every parent is treated as expanded (the tab shows the full tree; folding is out of
/// scope for #291) and there are no context parents or suppressed rows.
/// </para>
/// </summary>
public static class TaskTreeArranger
{
    /// <param name="currentTaskId">The id of the task the detail screen is showing; the matching row is
    /// flagged <see cref="TaskTreeRow.IsCurrent"/>.</param>
    /// <param name="ancestorsTopDown">The parent chain above the current task, <b>top-most first</b>
    /// (the current task's direct parent last). Empty when the current task is top-level.</param>
    /// <param name="current">The current task itself.</param>
    /// <param name="descendants">The current task's descendants (subtasks, recursively), in any order —
    /// each carries its own <see cref="TaskItem.ParentId"/> so nesting is by parentage, not input order.</param>
    public static IReadOnlyList<TaskTreeRow> Build(
        string currentTaskId,
        IReadOnlyList<TaskItem> ancestorsTopDown,
        TaskItem current,
        IReadOnlyList<TaskItem> descendants)
    {
        // One ordered list: ancestors (top-most first) → the current task → its descendants. De-dupe by
        // id defensively so a task that shows up in more than one bucket (e.g. a fetch that echoed the
        // current task back as a "subtask", or a cyclic parent link) is arranged once, keeping the first
        // occurrence's position. SubtaskArranger is itself cycle-safe, but a duplicate row would still
        // read as a doubled entry, so we drop it here.
        var ordered = new List<TaskItem>(ancestorsTopDown.Count + 1 + descendants.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in ancestorsTopDown)
            if (seen.Add(t.Id))
                ordered.Add(t);
        if (seen.Add(current.Id))
            ordered.Add(current);
        foreach (var t in descendants)
            if (seen.Add(t.Id))
                ordered.Add(t);

        var arranged = SubtaskArranger.Arrange(
            ordered,
            contextParents: EmptyContextParents,
            expanded: null,          // null ⇒ every parent expanded (show the whole tree)
            suppressTopLevel: null); // no foreign-orphan suppression in the single-task tree

        var rows = new List<TaskTreeRow>(arranged.Count);
        foreach (var row in arranged)
            rows.Add(new TaskTreeRow(row.Task, row.Depth, string.Equals(row.Task.Id, currentTaskId, StringComparison.Ordinal)));
        return rows;
    }

    private static readonly IReadOnlyDictionary<string, TaskItem> EmptyContextParents =
        new Dictionary<string, TaskItem>(StringComparer.Ordinal);
}
