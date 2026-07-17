using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure <see cref="ListSelectorModel"/> backing the reusable
/// <see cref="ListSelectorView"/> (#239): row rendering for the empty-search and type-ahead states, the
/// primary/home marker, the add/remove toggle decision (lists have no locked entry), and the debounce
/// coalescing decision. The shared logic is covered by <see cref="SelectorModel"/>'s own tests; these
/// pin the list-worded façade and the primary-marker seam it supplies.
/// </summary>
public sealed class ListSelectorModelTests
{
    private static NamedEntity L(string id, string name) => new(id, name);

    private static ISet<string> Ids(params string[] ids) => new HashSet<string>(ids, StringComparer.Ordinal);

    // ── Format ────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_Selected_LeadsWithCheck()
        => Assert.Equal("✓ Inbox", ListSelectorModel.Format(new ListRow("1", "Inbox", Selected: true, Primary: false)));

    [Fact]
    public void Format_Unselected_LeadsWithTwoSpaces()
        => Assert.Equal("  Inbox", ListSelectorModel.Format(new ListRow("1", "Inbox", Selected: false, Primary: false)));

    [Fact]
    public void Format_PrimaryRow_AppendsHomeMarker()
        => Assert.Equal("✓ Inbox (home)", ListSelectorModel.Format(new ListRow("1", "Inbox", Selected: true, Primary: true)));

    [Fact]
    public void Format_NonPrimaryRow_HasNoHomeMarker()
        => Assert.Equal("✓ Backlog", ListSelectorModel.Format(new ListRow("2", "Backlog", Selected: true, Primary: false)));

    // ── EmptyStateRows ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyState_SelectedFirstWithCheck_ThenTopUpExcludingSelected()
    {
        var rows = ListSelectorModel.EmptyStateRows(
            selected: [L("1", "Inbox"), L("2", "Backlog")],
            primaryIds: Ids(),
            topFrequent: [L("2", "Backlog"), L("3", "Sprint"), L("4", "Docs")],
            capacity: 10);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new ListRow("1", "Inbox", true, false), rows[0]);
        Assert.Equal(new ListRow("2", "Backlog", true, false), rows[1]); // selected — not re-added by top-up
        Assert.Equal(new ListRow("3", "Sprint", false, false), rows[2]);
        Assert.Equal(new ListRow("4", "Docs", false, false), rows[3]);
    }

    [Fact]
    public void EmptyState_MarksPrimarySelected()
    {
        var rows = ListSelectorModel.EmptyStateRows(
            selected: [L("home", "Inbox")], primaryIds: Ids("home"), topFrequent: [], capacity: 10);

        Assert.Equal(new ListRow("home", "Inbox", true, true), Assert.Single(rows));
    }

    [Fact]
    public void EmptyState_MarksPrimaryTopUp_WhenPrimaryIsUnselectedCandidate()
    {
        // A primary id can surface in the top-up (seeded as home but not yet a ✓ selection).
        var rows = ListSelectorModel.EmptyStateRows(
            selected: [], primaryIds: Ids("home"),
            topFrequent: [L("home", "Inbox"), L("2", "Other")], capacity: 10);

        Assert.Equal(new ListRow("home", "Inbox", false, true), rows[0]);
        Assert.Equal(new ListRow("2", "Other", false, false), rows[1]);
    }

    [Fact]
    public void EmptyState_CapacityBoundsOnlyTheTopUp()
    {
        var rows = ListSelectorModel.EmptyStateRows(
            selected: [L("1", "Inbox")],
            primaryIds: Ids(),
            topFrequent: [L("2", "B"), L("3", "C"), L("4", "D"), L("5", "E")],
            capacity: 3);

        Assert.Equal(["Inbox", "B", "C"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_SelectedAlwaysShown_EvenAboveCapacity()
    {
        var rows = ListSelectorModel.EmptyStateRows(
            selected: [L("1", "A"), L("2", "B"), L("3", "C"), L("4", "D")],
            primaryIds: Ids(),
            topFrequent: [L("9", "Z")],
            capacity: 2);

        Assert.Equal(["A", "B", "C", "D"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.True(r.Selected));
    }

    [Fact]
    public void EmptyState_DropsBlankIdsAndNames_AndDeDupes()
    {
        var rows = ListSelectorModel.EmptyStateRows(
            selected: [L("1", "Inbox"), L("", "NoId"), L("2", "  "), L("1", "Inbox-dup")],
            primaryIds: Ids(),
            topFrequent: [L("1", "Inbox"), L("3", "Sprint"), L("  ", "BlankId")],
            capacity: 10);

        Assert.Equal(["Inbox", "Sprint"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_EmptyPool_YieldsJustSelected()
    {
        var rows = ListSelectorModel.EmptyStateRows(
            selected: [L("1", "Inbox")], primaryIds: Ids(), topFrequent: [], capacity: 10);
        Assert.Equal(["Inbox"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_NothingSelectedNorPooled_YieldsEmpty()
        => Assert.Empty(ListSelectorModel.EmptyStateRows([], Ids(), [], 10));

    // ── SearchResultRows ────────────────────────────────────────────────────────

    [Fact]
    public void Search_MapsMatchesUnselected_ExcludingAlreadySelected()
    {
        var rows = ListSelectorModel.SearchResultRows(
            matches: [L("1", "Inbox"), L("2", "Backlog"), L("3", "Sprint")],
            selectedIds: Ids("2"));

        Assert.Equal(["Inbox", "Sprint"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.False(r.Selected));
        Assert.All(rows, r => Assert.False(r.Primary));
    }

    [Fact]
    public void Search_DropsBlankIdsNames_AndDeDupes()
    {
        var rows = ListSelectorModel.SearchResultRows(
            matches: [L("1", "Inbox"), L("1", "Inbox-dup"), L("", "NoId"), L("2", "   ")],
            selectedIds: Ids());
        Assert.Equal(["Inbox"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Search_EmptyMatches_YieldsEmpty()
        => Assert.Empty(ListSelectorModel.SearchResultRows([], Ids()));

    // ── Toggle (lists have no locked entry) ──────────────────────────────────────

    [Fact]
    public void Toggle_UnknownId_IsAdded()
        => Assert.Equal(new ListToggleResult(ToggleKind.Added, "7"), ListSelectorModel.Toggle(Ids("1", "2"), "7"));

    [Fact]
    public void Toggle_Selected_IsRemoved()
        => Assert.Equal(new ListToggleResult(ToggleKind.Removed, "2"), ListSelectorModel.Toggle(Ids("1", "2"), "2"));

    [Fact]
    public void Toggle_PrimarySelected_IsStillRemovable()
    {
        // Unlike the assignee locked default, the primary/home list is a plain selection: removable.
        // The "≥1 list" invariant is the host's job, not this control's.
        Assert.Equal(new ListToggleResult(ToggleKind.Removed, "home"), ListSelectorModel.Toggle(Ids("home", "2"), "home"));
    }

    [Fact]
    public void Toggle_DoesNotMutateItsInput()
    {
        var selected = Ids("1", "2");
        ListSelectorModel.Toggle(selected, "2");
        Assert.Equal(["1", "2"], selected.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ── ShouldRunSearch (debounce coalescing) ────────────────────────────────────

    [Fact]
    public void ShouldRunSearch_EqualStamps_Runs()
        => Assert.True(ListSelectorModel.ShouldRunSearch(5, 5));

    [Fact]
    public void ShouldRunSearch_StaleCapture_Skips()
        => Assert.False(ListSelectorModel.ShouldRunSearch(5, 6));

    // ── ShouldPickFromSearchBox (#234: Enter-in-box never removes) ────────────────
    // The base owns this decision; these pin that the list selector inherits the guard so an Enter in
    // an empty search box removes nothing, and search-box Enter stays strictly add-only.

    [Fact]
    public void ShouldPickFromSearchBox_BlankQuery_NeverPicks()
        => Assert.False(SelectorModel.ShouldPickFromSearchBox("", "home", Ids()));

    [Fact]
    public void ShouldPickFromSearchBox_AlreadySelectedHighlight_DoesNotPick()
        => Assert.False(SelectorModel.ShouldPickFromSearchBox("in", "1", Ids("1")));

    [Fact]
    public void ShouldPickFromSearchBox_ActiveQueryAddableHighlight_Picks()
        => Assert.True(SelectorModel.ShouldPickFromSearchBox("in", "3", Ids("1", "2")));
}
