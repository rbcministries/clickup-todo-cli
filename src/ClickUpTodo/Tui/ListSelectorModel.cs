using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>The decision for toggling one candidate list: what should happen, and to which list id.</summary>
public readonly record struct ListToggleResult(ToggleKind Kind, string Id);

/// <summary>One rendered row of the list selector: the list, whether it's currently a selected list
/// (shown with a <c>✓</c>), and whether it's the <see cref="Primary"/> — the primary/home list, the
/// create target (#240), which carries its own <c>" (home)"</c> marker. Lists have no locked/undeletable
/// entry (unlike the assignee current-user default): the "≥1 list" rule is enforced by the host, not
/// this control.</summary>
public readonly record struct ListRow(string Id, string Name, bool Selected, bool Primary);

/// <summary>
/// The list-typed façade over the generic <see cref="SelectorModel"/> (#243), backing
/// <see cref="ListSelectorView"/> (#239). Keeps the list boundary in <see cref="NamedEntity"/> — mapping
/// to the base's <see cref="SelectorItem"/> here — so the New Task List selector (#240) and Quick Updates
/// List pane (#242) call sites and their tests stay in list terms while the pure selection logic lives in
/// one shared place. Lists are natively string-id'd, so — unlike <see cref="AssigneeSelectorModel"/>,
/// which adapts <c>long</c> ids — this façade is a near-identity map; it exists only to word the rows as
/// lists (<see cref="ListRow"/>) and carry the primary/home marker the base expresses generically as its
/// distinguished entry.
/// </summary>
public static class ListSelectorModel
{
    /// <summary>The marker appended after the primary/home list's name in the empty-state rows.</summary>
    public const string HomeSuffix = " (home)";

    /// <summary>The display text for a row: a leading <c>✓</c> when selected, else a two-column blank
    /// indent, plus <see cref="HomeSuffix"/> when the row is the primary/home list. Delegates to
    /// <see cref="SelectorModel.Format"/>.</summary>
    public static string Format(ListRow row) => SelectorModel.Format(ToRow(row), HomeSuffix);

    /// <summary>The empty-search rows: current lists (marked <c>✓</c>, <c>Primary</c> when its id is in
    /// <paramref name="primaryIds"/>) first, then the most-frequent <paramref name="topFrequent"/> top-up
    /// up to <paramref name="capacity"/>. Lists carry no locked entry, so the base's locked set is empty.
    /// See <see cref="SelectorModel.EmptyStateRows"/>.</summary>
    public static IReadOnlyList<ListRow> EmptyStateRows(
        IReadOnlyList<NamedEntity> selected,
        ISet<string> primaryIds,
        IReadOnlyList<NamedEntity> topFrequent,
        int capacity)
        => SelectorModel.EmptyStateRows(
                ToItems(selected), EmptyStringSet, primaryIds, ToItems(topFrequent), capacity)
            .Select(ToListRow)
            .ToList();

    /// <summary>The type-ahead rows: <paramref name="matches"/> as unselected rows, excluding any list in
    /// <paramref name="selectedIds"/>. See <see cref="SelectorModel.SearchResultRows"/>.</summary>
    public static IReadOnlyList<ListRow> SearchResultRows(
        IReadOnlyList<NamedEntity> matches, ISet<string> selectedIds)
        => SelectorModel.SearchResultRows(ToItems(matches), selectedIds)
            .Select(ToListRow)
            .ToList();

    /// <summary>The add/remove decision for picking list <paramref name="id"/>. Lists have no locked
    /// entry, so this is a plain add/remove — a selected list is always removable. See
    /// <see cref="SelectorModel.Toggle"/>. The passed set is not modified.</summary>
    public static ListToggleResult Toggle(ISet<string> selectedIds, string id)
    {
        var decision = SelectorModel.Toggle(selectedIds, EmptyStringSet, id);
        return new ListToggleResult(decision.Kind, id);
    }

    /// <summary>Whether a debounce timer captured at <paramref name="capturedStamp"/> still represents
    /// the latest keystroke (<paramref name="currentStamp"/>). See
    /// <see cref="SelectorModel.ShouldRunSearch"/>.</summary>
    public static bool ShouldRunSearch(long capturedStamp, long currentStamp)
        => SelectorModel.ShouldRunSearch(capturedStamp, currentStamp);

    /// <summary>
    /// The primary/home list — the create target — for the current state: the first
    /// <b>currently-marked</b> distinguished (home) list if one is still selected, otherwise the first
    /// selected list (so the host always has a create target while ≥1 list is selected), otherwise
    /// <c>null</c> when nothing is selected. Driven off the same distinguished set the base renders the
    /// <c>" (home)"</c> marker from, so the exposed primary and the on-screen marker never disagree: once
    /// the seeded home is removed and the base stops marking it, this falls through to the first
    /// selection rather than resurrecting an unmarked list as "home".
    /// </summary>
    public static NamedEntity? ResolvePrimary(
        IReadOnlyList<NamedEntity> selection, IReadOnlyList<NamedEntity> distinguishedSelection)
    {
        if (distinguishedSelection.Count > 0)
            return distinguishedSelection[0];
        return selection.Count > 0 ? selection[0] : null;
    }

    // ── list ↔ base conversions ───────────────────────────────────────────────

    private static readonly ISet<string> EmptyStringSet = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>A <see cref="NamedEntity"/> as a base <see cref="SelectorItem"/>. Also used by
    /// <see cref="ListSelectorView"/> to adapt its callbacks onto the base.</summary>
    internal static SelectorItem ToItem(NamedEntity list) => new(list.Id, list.Name);

    /// <summary>A base <see cref="SelectorItem"/> back to a <see cref="NamedEntity"/>.</summary>
    internal static NamedEntity ToEntity(SelectorItem item) => new(item.Id, item.Name);

    private static IReadOnlyList<SelectorItem> ToItems(IReadOnlyList<NamedEntity> lists)
        => lists.Select(ToItem).ToList();

    // A list row's Primary maps to the base's Distinguished; lists never set Locked.
    private static SelectorRow ToRow(ListRow row)
        => new(row.Id, row.Name, row.Selected, Locked: false, Distinguished: row.Primary);

    private static ListRow ToListRow(SelectorRow row)
        => new(row.Id, row.Name, row.Selected, Primary: row.Distinguished);
}
