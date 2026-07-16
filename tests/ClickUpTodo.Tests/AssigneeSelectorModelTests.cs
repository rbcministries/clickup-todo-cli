using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure <see cref="AssigneeSelectorModel"/> backing the reusable
/// <see cref="AssigneeSelectorView"/> (#212): row rendering for the empty-search and type-ahead
/// states, the add/remove/locked toggle decision, and the debounce coalescing decision.
/// </summary>
public sealed class AssigneeSelectorModelTests
{
    private static TaskAssignee A(long id, string name) => new(id, name);

    private static ISet<long> Ids(params long[] ids) => new HashSet<long>(ids);

    // ── Format ────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_Selected_LeadsWithCheck()
        => Assert.Equal("✓ Ada", AssigneeSelectorModel.Format(new AssigneeRow(1, "Ada", Selected: true, Locked: false)));

    [Fact]
    public void Format_Unselected_LeadsWithTwoSpaces()
        => Assert.Equal("  Ada", AssigneeSelectorModel.Format(new AssigneeRow(1, "Ada", Selected: false, Locked: false)));

    [Fact]
    public void Format_LockedRow_StillShownAsSelected()
        => Assert.Equal("✓ Ada", AssigneeSelectorModel.Format(new AssigneeRow(1, "Ada", Selected: true, Locked: true)));

    // ── EmptyStateRows ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyState_SelectedFirstWithCheck_ThenTopUpExcludingSelected()
    {
        var rows = AssigneeSelectorModel.EmptyStateRows(
            selected: [A(1, "Ada"), A(2, "Babbage")],
            lockedIds: Ids(),
            topFrequent: [A(2, "Babbage"), A(3, "Curie"), A(4, "Dijkstra")],
            capacity: 10);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new AssigneeRow(1, "Ada", true, false), rows[0]);
        Assert.Equal(new AssigneeRow(2, "Babbage", true, false), rows[1]); // selected — not re-added by top-up
        Assert.Equal(new AssigneeRow(3, "Curie", false, false), rows[2]);
        Assert.Equal(new AssigneeRow(4, "Dijkstra", false, false), rows[3]);
    }

    [Fact]
    public void EmptyState_MarksLockedSelected()
    {
        var rows = AssigneeSelectorModel.EmptyStateRows(
            selected: [A(1, "Ada")], lockedIds: Ids(1), topFrequent: [], capacity: 10);

        Assert.Equal(new AssigneeRow(1, "Ada", true, true), Assert.Single(rows));
    }

    [Fact]
    public void EmptyState_CapacityBoundsOnlyTheTopUp()
    {
        // capacity 3, one selected → at most 2 top-up rows.
        var rows = AssigneeSelectorModel.EmptyStateRows(
            selected: [A(1, "Ada")],
            lockedIds: Ids(),
            topFrequent: [A(2, "B"), A(3, "C"), A(4, "D"), A(5, "E")],
            capacity: 3);

        Assert.Equal(["Ada", "B", "C"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_SelectedAlwaysShown_EvenAboveCapacity()
    {
        // Four selected, capacity 2: all selected still appear; no top-up (budget already negative).
        var rows = AssigneeSelectorModel.EmptyStateRows(
            selected: [A(1, "A"), A(2, "B"), A(3, "C"), A(4, "D")],
            lockedIds: Ids(),
            topFrequent: [A(9, "Z")],
            capacity: 2);

        Assert.Equal(["A", "B", "C", "D"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.True(r.Selected));
    }

    [Fact]
    public void EmptyState_DropsBlankNamesAndNonPositiveIds_AndDeDupes()
    {
        var rows = AssigneeSelectorModel.EmptyStateRows(
            selected: [A(1, "Ada"), A(0, "Zero"), A(2, "  "), A(1, "Ada-dup")],
            lockedIds: Ids(),
            topFrequent: [A(1, "Ada"), A(3, "Curie"), A(-5, "Neg")],
            capacity: 10);

        Assert.Equal(["Ada", "Curie"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_EmptyPool_YieldsJustSelected()
    {
        var rows = AssigneeSelectorModel.EmptyStateRows(
            selected: [A(1, "Ada")], lockedIds: Ids(), topFrequent: [], capacity: 10);
        Assert.Equal(["Ada"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_NothingSelectedNorPooled_YieldsEmpty()
        => Assert.Empty(AssigneeSelectorModel.EmptyStateRows([], Ids(), [], 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void EmptyState_NonPositiveCapacity_ShowsSelectedButNoTopUp(int capacity)
    {
        // The model itself doesn't clamp capacity (the View guards it); a non-positive budget yields no
        // top-up, but selected assignees are always shown regardless.
        var rows = AssigneeSelectorModel.EmptyStateRows(
            selected: [A(1, "Ada")], lockedIds: Ids(), topFrequent: [A(2, "Babbage")], capacity: capacity);
        Assert.Equal(["Ada"], rows.Select(r => r.Name));
    }

    // ── SearchResultRows ────────────────────────────────────────────────────────

    [Fact]
    public void Search_MapsMatchesUnselected_ExcludingAlreadySelected()
    {
        var rows = AssigneeSelectorModel.SearchResultRows(
            matches: [A(1, "Ada"), A(2, "Babbage"), A(3, "Curie")],
            selectedIds: Ids(2));

        Assert.Equal(["Ada", "Curie"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.False(r.Selected));
        Assert.All(rows, r => Assert.False(r.Locked));
    }

    [Fact]
    public void Search_DropsBlankNamesNonPositiveIds_AndDeDupes()
    {
        var rows = AssigneeSelectorModel.SearchResultRows(
            matches: [A(1, "Ada"), A(1, "Ada-dup"), A(0, "Zero"), A(2, "   ")],
            selectedIds: Ids());
        Assert.Equal(["Ada"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Search_EmptyMatches_YieldsEmpty()
        => Assert.Empty(AssigneeSelectorModel.SearchResultRows([], Ids()));

    // ── Toggle ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggle_UnknownId_IsAdded()
        => Assert.Equal(new ToggleResult(ToggleKind.Added, 7), AssigneeSelectorModel.Toggle(Ids(1, 2), Ids(), 7));

    [Fact]
    public void Toggle_SelectedUnlocked_IsRemoved()
        => Assert.Equal(new ToggleResult(ToggleKind.Removed, 2), AssigneeSelectorModel.Toggle(Ids(1, 2), Ids(), 2));

    [Fact]
    public void Toggle_SelectedLocked_IsNoOp()
        => Assert.Equal(new ToggleResult(ToggleKind.LockedNoOp, 1), AssigneeSelectorModel.Toggle(Ids(1, 2), Ids(1), 1));

    [Fact]
    public void Toggle_LockedButNotSelected_IsAdded()
    {
        // A lock only bites once the person is actually selected; an unselected locked id still adds.
        Assert.Equal(new ToggleResult(ToggleKind.Added, 5), AssigneeSelectorModel.Toggle(Ids(1), Ids(5), 5));
    }

    [Fact]
    public void Toggle_DoesNotMutateItsInputs()
    {
        var selected = Ids(1, 2);
        var locked = Ids(1);
        AssigneeSelectorModel.Toggle(selected, locked, 2);
        Assert.Equal([1L, 2L], selected.OrderBy(x => x));
        Assert.Equal([1L], locked);
    }

    // ── ShouldRunSearch (debounce coalescing) ────────────────────────────────────

    [Fact]
    public void ShouldRunSearch_EqualStamps_Runs()
        => Assert.True(AssigneeSelectorModel.ShouldRunSearch(5, 5));

    [Fact]
    public void ShouldRunSearch_StaleCapture_Skips()
        => Assert.False(AssigneeSelectorModel.ShouldRunSearch(5, 6)); // a newer keystroke arrived
}
