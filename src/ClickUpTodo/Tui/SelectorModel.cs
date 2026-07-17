namespace ClickUpTodo.Tui;

/// <summary>The outcome of a <see cref="SelectorModel.Toggle"/> decision.</summary>
public enum ToggleKind
{
    /// <summary>The id was not selected and is now to be added.</summary>
    Added = 0,
    /// <summary>The id was selected (and not locked) and is now to be removed.</summary>
    Removed = 1,
    /// <summary>The id is a locked entry — a remove attempt is a no-op.</summary>
    LockedNoOp = 2,
}

/// <summary>An entity a selector can offer: a stable string id and a display name. The base selector
/// (<see cref="SelectorView"/>) is keyed on <b>string</b> ids so it fits both ClickUp lists (native
/// string ids) and assignees (a <c>long</c> id adapted via <c>ToString()</c> at the assignee
/// boundary — see <see cref="AssigneeSelectorModel"/>).</summary>
public readonly record struct SelectorItem(string Id, string Name);

/// <summary>One rendered row of a selector: the item, whether it's currently a selected entry (shown
/// with a <c>✓</c>), whether that selection is <see cref="Locked"/> (a default that can't be removed,
/// e.g. the current user on a New Task), and whether it's the <see cref="Distinguished"/> entry (the
/// primary/home item that carries its own marker, e.g. the create-target list — #239/#240).</summary>
public readonly record struct SelectorRow(string Id, string Name, bool Selected, bool Locked, bool Distinguished);

/// <summary>The decision for toggling one candidate: what should happen, and to whom.</summary>
public readonly record struct SelectorToggle(ToggleKind Kind, string Id);

/// <summary>
/// Pure presentation / selection logic backing <see cref="SelectorView"/>, kept free of Terminal.Gui,
/// I/O, and timers so it can be unit-tested in isolation (mirrors the <c>DetailPaneView.BuildCells</c>
/// / <c>QuickUpdatesModel</c> split — pure logic here, CI-untestable draw/input glue in the View).
/// <para>
/// Covers row rendering for both the empty-search state (current selection with a <c>✓</c>, topped up
/// from the most-frequent pool) and the type-ahead results, the add/remove/locked toggle decision, and
/// the debounce <em>coalescing</em> decision (does a fired timer still represent the latest keystroke).
/// The candidate pool itself comes from the host's match/frequency source; this class only shapes and
/// interprets it. Extracted from the merged assignee selector (#212) so the List selector (#239) can
/// specialize the same base instead of forking a near-duplicate (#243).
/// </para>
/// </summary>
public static class SelectorModel
{
    /// <summary>The display text for a row: a leading <c>✓</c> when selected, else a two-column blank
    /// indent (aligning with the status/priority rows' <c>"  "</c> prefix), plus
    /// <paramref name="distinguishedSuffix"/> appended when the row is the distinguished entry and a
    /// non-empty suffix was supplied. The default empty suffix reproduces the assignee selector's
    /// output verbatim (a locked default is shown selected, with no extra marker).</summary>
    public static string Format(SelectorRow row, string distinguishedSuffix = "")
    {
        var check = row.Selected ? "✓ " : "  ";
        var suffix = row.Distinguished && !string.IsNullOrEmpty(distinguishedSuffix) ? distinguishedSuffix : "";
        return $"{check}{row.Name}{suffix}";
    }

    /// <summary>
    /// The rows for the empty-search state: every currently-<paramref name="selected"/> item first
    /// (marked <c>✓</c>, <c>Locked</c> when its id is in <paramref name="lockedIds"/>, and
    /// <c>Distinguished</c> when in <paramref name="distinguishedIds"/>), then the most-frequent
    /// <paramref name="topFrequent"/> candidates that aren't already selected, filling up to
    /// <paramref name="capacity"/> rows in total. Selected items are <b>always</b> shown even if they
    /// alone exceed <paramref name="capacity"/> (the list scrolls) — the cap only bounds the top-up.
    /// De-dupes by id (an item only appears once), and drops rows with a blank id or a blank name.
    /// Input order among the selected and among the top-up is preserved.
    /// </summary>
    public static IReadOnlyList<SelectorRow> EmptyStateRows(
        IReadOnlyList<SelectorItem> selected,
        ISet<string> lockedIds,
        ISet<string> distinguishedIds,
        IReadOnlyList<SelectorItem> topFrequent,
        int capacity)
    {
        var rows = new List<SelectorRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in selected)
        {
            if (!IsUsable(item) || !seen.Add(item.Id))
                continue;
            rows.Add(new SelectorRow(item.Id, item.Name, Selected: true,
                Locked: lockedIds.Contains(item.Id), Distinguished: distinguishedIds.Contains(item.Id)));
        }

        // The top-up only fills the gap up to capacity; selected rows above cover the rest (scrolled).
        var topUpBudget = capacity - rows.Count;
        if (topUpBudget > 0)
        {
            foreach (var item in topFrequent)
            {
                if (topUpBudget <= 0)
                    break;
                if (!IsUsable(item) || !seen.Add(item.Id))
                    continue;
                rows.Add(new SelectorRow(item.Id, item.Name, Selected: false, Locked: false,
                    Distinguished: distinguishedIds.Contains(item.Id)));
                topUpBudget--;
            }
        }

        return rows;
    }

    /// <summary>
    /// The rows for a non-blank query: the <paramref name="matches"/> mapped to <b>unselected</b> rows,
    /// excluding anyone already in <paramref name="selectedIds"/> — search is how you <em>add</em> an
    /// item; you remove one by re-picking its <c>✓</c> row in the empty state. De-dupes by id and drops
    /// blank ids / blank names.
    /// </summary>
    public static IReadOnlyList<SelectorRow> SearchResultRows(
        IReadOnlyList<SelectorItem> matches, ISet<string> selectedIds)
    {
        var rows = new List<SelectorRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in matches)
        {
            if (!IsUsable(item) || selectedIds.Contains(item.Id) || !seen.Add(item.Id))
                continue;
            rows.Add(new SelectorRow(item.Id, item.Name, Selected: false, Locked: false, Distinguished: false));
        }
        return rows;
    }

    /// <summary>
    /// The decision for picking the candidate <paramref name="id"/>: <see cref="ToggleKind.Added"/>
    /// when it isn't currently selected, <see cref="ToggleKind.LockedNoOp"/> when it's a locked entry
    /// (selected and in <paramref name="lockedIds"/>) so a remove is refused, otherwise
    /// <see cref="ToggleKind.Removed"/>. Pure — the caller mutates its own selection set and (in
    /// immediate-apply mode) fires the server write off the returned kind; the passed sets are not
    /// modified.
    /// </summary>
    public static SelectorToggle Toggle(ISet<string> selectedIds, ISet<string> lockedIds, string id)
    {
        if (!selectedIds.Contains(id))
            return new SelectorToggle(ToggleKind.Added, id);
        if (lockedIds.Contains(id))
            return new SelectorToggle(ToggleKind.LockedNoOp, id);
        return new SelectorToggle(ToggleKind.Removed, id);
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
    /// Whether an <c>Enter</c> keypress in the search box should pick the highlighted row — making the
    /// search box strictly <em>add-only</em>. True only when there is an active (non-blank)
    /// <paramref name="query"/> <b>and</b> the highlighted row is an addable candidate: a usable
    /// (non-blank <paramref name="highlightedId"/>) entry that is <b>not already selected</b>
    /// (<paramref name="selectedIds"/>).
    /// <para>
    /// Gating on the query text alone is not enough: during the type-ahead debounce the displayed rows
    /// can still be the empty-state <c>✓</c> current-selection rows even though the query box is already
    /// non-blank (<c>OnSearchChanged</c> arms the timer without re-rendering). Picking row 0 there would
    /// silently <em>remove</em> the first selected entry — the exact #234 symptom, through a timing
    /// window. By refusing to pick an already-selected row, a search-box <c>Enter</c> can never remove
    /// regardless of debounce/render state; removal stays an explicit pick on a <c>✓</c> row (cursor into
    /// the list, then <c>Enter</c>). A blank query yields no addable target (row 0 is a <c>✓</c> entry),
    /// so it is a no-op. Applies to every specialization of the base (assignees #158/#213, lists #239).
    /// </para>
    /// </summary>
    public static bool ShouldPickFromSearchBox(string? query, string highlightedId, ISet<string> selectedIds)
        => !string.IsNullOrWhiteSpace(query)
           && !string.IsNullOrWhiteSpace(highlightedId)
           && !selectedIds.Contains(highlightedId);

    private static bool IsUsable(SelectorItem item)
        => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name);
}
