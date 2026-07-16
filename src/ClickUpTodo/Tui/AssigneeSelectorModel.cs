using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>The outcome of a <see cref="AssigneeSelectorModel.Toggle"/> decision.</summary>
public enum ToggleKind
{
    /// <summary>The id was not selected and is now to be added.</summary>
    Added = 0,
    /// <summary>The id was selected (and not locked) and is now to be removed.</summary>
    Removed = 1,
    /// <summary>The id is a locked default assignee — a remove attempt is a no-op.</summary>
    LockedNoOp = 2,
}

/// <summary>The decision for toggling one candidate: what should happen, and to whom.</summary>
public readonly record struct ToggleResult(ToggleKind Kind, long Id);

/// <summary>One rendered row of the assignee selector: the person, whether they're currently a
/// selected assignee (shown with a <c>✓</c>), and whether that selection is locked (a default that
/// can't be removed, e.g. the current user on a New Task).</summary>
public readonly record struct AssigneeRow(long Id, string Name, bool Selected, bool Locked);

/// <summary>
/// Pure presentation / selection logic backing <see cref="AssigneeSelectorView"/> (#212), kept free
/// of Terminal.Gui, I/O, and timers so it can be unit-tested in isolation (mirrors the
/// <c>DetailPaneView.BuildCells</c> / <c>QuickUpdatesModel</c> split — pure logic here, CI-untestable
/// draw/input glue in the View).
/// <para>
/// Covers row rendering for both the empty-search state (current assignees with a <c>✓</c>, topped up
/// from the most-frequent pool) and the type-ahead results, the add/remove/locked toggle decision,
/// and the debounce <em>coalescing</em> decision (does a fired timer still represent the latest
/// keystroke). The candidate pool itself comes from <see cref="Services.AssigneeFrequencyCache"/>
/// (#155); this class only shapes and interprets it.
/// </para>
/// </summary>
public static class AssigneeSelectorModel
{
    /// <summary>The display text for a row: a leading <c>✓</c> when selected, else a two-column blank
    /// indent (aligning with the status/priority rows' <c>"  "</c> prefix).</summary>
    public static string Format(AssigneeRow row) => row.Selected ? $"✓ {row.Name}" : $"  {row.Name}";

    /// <summary>
    /// The rows for the empty-search state: every currently-<paramref name="selected"/> assignee first
    /// (marked <c>✓</c>, and <c>Locked</c> when their id is in <paramref name="lockedIds"/>), then the
    /// most-frequent <paramref name="topFrequent"/> candidates that aren't already selected, filling up
    /// to <paramref name="capacity"/> rows in total. Selected assignees are <b>always</b> shown even if
    /// they alone exceed <paramref name="capacity"/> (the list scrolls) — the cap only bounds the
    /// top-up. De-dupes by id (a person only appears once), and drops rows with a non-positive id or a
    /// blank name. Input order among the selected and among the top-up is preserved.
    /// </summary>
    public static IReadOnlyList<AssigneeRow> EmptyStateRows(
        IReadOnlyList<TaskAssignee> selected,
        ISet<long> lockedIds,
        IReadOnlyList<TaskAssignee> topFrequent,
        int capacity)
    {
        var rows = new List<AssigneeRow>();
        var seen = new HashSet<long>();

        foreach (var person in selected)
        {
            if (!IsUsable(person) || !seen.Add(person.Id))
                continue;
            rows.Add(new AssigneeRow(person.Id, person.Name, Selected: true, Locked: lockedIds.Contains(person.Id)));
        }

        // The top-up only fills the gap up to capacity; selected rows above cover the rest (scrolled).
        var topUpBudget = capacity - rows.Count;
        if (topUpBudget > 0)
        {
            foreach (var person in topFrequent)
            {
                if (topUpBudget <= 0)
                    break;
                if (!IsUsable(person) || !seen.Add(person.Id))
                    continue;
                rows.Add(new AssigneeRow(person.Id, person.Name, Selected: false, Locked: false));
                topUpBudget--;
            }
        }

        return rows;
    }

    /// <summary>
    /// The rows for a non-blank query: the <paramref name="matches"/> mapped to <b>unselected</b> rows,
    /// excluding anyone already in <paramref name="selectedIds"/> — search is how you <em>add</em> a
    /// person; you remove one by re-picking their <c>✓</c> row in the empty state. De-dupes by id and
    /// drops non-positive ids / blank names.
    /// </summary>
    public static IReadOnlyList<AssigneeRow> SearchResultRows(
        IReadOnlyList<TaskAssignee> matches, ISet<long> selectedIds)
    {
        var rows = new List<AssigneeRow>();
        var seen = new HashSet<long>();
        foreach (var person in matches)
        {
            if (!IsUsable(person) || selectedIds.Contains(person.Id) || !seen.Add(person.Id))
                continue;
            rows.Add(new AssigneeRow(person.Id, person.Name, Selected: false, Locked: false));
        }
        return rows;
    }

    /// <summary>
    /// The decision for picking the candidate <paramref name="id"/>: <see cref="ToggleKind.Added"/>
    /// when it isn't currently selected, <see cref="ToggleKind.LockedNoOp"/> when it's a locked default
    /// (selected and in <paramref name="lockedIds"/>) so a remove is refused, otherwise
    /// <see cref="ToggleKind.Removed"/>. Pure — the caller mutates its own selection set and (in
    /// immediate-apply mode) fires the server write off the returned kind; the passed sets are not
    /// modified.
    /// </summary>
    public static ToggleResult Toggle(ISet<long> selectedIds, ISet<long> lockedIds, long id)
    {
        if (!selectedIds.Contains(id))
            return new ToggleResult(ToggleKind.Added, id);
        if (lockedIds.Contains(id))
            return new ToggleResult(ToggleKind.LockedNoOp, id);
        return new ToggleResult(ToggleKind.Removed, id);
    }

    /// <summary>
    /// Whether a debounce timer that was scheduled for keystroke <paramref name="capturedStamp"/> should
    /// still run its search: only when no newer keystroke has arrived since (i.e. it equals the
    /// <paramref name="currentStamp"/>). The View bumps a monotonic stamp on every keystroke and
    /// captures it when arming the timer; this coalesces a burst of typing into a single search after
    /// the pause. Keeping the decision pure makes it testable without real waits.
    /// </summary>
    public static bool ShouldRunSearch(long capturedStamp, long currentStamp)
        => capturedStamp == currentStamp;

    /// <summary>
    /// Whether an <c>Enter</c> keypress in the search box should pick the highlighted row. Only when
    /// there is an active (non-blank) <paramref name="query"/> and at least one row
    /// (<paramref name="rowCount"/> &gt; 0) — in that state every row is an addable search match, so
    /// picking <em>adds</em> without leaving the box. On a blank query the rows are the current-assignee
    /// <c>✓</c> rows (and top-frequent top-ups), where picking row 0 would silently <em>remove</em> the
    /// first assignee (#234); there the caller swallows <c>Enter</c> as a no-op and leaves removal to an
    /// explicit pick on a <c>✓</c> row (cursor into the list, then <c>Enter</c>).
    /// </summary>
    public static bool ShouldPickFromSearchBox(string? query, int rowCount)
        => rowCount > 0 && !string.IsNullOrWhiteSpace(query);

    private static bool IsUsable(TaskAssignee person)
        => person.Id > 0 && !string.IsNullOrWhiteSpace(person.Name);
}
