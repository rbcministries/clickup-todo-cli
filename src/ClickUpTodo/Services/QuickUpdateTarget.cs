using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// The mutable "unit of truth" a Quick Updates commit resolves against and writes back to (#297).
/// It decouples the status/priority/assignee write path from the main-list snapshot (<c>_all</c>):
/// <list type="bullet">
/// <item>List mode backs it with the snapshot + visible rows, so a commit repaints the on-screen
/// row (the list stays authoritative — unchanged behaviour).</item>
/// <item>Single-task launch mode (#296) backs it with the one loaded task and no list, so Quick
/// Updates works with no <c>_all</c> present.</item>
/// </list>
/// Both modes route a commit through the same resolve → apply reconcile
/// (<see cref="TaskService.ApplyFieldChanges"/>), so a field settles identically in each. Not
/// thread-safe: like the list path it is only ever touched on the UI thread.
/// </summary>
public interface IQuickUpdateTarget
{
    /// <summary>The current record for <paramref name="taskId"/>, or <c>null</c> if it is no longer
    /// editable here (dropped from the working set, or a different task than this target holds).</summary>
    TaskItem? Resolve(string taskId);

    /// <summary>Applies an in-place field update. <paramref name="sending"/> marks the optimistic
    /// (in-flight) apply versus the server-confirmed / reverted settle — the list target uses it to
    /// mark the row as sending; a single-task target has no visible row and ignores it.</summary>
    void Apply(TaskItem updated, bool sending);
}

/// <summary>
/// An <see cref="IQuickUpdateTarget"/> backed by a single loaded task with <b>no list snapshot</b> —
/// the unit of truth for Quick Updates launched over a Task Detail whose task isn't in the main list
/// (a feed-opened task today, #115; every task in single-task launch mode, #296). It holds the current
/// record so consecutive edits compose, and <see cref="Apply"/> reuses the same pure
/// <see cref="TaskService.ApplyFieldChanges"/> reconcile the list snapshot uses. Pure and
/// terminal-free, so both entry modes' shared write/reconcile is unit-testable.
/// </summary>
public sealed class SingleTaskUpdateTarget(TaskItem task) : IQuickUpdateTarget
{
    private TaskItem _task = task;

    /// <summary>The current record after any applied edits.</summary>
    public TaskItem Current => _task;

    public TaskItem? Resolve(string taskId)
        => string.Equals(_task.Id, taskId, StringComparison.Ordinal) ? _task : null;

    public void Apply(TaskItem updated, bool sending)
    {
        _ = sending; // no visible row to mark; the loaded task simply carries the new value
        if (!string.Equals(_task.Id, updated.Id, StringComparison.Ordinal))
            return;
        // Reuse the exact snapshot reconcile on a one-element list so a single-task edit composes
        // like a list edit — the parity that lets #296 reuse this path unchanged.
        _task = TaskService.ApplyFieldChanges([_task], updated)[0];
    }
}
