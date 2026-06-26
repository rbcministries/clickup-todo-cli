using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure presentation logic for the status-picker screen, factored out of the Terminal.Gui glue so
/// it can be unit-tested without a terminal: how each status row is rendered and which row should be
/// pre-selected for the task's current status.
/// </summary>
public static class StatusPickerModel
{
    /// <summary>The display text for a single status row.</summary>
    public static string FormatStatus(StatusOption status) => $"  {status.Name}";

    /// <summary>
    /// The index of the status matching <paramref name="currentStatus"/> (case-insensitive), or -1
    /// when there's no match (e.g. the task has no status, or it isn't in this list's workflow).
    /// </summary>
    public static int PreselectedIndex(IReadOnlyList<StatusOption> statuses, string? currentStatus)
    {
        if (string.IsNullOrWhiteSpace(currentStatus))
            return -1;
        for (var i = 0; i < statuses.Count; i++)
            if (string.Equals(statuses[i].Name, currentStatus, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
