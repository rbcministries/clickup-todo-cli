namespace ClickUpTodo.Services;

/// <summary>What deleting a highlighted Task Tree row (F, #594) means for navigation — the two cases the
/// tree's shape distinguishes.</summary>
public enum TaskTreeDeleteKind
{
    /// <summary>The highlighted row is the task the detail screen is showing (<see cref="TaskTreeRow.IsCurrent"/>).
    /// Deleting it removes the view's own subject, so the host navigates away (pop / quit) per the accepted
    /// navigation ADR (<c>docs/navigation-model.md</c>).</summary>
    Current,

    /// <summary>The highlighted row is a descendant subtask (a row below the current task in the tree).
    /// Deleting it leaves the view's subject intact, so its row + subtree are removed in place.</summary>
    Subtask,
}

/// <summary>The resolved target of a Task Tree <c>Delete</c> (#594): the task id + name to delete and how its
/// deletion is handled.</summary>
public readonly record struct TaskTreeDeleteTarget(string TaskId, string Name, TaskTreeDeleteKind Kind);

/// <summary>
/// Pure helpers for the Task Tree tab's contextual <c>Delete</c> (F, #594) — classifying the highlighted row
/// and pruning a deleted subtree from the rendered tree. Pure (no Terminal.Gui, no I/O) so the delete
/// semantics are unit-testable, mirroring <see cref="TaskTreeArranger"/> (which builds the rows) and the
/// checklist edit helpers.
/// </summary>
public static class TaskTreeDeleteModel
{
    /// <summary>
    /// Classifies deleting the row at <paramref name="selectedIndex"/> in <paramref name="rows"/> (the loaded
    /// tree, ordered ancestors → current → descendants). Returns the <see cref="TaskTreeDeleteTarget"/> for the
    /// current task (<see cref="TaskTreeDeleteKind.Current"/>) or a descendant subtask
    /// (<see cref="TaskTreeDeleteKind.Subtask"/>), and <c>null</c> — an inert row — for:
    /// an out-of-range index or an empty/unloaded tree; a tree with no current-task row (defensive); or an
    /// <b>ancestor</b> row (delete is downward-only here — an ancestor is deleted from its own view, #594).
    /// </summary>
    public static TaskTreeDeleteTarget? Resolve(IReadOnlyList<TaskTreeRow> rows, int selectedIndex)
    {
        if (rows is null || selectedIndex < 0 || selectedIndex >= rows.Count)
            return null;

        var currentIndex = -1;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].IsCurrent)
            {
                currentIndex = i;
                break;
            }
        if (currentIndex < 0)
            return null;

        var row = rows[selectedIndex];
        if (selectedIndex == currentIndex)
            return new TaskTreeDeleteTarget(row.Task.Id, row.Task.Name, TaskTreeDeleteKind.Current);
        if (selectedIndex > currentIndex)
            return new TaskTreeDeleteTarget(row.Task.Id, row.Task.Name, TaskTreeDeleteKind.Subtask);
        return null; // an ancestor — inert (downward-only delete).
    }

    /// <summary>
    /// Removes the row for <paramref name="taskId"/> and its contiguous deeper descendants from
    /// <paramref name="rows"/> — the optimistic in-place removal of a deleted subtask (its whole subtree, since
    /// ClickUp cascades a parent delete to its subtasks). A no-op returning the same rows when the id isn't
    /// present (e.g. a mid-write refresh already dropped it).
    /// </summary>
    public static IReadOnlyList<TaskTreeRow> RemoveSubtree(IReadOnlyList<TaskTreeRow> rows, string taskId)
    {
        var index = -1;
        for (var i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].Task.Id, taskId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        if (index < 0)
            return rows;

        var depth = rows[index].Depth;
        var end = index + 1;
        while (end < rows.Count && rows[end].Depth > depth)
            end++;

        var result = new List<TaskTreeRow>(rows.Count - (end - index));
        for (var i = 0; i < index; i++)
            result.Add(rows[i]);
        for (var i = end; i < rows.Count; i++)
            result.Add(rows[i]);
        return result;
    }

    /// <summary>The row to select after removing the block that started at <paramref name="removedIndex"/>,
    /// clamped into a tree of <paramref name="newCount"/> rows (the row that slid up into the gap, or the last
    /// row); <c>-1</c> when nothing remains. Keeps the cursor near where the deleted subtask was.</summary>
    public static int SelectAfterDelete(int removedIndex, int newCount)
    {
        if (newCount <= 0)
            return -1;
        return Math.Clamp(removedIndex, 0, newCount - 1);
    }
}
