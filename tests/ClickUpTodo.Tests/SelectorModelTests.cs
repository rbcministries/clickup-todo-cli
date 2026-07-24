using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the generic, string-id <see cref="SelectorModel"/> extracted from the assignee
/// selector (#243) so the List selector (#239) can specialize the same base: row rendering for the
/// empty-search and type-ahead states (including the distinguished/primary marker seam), the
/// add/remove/locked toggle decision, and the debounce coalescing decision. The assignee-typed façade
/// is covered separately by <see cref="AssigneeSelectorModelTests"/>.
/// </summary>
public sealed class SelectorModelTests
{
    private static SelectorItem I(string id, string name) => new(id, name);

    private static ISet<string> Ids(params string[] ids) => new HashSet<string>(ids, StringComparer.Ordinal);

    // ── Format ────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_Selected_LeadsWithCheck()
        => Assert.Equal("✓ Ada", SelectorModel.Format(new SelectorRow("1", "Ada", Selected: true, Locked: false, Distinguished: false)));

    [Fact]
    public void Format_Unselected_LeadsWithTwoSpaces()
        => Assert.Equal("  Ada", SelectorModel.Format(new SelectorRow("1", "Ada", Selected: false, Locked: false, Distinguished: false)));

    [Fact]
    public void Format_LockedRow_StillShownAsSelected_NoExtraMarker()
        => Assert.Equal("✓ Ada", SelectorModel.Format(new SelectorRow("1", "Ada", Selected: true, Locked: true, Distinguished: false)));

    [Fact]
    public void Format_DistinguishedRow_AppendsSuffix_WhenSupplied()
        => Assert.Equal("✓ Inbox (home)",
            SelectorModel.Format(new SelectorRow("1", "Inbox", Selected: true, Locked: false, Distinguished: true), " (home)"));

    [Fact]
    public void Format_DistinguishedRow_NoSuffix_WhenSuffixEmpty()
        => Assert.Equal("✓ Inbox",
            SelectorModel.Format(new SelectorRow("1", "Inbox", Selected: true, Locked: false, Distinguished: true)));

    [Fact]
    public void Format_NonDistinguishedRow_IgnoresSuffix()
        => Assert.Equal("✓ Ada",
            SelectorModel.Format(new SelectorRow("1", "Ada", Selected: true, Locked: false, Distinguished: false), " (home)"));

    // ── EmptyStateRows ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyState_SelectedFirstWithCheck_ThenTopUpExcludingSelected()
    {
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("1", "Ada"), I("2", "Babbage")],
            lockedIds: Ids(),
            distinguishedIds: Ids(),
            topFrequent: [I("2", "Babbage"), I("3", "Curie"), I("4", "Dijkstra")],
            capacity: 10);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new SelectorRow("1", "Ada", true, false, false), rows[0]);
        Assert.Equal(new SelectorRow("2", "Babbage", true, false, false), rows[1]); // selected — not re-added by top-up
        Assert.Equal(new SelectorRow("3", "Curie", false, false, false), rows[2]);
        Assert.Equal(new SelectorRow("4", "Dijkstra", false, false, false), rows[3]);
    }

    [Fact]
    public void EmptyState_MarksLockedSelected()
    {
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("1", "Ada")], lockedIds: Ids("1"), distinguishedIds: Ids(), topFrequent: [], capacity: 10);

        Assert.Equal(new SelectorRow("1", "Ada", true, true, false), Assert.Single(rows));
    }

    [Fact]
    public void EmptyState_MarksDistinguishedSelected()
    {
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("home", "Inbox")], lockedIds: Ids(), distinguishedIds: Ids("home"), topFrequent: [], capacity: 10);

        Assert.Equal(new SelectorRow("home", "Inbox", true, false, true), Assert.Single(rows));
    }

    [Fact]
    public void EmptyState_MarksDistinguishedTopUp_WhenPrimaryIsUnselectedCandidate()
    {
        // A distinguished id can appear in the top-up (e.g. the home list surfaced but not yet a
        // "selected" location) and is still flagged so its marker renders.
        var rows = SelectorModel.EmptyStateRows(
            selected: [], lockedIds: Ids(), distinguishedIds: Ids("home"),
            topFrequent: [I("home", "Inbox"), I("2", "Other")], capacity: 10);

        Assert.Equal(new SelectorRow("home", "Inbox", false, false, true), rows[0]);
        Assert.Equal(new SelectorRow("2", "Other", false, false, false), rows[1]);
    }

    [Fact]
    public void EmptyState_CapacityBoundsOnlyTheTopUp()
    {
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("1", "Ada")],
            lockedIds: Ids(),
            distinguishedIds: Ids(),
            topFrequent: [I("2", "B"), I("3", "C"), I("4", "D"), I("5", "E")],
            capacity: 3);

        Assert.Equal(["Ada", "B", "C"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_SelectedAlwaysShown_EvenAboveCapacity()
    {
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("1", "A"), I("2", "B"), I("3", "C"), I("4", "D")],
            lockedIds: Ids(),
            distinguishedIds: Ids(),
            topFrequent: [I("9", "Z")],
            capacity: 2);

        Assert.Equal(["A", "B", "C", "D"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.True(r.Selected));
    }

    [Fact]
    public void EmptyState_DropsBlankNamesAndBlankIds_AndDeDupes()
    {
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("1", "Ada"), I("", "Blank"), I("2", "  "), I("1", "Ada-dup")],
            lockedIds: Ids(),
            distinguishedIds: Ids(),
            topFrequent: [I("1", "Ada"), I("3", "Curie"), I(" ", "Whitespace")],
            capacity: 10);

        Assert.Equal(["Ada", "Curie"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_StringIds_AreOrdinalAndNonNumeric()
    {
        // Base ids are opaque tokens — de-dupe is exact/ordinal and non-numeric ids are first-class.
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("abc123", "List A"), I("ABC123", "List B")], // different ids (case-sensitive)
            lockedIds: Ids(),
            distinguishedIds: Ids(),
            topFrequent: [],
            capacity: 10);

        Assert.Equal(["List A", "List B"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_NothingSelectedNorPooled_YieldsEmpty()
        => Assert.Empty(SelectorModel.EmptyStateRows([], Ids(), Ids(), [], 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void EmptyState_NonPositiveCapacity_ShowsSelectedButNoTopUp(int capacity)
    {
        var rows = SelectorModel.EmptyStateRows(
            selected: [I("1", "Ada")], lockedIds: Ids(), distinguishedIds: Ids(), topFrequent: [I("2", "Babbage")], capacity: capacity);
        Assert.Equal(["Ada"], rows.Select(r => r.Name));
    }

    // ── SearchResultRows ────────────────────────────────────────────────────────

    [Fact]
    public void Search_MapsMatchesUnselected_ExcludingAlreadySelected()
    {
        var rows = SelectorModel.SearchResultRows(
            matches: [I("1", "Ada"), I("2", "Babbage"), I("3", "Curie")],
            selectedIds: Ids("2"));

        Assert.Equal(["Ada", "Curie"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.False(r.Selected));
        Assert.All(rows, r => Assert.False(r.Locked));
        Assert.All(rows, r => Assert.False(r.Distinguished));
    }

    [Fact]
    public void Search_DropsBlankNamesBlankIds_AndDeDupes()
    {
        var rows = SelectorModel.SearchResultRows(
            matches: [I("1", "Ada"), I("1", "Ada-dup"), I("", "Blank"), I("2", "   ")],
            selectedIds: Ids());
        Assert.Equal(["Ada"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Search_EmptyMatches_YieldsEmpty()
        => Assert.Empty(SelectorModel.SearchResultRows([], Ids()));

    // ── Toggle ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggle_UnknownId_IsAdded()
        => Assert.Equal(new SelectorToggle(ToggleKind.Added, "7"), SelectorModel.Toggle(Ids("1", "2"), Ids(), "7"));

    [Fact]
    public void Toggle_SelectedUnlocked_IsRemoved()
        => Assert.Equal(new SelectorToggle(ToggleKind.Removed, "2"), SelectorModel.Toggle(Ids("1", "2"), Ids(), "2"));

    [Fact]
    public void Toggle_SelectedLocked_IsNoOp()
        => Assert.Equal(new SelectorToggle(ToggleKind.LockedNoOp, "1"), SelectorModel.Toggle(Ids("1", "2"), Ids("1"), "1"));

    [Fact]
    public void Toggle_LockedButNotSelected_IsAdded()
        => Assert.Equal(new SelectorToggle(ToggleKind.Added, "5"), SelectorModel.Toggle(Ids("1"), Ids("5"), "5"));

    [Fact]
    public void Toggle_DoesNotMutateItsInputs()
    {
        var selected = Ids("1", "2");
        var locked = Ids("1");
        SelectorModel.Toggle(selected, locked, "2");
        Assert.Equal(["1", "2"], selected.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(["1"], locked);
    }

    // ── PruneMarkersToSelection (#370: de-selected seed stops being marked) ──────

    [Fact]
    public void Prune_RemovesIdsNoLongerSelected_KeepsStillSelected_MutatesInPlace()
    {
        var distinguished = Ids("home", "gone");
        var locked = Ids("me", "gone");
        var selected = Ids("home", "me"); // "gone" has been de-selected

        SelectorModel.PruneMarkersToSelection(selected, distinguished, locked);

        Assert.Equal(["home"], distinguished);
        Assert.Equal(["me"], locked);
    }

    [Fact]
    public void Prune_EmptySelection_ClearsEveryMarkerSet()
    {
        var distinguished = Ids("home");
        var locked = Ids("me");

        SelectorModel.PruneMarkersToSelection(Ids(), distinguished, locked);

        Assert.Empty(distinguished);
        Assert.Empty(locked);
    }

    [Fact]
    public void Prune_NoMarkerSets_IsNoOp()
        // The params overload with nothing to prune must not throw.
        => SelectorModel.PruneMarkersToSelection(Ids("1"));

    [Fact]
    public void Prune_ThenEmptyState_DeselectedDistinguishedTopUpRowIsUnmarked()
    {
        // #370 end-to-end at the model seam the View wires together: on a collect-mode removal the View
        // removes the seeded home from the selection and prunes the marker set; a later empty-state
        // render must NOT re-mark the same list when it re-surfaces in the top-frequent pool. Contrast
        // with EmptyState_MarksDistinguishedTopUp_WhenPrimaryIsUnselectedCandidate, which shows the
        // *un*-pruned input still marks — proving the renderer is unchanged and the prune is the fix.
        var distinguished = Ids("home");
        var selectedIds = Ids(); // seeded home de-selected

        SelectorModel.PruneMarkersToSelection(selectedIds, distinguished);

        var rows = SelectorModel.EmptyStateRows(
            selected: [], lockedIds: Ids(), distinguishedIds: distinguished,
            topFrequent: [I("home", "Inbox"), I("2", "Other")], capacity: 10);

        Assert.Equal(new SelectorRow("home", "Inbox", false, false, false), rows[0]); // no stray (home)
        Assert.Equal(new SelectorRow("2", "Other", false, false, false), rows[1]);
    }

    // ── ShouldRunSearch (debounce coalescing) ────────────────────────────────────

    [Fact]
    public void ShouldRunSearch_EqualStamps_Runs()
        => Assert.True(SelectorModel.ShouldRunSearch(5, 5));

    [Fact]
    public void ShouldRunSearch_StaleCapture_Skips()
        => Assert.False(SelectorModel.ShouldRunSearch(5, 6));

    // ── ShouldPickFromSearchBox (add-only search-box Enter guard, #234) ──────────

    [Fact]
    public void ShouldPickFromSearchBox_ActiveQuery_HighlightedUnselected_Picks()
        // A real search: the highlighted match is an addable (unselected) candidate → add it.
        => Assert.True(SelectorModel.ShouldPickFromSearchBox("ad", highlightedId: "7", Ids("1", "2")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void ShouldPickFromSearchBox_BlankQuery_DoesNotPick(string? query)
        // On a blank box the highlighted row is a current-selection ✓ row; a stray Enter must NOT pick
        // (which would remove the first selected entry — the original #234 symptom).
        => Assert.False(SelectorModel.ShouldPickFromSearchBox(query, highlightedId: "1", Ids("1", "2")));

    [Fact]
    public void ShouldPickFromSearchBox_NonBlankQuery_HighlightedAlreadySelected_DoesNotPick()
        // The debounce-window regression: the query box is already non-blank but the still-displayed
        // rows are the empty-state ✓ current-selection rows, so the highlighted id is already selected.
        // Picking would remove it — refuse, so a search-box Enter can never remove (#234).
        => Assert.False(SelectorModel.ShouldPickFromSearchBox("ada", highlightedId: "1", Ids("1", "2")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldPickFromSearchBox_BlankHighlightedId_DoesNotPick(string highlightedId)
        => Assert.False(SelectorModel.ShouldPickFromSearchBox("ada", highlightedId, Ids()));

    [Fact]
    public void ShouldPickFromSearchBox_IdsAreOrdinal_CaseSensitiveSelectionMiss_Picks()
        // Selection membership is ordinal (matching the base's HashSet). A case-different id is a
        // genuinely different, unselected candidate under an active query → addable.
        => Assert.True(SelectorModel.ShouldPickFromSearchBox("abc", highlightedId: "ABC123", Ids("abc123")));
}
