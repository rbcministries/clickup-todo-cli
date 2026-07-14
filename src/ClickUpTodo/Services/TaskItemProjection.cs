using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Projects a fetched <see cref="TaskDetail"/> back into the lighter <see cref="TaskItem"/> shape the
/// list carries. Used when launching Quick Updates from the Task Detail view (#159) for a task that
/// isn't in the current list snapshot (e.g. a task opened from the feed, #115) — the common case reuses
/// the live <see cref="TaskItem"/> from the snapshot, which carries fuller fidelity (assignee ids, the
/// status <c>type</c>).
/// <para>
/// A detail is lossy relative to a list item: it exposes priority only by <b>name</b> (no importance
/// level) and assignees only by <b>name</b> (no numeric id). Priority is recovered to its canonical
/// level via <see cref="ClickUpPriority"/>; assignee names map to <see cref="TaskAssignee"/> with a
/// placeholder id (<c>0</c>) — good enough for the Quick Updates panes, which on this path display the
/// current assignees rather than edit them by id.
/// </para>
/// </summary>
public static class TaskItemProjection
{
    /// <summary>Builds a <see cref="TaskItem"/> from a <see cref="TaskDetail"/>. Pure.</summary>
    public static TaskItem FromDetail(TaskDetail detail)
    {
        var priorityLevel = ClickUpPriority.LevelFromName(detail.Priority);
        return new TaskItem
        {
            Id = detail.Id,
            CustomId = detail.CustomId,
            Name = detail.Name,
            Url = detail.Url,
            StatusName = detail.StatusName,
            StatusColor = detail.StatusColor,
            ListId = detail.ListId,
            ListName = detail.ListName,
            DueDateMs = detail.DueDateMs,
            CreatedMs = detail.CreatedMs,
            UpdatedMs = detail.UpdatedMs,
            PriorityLevel = priorityLevel,
            PriorityName = ClickUpPriority.NameFromLevel(priorityLevel),
            // Only carry the colour when the priority actually resolved, so an unrecognised priority
            // never yields a coloured-but-nameless priority.
            PriorityColor = priorityLevel is null ? null : detail.PriorityColor,
            Assignees = [.. detail.Assignees.Select(name => new TaskAssignee(0, name))],
        };
    }
}
