using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>The three Tab-navigable controls of the Quick Updates screen (#153/#156), in focus order.</summary>
public enum QuickUpdatesPane
{
    Status = 0,
    Priority = 1,
    Assignees = 2,
}

/// <summary>
/// Pure presentation/navigation logic for the Quick Updates screen, factored out of the Terminal.Gui
/// glue so it can be unit-tested without a terminal (mirrors <see cref="StatusPickerModel"/>). Covers
/// the pane cycle order + wrap and the Priority/Assignees stub panes' row rendering + preselection.
/// The Status pane reuses <see cref="StatusPickerModel"/>; Status/Priority apply-on-Enter (#157) and
/// the Assignees search/apply (#158) build on this shell.
/// </summary>
public static class QuickUpdatesModel
{
    /// <summary>The number of panes (Status, Priority, Assignees).</summary>
    public const int PaneCount = 3;

    /// <summary>
    /// The pane focus lands on when Tab (<paramref name="forward"/> = true) or Shift+Tab
    /// (<paramref name="forward"/> = false) is pressed, cycling Status → Priority → Assignees and
    /// wrapping in both directions.
    /// </summary>
    public static QuickUpdatesPane Cycle(QuickUpdatesPane current, bool forward)
    {
        var next = ((int)current + (forward ? 1 : -1) + PaneCount) % PaneCount;
        return (QuickUpdatesPane)next;
    }

    /// <summary>The display text for a single priority row (indented like the status rows).</summary>
    public static string FormatPriority(string name) => $"  {name}";

    /// <summary>The priority rows in canonical order (Urgent → High → Normal → Low).</summary>
    public static IReadOnlyList<string> PriorityRows()
        => [.. ClickUpPriority.Names.Select(FormatPriority)];

    /// <summary>
    /// The index of the row matching the task's current importance <paramref name="level"/> (1=Urgent …
    /// 4=Low), or -1 when the task has no priority (or an out-of-range level). Rows run most-urgent
    /// first, so level <c>n</c> is row <c>n-1</c>.
    /// </summary>
    public static int PreselectedPriorityIndex(int? level)
        => level is >= 1 and <= 4 ? level.Value - 1 : -1;

    /// <summary>
    /// The rows for the (stubbed) Assignees pane: the task's current assignees, or a single
    /// <c>(no assignees)</c> placeholder when there are none. The candidate pool + search land in #158.
    /// </summary>
    public static IReadOnlyList<string> AssigneeRows(IReadOnlyList<TaskAssignee> assignees)
        => assignees.Count == 0
            ? ["  (no assignees)"]
            : [.. assignees.Select(a => $"  {a.Name}")];
}
