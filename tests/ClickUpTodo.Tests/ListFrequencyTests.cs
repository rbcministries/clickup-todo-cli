using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tally / ranking / matching rules for the list-frequency cache (#238) —
/// <see cref="ListFrequency"/>. Counting is by distinct task id, so no I/O and no per-poll inflation;
/// the stateful glue is covered by <see cref="ListFrequencyCacheTests"/>. Mirrors
/// <see cref="AssigneeFrequencyTests"/>, keyed by the list's string id.
/// </summary>
public sealed class ListFrequencyTests
{
    private static TaskItem Task(string id, string? listId, string? listName) => new()
    {
        Id = id,
        Name = $"Task {id}",
        ListId = listId,
        ListName = listName,
    };

    private static Dictionary<string, ListFrequencyEntry> Tally(params TaskItem[] tasks)
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);
        ListFrequency.Accumulate(acc, tasks);
        return acc;
    }

    [Fact]
    public void Accumulate_CountsDistinctTasksPerList()
    {
        var acc = Tally(
            Task("t1", "L1", "Alpha"),
            Task("t2", "L1", "Alpha"),
            Task("t3", "L2", "Beta"),
            Task("t4", "L1", "Alpha"));

        Assert.Equal(3, acc["L1"].Count); // t1, t2, t4
        Assert.Equal(1, acc["L2"].Count); // t3
    }

    [Fact]
    public void Accumulate_IsIdempotent_ReObservingTheSameTaskDoesNotInflate()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);

        // First "poll": records t1 for L1.
        Assert.True(ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha")]));
        // A later poll returns the same task — must not bump the count and must report no change, so the
        // caller doesn't re-persist (fixes the per-poll inflation / hot-path write).
        Assert.False(ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha")]));

        Assert.Equal(1, acc["L1"].Count);
    }

    [Fact]
    public void Accumulate_RefreshesNameToLatestNonBlank()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);
        ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha")]);
        ListFrequency.Accumulate(acc, [Task("t2", "L1", "Alpha (renamed)")]);

        Assert.Equal("Alpha (renamed)", acc["L1"].Name);
        Assert.Equal(2, acc["L1"].Count);
    }

    [Fact]
    public void Accumulate_NameChangeOnAlreadyRecordedTask_UpdatesName_WithoutInflating()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);
        ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha")]);

        // Same task id, new list name → a change (name refresh) but the distinct-task count stays 1.
        Assert.True(ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha!")]));
        Assert.Equal("Alpha!", acc["L1"].Name);
        Assert.Equal(1, acc["L1"].Count);
    }

    [Fact]
    public void Accumulate_IgnoresBlankListIdAndBlankName()
    {
        var acc = Tally(
            Task("t1", null, "NoId"),
            Task("t2", "  ", "Blank"),
            Task("t3", "L3", "   "),
            Task("t4", "L4", "Cid"));

        Assert.Equal(["L4"], acc.Keys.OrderBy(k => k));
        Assert.Equal("Cid", acc["L4"].Name);
    }

    [Fact]
    public void Accumulate_IgnoresTaskWithBlankId()
    {
        var acc = Tally(Task("", "L1", "Alpha"));

        Assert.Empty(acc);
    }

    [Fact]
    public void Accumulate_TrimsListIdAndName()
    {
        var acc = Tally(Task("t1", "  L1  ", "  Alpha  "));

        Assert.Equal("L1", acc.Keys.Single());
        Assert.Equal("Alpha", acc["L1"].Name);
    }

    [Fact]
    public void Accumulate_ReturnsFalse_WhenNothingTallied()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);

        Assert.False(ListFrequency.Accumulate(acc, [Task("t1", null, "NoId"), Task("t2", "L2", "  ")]));
        Assert.Empty(acc);
    }

    [Fact]
    public void TopMostFrequent_RanksByCountThenName()
    {
        var acc = Tally(
            Task("t1", "L1", "Alpha"),
            Task("t2", "L1", "Alpha"),
            Task("t3", "L1", "Alpha"),
            Task("t4", "L2", "Beta"),
            Task("t5", "L2", "Beta"),
            Task("t6", "L3", "Cid"));

        var top = ListFrequency.TopMostFrequent(acc.Values, 3);

        Assert.Equal(["L1", "L2", "L3"], top.Select(l => l.Id));
    }

    [Fact]
    public void TopMostFrequent_BreaksTiesByNameCaseInsensitiveThenId()
    {
        // All count 1 → tie broken by name (case-insensitive), then id (ordinal).
        var acc = Tally(
            Task("t1", "z", "bravo"),
            Task("t2", "a3", "Alpha"),
            Task("t3", "a9", "alpha"));

        var top = ListFrequency.TopMostFrequent(acc.Values, 10);

        // "Alpha"/"alpha" sort together ahead of "bravo"; between the two, ordinal id "a3" < "a9".
        Assert.Equal(["a3", "a9", "z"], top.Select(l => l.Id));
    }

    [Fact]
    public void TopMostFrequent_ExcludesGivenIds()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"), Task("t2", "L2", "Beta"), Task("t3", "L3", "Cid"));

        var top = ListFrequency.TopMostFrequent(acc.Values, 10, new HashSet<string> { "L2" });

        Assert.DoesNotContain(top, l => l.Id == "L2");
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void TopMostFrequent_RespectsN()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"), Task("t2", "L2", "Beta"), Task("t3", "L3", "Cid"));

        Assert.Equal(2, ListFrequency.TopMostFrequent(acc.Values, 2).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TopMostFrequent_NonPositiveN_IsEmpty(int n)
    {
        var acc = Tally(Task("t1", "L1", "Alpha"));

        Assert.Empty(ListFrequency.TopMostFrequent(acc.Values, n));
    }

    [Fact]
    public void Match_IsCaseInsensitiveSubstring()
    {
        var acc = Tally(
            Task("t1", "L1", "Backlog"),
            Task("t2", "L2", "Blocked"),
            Task("t3", "L3", "Done"));

        var hits = ListFrequency.Match(acc.Values, "lo");

        // "Backlog" (…lo g) and "Blocked" (b lo cked) both contain "lo"; "Done" does not.
        Assert.Equal(["L1", "L2"], hits.Select(l => l.Id).OrderBy(x => x));
    }

    [Fact]
    public void Match_BlankQuery_ReturnsWholeRankedPool()
    {
        var acc = Tally(
            Task("t1", "L1", "Alpha"),
            Task("t2", "L2", "Beta"),
            Task("t3", "L2", "Beta"));

        var all = ListFrequency.Match(acc.Values, "  ");

        Assert.Equal(["L2", "L1"], all.Select(l => l.Id)); // Beta (2) ranks first
    }

    [Fact]
    public void Match_ExcludesGivenIds()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"), Task("t2", "L2", "Amber"));

        var hits = ListFrequency.Match(acc.Values, "a", new HashSet<string> { "L1" });

        Assert.Equal(["L2"], hits.Select(l => l.Id));
    }

    [Fact]
    public void Seed_AddsNewListsAtCountZero_WithoutClobberingRealCounts()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"));

        var changed = ListFrequency.Seed(acc,
        [
            new NamedEntity("L1", "Different Name"), // existing — must not be clobbered
            new NamedEntity("L5", "Newcomer"),       // genuinely new
        ]);

        Assert.True(changed);
        Assert.Equal(1, acc["L1"].Count);         // real distinct-task count preserved
        Assert.Equal("Alpha", acc["L1"].Name);    // known name preserved
        Assert.Equal(0, acc["L5"].Count);
        Assert.Equal("Newcomer", acc["L5"].Name);
    }

    [Fact]
    public void Seed_IgnoresInvalidAndReturnsFalse_WhenNothingAdded()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"));

        var changed = ListFrequency.Seed(acc,
        [
            new NamedEntity("", "Blank Id"),
            new NamedEntity("L2", "  "),
            new NamedEntity("L1", "Alpha"), // already present
        ]);

        Assert.False(changed);
        Assert.Single(acc);
    }

    [Fact]
    public void SeededZeroCountLists_RankBelowTallied_ButStillAppear()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"));
        ListFrequency.Seed(acc, [new NamedEntity("L5", "Newcomer")]);

        var top = ListFrequency.TopMostFrequent(acc.Values, 10);

        Assert.Equal(["L1", "L5"], top.Select(l => l.Id)); // Alpha (count 1) ahead of Newcomer (count 0)
    }

    [Fact]
    public void Seed_ThenAccumulateSameList_PromotesToCountOne_AndRefreshesName()
    {
        // The two-feed interaction: the walk seeds a list at count 0, then a task on that same list
        // arrives on a later refresh. The seeded entry (empty TaskIds) must promote to count 1 and
        // adopt the task's list name — not stay stuck at 0 or spawn a duplicate.
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);
        ListFrequency.Seed(acc, [new NamedEntity("L1", "Archive")]);
        Assert.Equal(0, acc["L1"].Count);

        var changed = ListFrequency.Accumulate(acc, [Task("t1", "L1", "Archive (renamed)")]);

        Assert.True(changed);
        Assert.Single(acc);                          // promoted in place, no duplicate
        Assert.Equal(1, acc["L1"].Count);            // 0 → 1
        Assert.Equal("Archive (renamed)", acc["L1"].Name);
    }

    // --- Merge (#293): union a concurrent tab's document into ours without losing either side --------

    [Fact]
    public void Merge_UnionsDistinctTaskSets_AndAddsUnknownLists()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);
        ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha")]);   // ours: L1 on {t1}

        var changed = ListFrequency.Merge(acc,
        [
            new ListFrequencyEntry("L1", "Alpha", ["t1", "t2"]),      // other tab saw L1 on t2 too
            new ListFrequencyEntry("L2", "Beta", ["t3"]),             // other tab learned L2
        ]);

        Assert.True(changed);
        Assert.Equal(2, acc["L1"].Count);            // {t1} ∪ {t1,t2} = {t1,t2}
        Assert.Equal(["t1", "t2"], acc["L1"].TaskIds);
        Assert.Equal(1, acc["L2"].Count);            // Beta adopted
        Assert.Equal("Beta", acc["L2"].Name);
    }

    [Fact]
    public void Merge_IsIdempotent_MergingAnEqualOrSubsetSetChangesNothing()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);
        ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha"), Task("t2", "L1", "Alpha")]);

        Assert.False(ListFrequency.Merge(acc, [new ListFrequencyEntry("L1", "Alpha", ["t1", "t2"])]));
        Assert.False(ListFrequency.Merge(acc, [new ListFrequencyEntry("L1", "Alpha", ["t1"])]));
        Assert.Equal(2, acc["L1"].Count);
    }

    [Fact]
    public void Merge_KeepsOurKnownName_AndIgnoresInvalidRows()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);
        ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha")]);

        ListFrequency.Merge(acc,
        [
            new ListFrequencyEntry("L1", "STALE", ["t1"]), // our non-blank name wins over the incoming one
            new ListFrequencyEntry("  ", "Blank", ["t9"]), // blank id ignored
            new ListFrequencyEntry("L3", "  ", ["t9"]),    // nameless new entry ignored
        ]);

        Assert.Equal("Alpha", acc["L1"].Name);
        Assert.DoesNotContain("L3", acc.Keys);
        Assert.Single(acc);
    }
}
