using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure <see cref="MentionPickerModel"/> backing the reusable
/// <see cref="MentionPickerView"/> (#324): row rendering for the empty-search and type-ahead states,
/// the add/remove toggle decision (mentions carry no locked entry), the debounce coalescing decision,
/// and the member/row → <see cref="MentionTarget"/> mapping that keys a pick on the userId rather than
/// the raw typed text (so spaced display names submit correctly — #323). Mirrors
/// <see cref="AssigneeSelectorModelTests"/> / <see cref="SelectorModelTests"/>.
/// </summary>
public sealed class MentionPickerModelTests
{
    // A member with an explicit username (ClickUp's spaced display name); email left null.
    private static WorkspaceMember M(long id, string username) => new(id, username, null);

    private static ISet<long> Ids(params long[] ids) => new HashSet<long>(ids);

    // ── Format ────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_Selected_LeadsWithCheck()
        => Assert.Equal("✓ Ada Lovelace", MentionPickerModel.Format(new MemberRow(1, "Ada Lovelace", Selected: true)));

    [Fact]
    public void Format_Unselected_LeadsWithTwoSpaces()
        => Assert.Equal("  Ada Lovelace", MentionPickerModel.Format(new MemberRow(1, "Ada Lovelace", Selected: false)));

    // ── EmptyStateRows ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyState_SelectedFirstWithCheck_ThenTopUpExcludingSelected()
    {
        var rows = MentionPickerModel.EmptyStateRows(
            selected: [M(1, "Ada"), M(2, "Babbage")],
            topFrequent: [M(2, "Babbage"), M(3, "Curie"), M(4, "Dijkstra")],
            capacity: 10);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new MemberRow(1, "Ada", true), rows[0]);
        Assert.Equal(new MemberRow(2, "Babbage", true), rows[1]); // selected — not re-added by top-up
        Assert.Equal(new MemberRow(3, "Curie", false), rows[2]);
        Assert.Equal(new MemberRow(4, "Dijkstra", false), rows[3]);
    }

    [Fact]
    public void EmptyState_NothingSelected_YieldsRankedPool()
    {
        // The picker's real opening state: nothing chosen, so the empty state is just the top pool.
        var rows = MentionPickerModel.EmptyStateRows(
            selected: [], topFrequent: [M(3, "Curie"), M(4, "Dijkstra")], capacity: 10);

        Assert.Equal(["Curie", "Dijkstra"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.False(r.Selected));
    }

    [Fact]
    public void EmptyState_CapacityBoundsOnlyTheTopUp()
    {
        // capacity 3, one selected → at most 2 top-up rows.
        var rows = MentionPickerModel.EmptyStateRows(
            selected: [M(1, "Ada")],
            topFrequent: [M(2, "B"), M(3, "C"), M(4, "D"), M(5, "E")],
            capacity: 3);

        Assert.Equal(["Ada", "B", "C"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_SelectedAlwaysShown_EvenAboveCapacity()
    {
        var rows = MentionPickerModel.EmptyStateRows(
            selected: [M(1, "A"), M(2, "B"), M(3, "C"), M(4, "D")],
            topFrequent: [M(9, "Z")],
            capacity: 2);

        Assert.Equal(["A", "B", "C", "D"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.True(r.Selected));
    }

    [Fact]
    public void EmptyState_DropsNonPositiveIds_AndDeDupes()
    {
        var rows = MentionPickerModel.EmptyStateRows(
            selected: [M(1, "Ada"), M(0, "Zero"), M(1, "Ada-dup")],
            topFrequent: [M(1, "Ada"), M(3, "Curie"), M(-5, "Neg")],
            capacity: 10);

        Assert.Equal(["Ada", "Curie"], rows.Select(r => r.Name));
    }

    [Fact]
    public void EmptyState_UsesDisplayName_ForEmailOnlyAndNamelessMembers()
    {
        // DisplayName (#323): username → email local part → "User {id}"; never blank, so no blank rows.
        var rows = MentionPickerModel.EmptyStateRows(
            selected: [],
            topFrequent: [new WorkspaceMember(1, "Ada Lovelace", "ada@x.io"), new WorkspaceMember(2, null, "grace@x.io"), new WorkspaceMember(3, null, null)],
            capacity: 10);

        Assert.Equal(["Ada Lovelace", "grace", "User 3"], rows.Select(r => r.Name));
    }

    // ── SearchResultRows ────────────────────────────────────────────────────────

    [Fact]
    public void Search_MapsMatchesUnselected_ExcludingAlreadySelected()
    {
        var rows = MentionPickerModel.SearchResultRows(
            matches: [M(1, "Ada"), M(2, "Babbage"), M(3, "Curie")],
            selectedIds: Ids(2));

        Assert.Equal(["Ada", "Curie"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.False(r.Selected));
    }

    [Fact]
    public void Search_DropsNonPositiveIds_AndDeDupes()
    {
        var rows = MentionPickerModel.SearchResultRows(
            matches: [M(1, "Ada"), M(1, "Ada-dup"), M(0, "Zero")],
            selectedIds: Ids());
        Assert.Equal(["Ada"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Search_EmptyMatches_YieldsEmpty()
        => Assert.Empty(MentionPickerModel.SearchResultRows([], Ids()));

    // ── Toggle (no locked entry) ──────────────────────────────────────────────────

    [Fact]
    public void Toggle_UnknownId_IsAdded()
        => Assert.Equal(new MemberToggleResult(ToggleKind.Added, 7), MentionPickerModel.Toggle(Ids(1, 2), 7));

    [Fact]
    public void Toggle_SelectedId_IsRemoved()
        => Assert.Equal(new MemberToggleResult(ToggleKind.Removed, 2), MentionPickerModel.Toggle(Ids(1, 2), 2));

    [Fact]
    public void Toggle_NeverLockedNoOp()
    {
        // Mentions have no locked default, so a selected member is always removable.
        Assert.Equal(ToggleKind.Removed, MentionPickerModel.Toggle(Ids(5), 5).Kind);
        Assert.Equal(ToggleKind.Added, MentionPickerModel.Toggle(Ids(), 5).Kind);
    }

    [Fact]
    public void Toggle_DoesNotMutateItsInput()
    {
        var selected = Ids(1, 2);
        MentionPickerModel.Toggle(selected, 2);
        Assert.Equal([1L, 2L], selected.OrderBy(x => x));
    }

    // ── ShouldRunSearch (debounce coalescing) ────────────────────────────────────

    [Fact]
    public void ShouldRunSearch_EqualStamps_Runs()
        => Assert.True(MentionPickerModel.ShouldRunSearch(5, 5));

    [Fact]
    public void ShouldRunSearch_StaleCapture_Skips()
        => Assert.False(MentionPickerModel.ShouldRunSearch(5, 6));

    // ── ToTarget (pick → { userId, displayName }) ─────────────────────────────────

    [Fact]
    public void ToTarget_FromMember_UsesIdAndDisplayName()
        => Assert.Equal(new MentionTarget(42, "Ada Lovelace"), MentionPickerModel.ToTarget(M(42, "Ada Lovelace")));

    [Fact]
    public void ToTarget_SpacedName_SubmitsById_NotTypedText()
    {
        // The whole reason J (#324) gated on I (#323): a spaced display name must round-trip by userId,
        // never by parsing the name. ToItem carries the id into the row; ToTarget reads it back.
        var item = MentionPickerModel.ToItem(M(99, "Ben Seymour"));
        var target = MentionPickerModel.ToTarget(item);
        Assert.Equal(new MentionTarget(99, "Ben Seymour"), target);
    }

    [Fact]
    public void ToTarget_FromMember_MatchesDisplayNameFallbacks()
    {
        Assert.Equal("grace", MentionPickerModel.ToTarget(new WorkspaceMember(2, null, "grace@x.io")).DisplayName);
        Assert.Equal("User 3", MentionPickerModel.ToTarget(new WorkspaceMember(3, null, null)).DisplayName);
    }

    // ── NewlyAnnounced (raise MemberPicked exactly once per pick) ─────────────────

    private static MentionTarget T(long id, string name) => new(id, name);

    [Fact]
    public void NewlyAnnounced_FirstPick_IsAnnouncedAndRecorded()
    {
        var announced = Ids();
        var fresh = MentionPickerModel.NewlyAnnounced(announced, [T(1, "Ada")]);
        Assert.Equal([T(1, "Ada")], fresh);
        Assert.Equal([1L], announced.OrderBy(x => x));
    }

    [Fact]
    public void NewlyAnnounced_UnchangedSelection_AnnouncesNothingAgain()
    {
        var announced = Ids(1);
        Assert.Empty(MentionPickerModel.NewlyAnnounced(announced, [T(1, "Ada")]));
        Assert.Equal([1L], announced.OrderBy(x => x));
    }

    [Fact]
    public void NewlyAnnounced_DeselectThenRePick_AnnouncesAgain()
    {
        var announced = Ids(1);
        // De-selected (selection now empty): the id is forgotten.
        Assert.Empty(MentionPickerModel.NewlyAnnounced(announced, []));
        Assert.Empty(announced);
        // Re-picked: announced afresh.
        Assert.Equal([T(1, "Ada")], MentionPickerModel.NewlyAnnounced(announced, [T(1, "Ada")]));
    }

    [Fact]
    public void NewlyAnnounced_MultipleNewPicks_EachAnnouncedOnceInOrder()
    {
        var announced = Ids(1);
        var fresh = MentionPickerModel.NewlyAnnounced(announced, [T(1, "Ada"), T(2, "Babbage"), T(3, "Curie")]);
        Assert.Equal([T(2, "Babbage"), T(3, "Curie")], fresh); // 1 already announced, kept
        Assert.Equal([1L, 2L, 3L], announced.OrderBy(x => x));
    }

    [Fact]
    public void NewlyAnnounced_DropsNonPositiveTargets()
    {
        var announced = Ids();
        Assert.Empty(MentionPickerModel.NewlyAnnounced(announced, [T(0, "Zero")]));
        Assert.Empty(announced);
    }
}
