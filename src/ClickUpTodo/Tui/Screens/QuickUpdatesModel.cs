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
/// the pane cycle order + wrap and the Status/Priority rows with their leading <c>✓</c> current-value
/// marker + preselection (#157). The Assignees pane's own row/search/toggle logic lives in
/// <see cref="AssigneeSelectorModel"/> (#212), embedded by the screen as an
/// <see cref="AssigneeSelectorView"/> in immediate-apply mode (#158).
/// </summary>
public static class QuickUpdatesModel
{
    /// <summary>The number of panes (Status, Priority, Assignees).</summary>
    public const int PaneCount = 3;

    /// <summary>The 2-column prefix on the currently-effective row: a check mark then a space.</summary>
    public const string CurrentMarker = "✓ ";

    /// <summary>The 2-column prefix on a non-current row, keeping every label left-aligned under the mark.</summary>
    public const string NoMarker = "  ";

    /// <summary>The label of the Priority pane's "clear priority" row (commits a <c>null</c> level).</summary>
    public const string NoPriorityLabel = "(no priority)";

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

    /// <summary>A selector row: the label prefixed with <see cref="CurrentMarker"/> when it is the
    /// task's current effective value, else <see cref="NoMarker"/> so the two align.</summary>
    public static string Mark(string label, bool current) => (current ? CurrentMarker : NoMarker) + label;

    /// <summary>The Status pane rows, with a leading <c>✓</c> on the row whose name matches
    /// <paramref name="effectiveStatus"/> (case-insensitive; none marked when it isn't in the workflow).</summary>
    public static IReadOnlyList<string> StatusRows(IReadOnlyList<StatusOption> statuses, string? effectiveStatus)
        => [.. statuses.Select(s => Mark(s.Name, string.Equals(s.Name, effectiveStatus, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>The Priority pane row labels: the four canonical priorities (Urgent → Low) then the
    /// "(no priority)" clear row.</summary>
    public static IReadOnlyList<string> PriorityLabels { get; } = [.. ClickUpPriority.Names, NoPriorityLabel];

    /// <summary>The row index of the "(no priority)" clear option — always the last row.</summary>
    public static int NoPriorityRow => ClickUpPriority.Names.Count;

    /// <summary>
    /// The importance level a Priority row commits: 1..4 for the four priority rows, or <c>null</c> for
    /// the "(no priority)" clear row (and any out-of-range index).
    /// </summary>
    public static int? PriorityLevelForRow(int index)
        => index >= 0 && index < ClickUpPriority.Names.Count ? index + 1 : null;

    /// <summary>
    /// The Priority row to preselect / mark for a task's current importance <paramref name="level"/>:
    /// row <c>level-1</c> for 1..4, else the "(no priority)" clear row (unset or out-of-range).
    /// </summary>
    public static int PriorityRowForLevel(int? level)
        => level is >= 1 and <= 4 ? level.Value - 1 : NoPriorityRow;

    /// <summary>The Priority pane rows, with a leading <c>✓</c> on the row matching the task's current
    /// effective <paramref name="effectiveLevel"/> (the clear row when it has no priority).</summary>
    public static IReadOnlyList<string> PriorityRows(int? effectiveLevel)
        => [.. PriorityLabels.Select((label, i) => Mark(label, PriorityLevelForRow(i) == effectiveLevel))];
}
