using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// Pure classification of a <b>foreign</b> subtask — one pulled in under an in-view parent by the #70
/// fetch, i.e. not in the assignee-scoped snapshot — under the F4 three-state subtask view (#179).
/// A foreign subtask is never assigned to me (else it would be in the snapshot), so it is either
/// <b>unassigned</b> (no assignees) or <b>assigned to others</b>. The F4 state decides which of those
/// render: <see cref="SubtaskView.MineAndUnassigned"/> shows only the unassigned ones (marked
/// <c>(unassigned)</c>); <see cref="SubtaskView.All"/> shows both (others marked <c>(not assigned to
/// you)</c>). Pure (no Terminal.Gui, no I/O) so the rule is unit-testable.
/// </summary>
public static class SubtaskVisibility
{
    /// <summary>A subtask is unassigned when it carries no assignees.</summary>
    public static bool IsUnassigned(TaskItem task) => task.Assignees.Count == 0;

    /// <summary>
    /// Whether a foreign (pulled-in) subtask should render under the given F4 <paramref name="state"/>:
    /// <see cref="SubtaskView.All"/> shows every foreign subtask; <see cref="SubtaskView.MineAndUnassigned"/>
    /// shows only the unassigned ones; <see cref="SubtaskView.Hidden"/> shows none (nesting is off).
    /// </summary>
    public static bool IsVisibleForeign(TaskItem foreign, SubtaskView state) => state switch
    {
        SubtaskView.All => true,
        SubtaskView.MineAndUnassigned => IsUnassigned(foreign),
        _ => false,
    };
}
