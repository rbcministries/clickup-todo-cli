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

    /// <summary>
    /// Builds a <see cref="TaskItem"/> that seeds the Quick Updates screen from a <see cref="TaskDetail"/>
    /// — the fallback used when Quick Updates is launched from the detail view (#159) for a task that
    /// isn't in the live list snapshot (e.g. a detail opened from the feed, #115). The importance
    /// <c>PriorityLevel</c> is derived from the detail's priority <em>name</em> (which is all a
    /// <see cref="TaskDetail"/> carries), and assignees keep their display names with a placeholder id
    /// (<c>0</c>) since the detail exposes names only — enough for the Priority/Assignees panes'
    /// display and the Status apply, which is the only pane that writes on <c>main</c>. Callers should
    /// prefer the live <see cref="TaskItem"/> from the snapshot (real ids) when present.
    /// </summary>
    public static TaskItem TaskItemFromDetail(TaskDetail detail) => new()
    {
        Id = detail.Id,
        CustomId = detail.CustomId,
        Name = detail.Name,
        Url = detail.Url,
        StatusName = detail.StatusName,
        StatusColor = detail.StatusColor,
        ListId = detail.ListId,
        ListName = detail.ListName,
        PriorityLevel = ClickUpPriority.LevelFromName(detail.Priority),
        PriorityName = detail.Priority,
        PriorityColor = detail.PriorityColor,
        DueDateMs = detail.DueDateMs,
        CreatedMs = detail.CreatedMs,
        UpdatedMs = detail.UpdatedMs,
        Assignees = [.. detail.Assignees.Select(name => new TaskAssignee(0, name))],
    };
}
