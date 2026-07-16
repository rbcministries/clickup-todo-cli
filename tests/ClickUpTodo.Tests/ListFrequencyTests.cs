using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tally / ranking / matching rules for the list-frequency cache (#238) —
/// <see cref="ListFrequency"/>. Counting is by distinct task id, so no I/O and no per-poll inflation;
/// the stateful glue is covered by <see cref="ListFrequencyCacheTests"/>. Mirrors
/// <see cref="AssigneeFrequencyTests"/>, keyed by string list id.
/// </summary>
public sealed class ListFrequencyTests
{
    private static TaskItem Task(string id, string listId, string listName) => new()
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
    public void Accumulate_CountsDistinctTasksAcrossTasks()
    {
        var acc = Tally(
            Task("t1", "L1", "Alpha"),
            Task("t2", "L1", "Alpha"),
            Task("t3", "L2", "Beta"));

        Assert.Equal(2, acc["L1"].Count); // t1, t2
        Assert.Equal(1, acc["L2"].Count); // t3
    }

    [Fact]
    public void Accumulate_IsIdempotent_ReObservingTheSameTaskDoesNotInflate()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);

        // First "poll": records t1 for list L1.
        Assert.True(ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha")]));
        // A later poll returns the same task — must not bump the count and must report no change, so
        // the caller doesn't re-persist (the per-poll inflation / hot-path write guard).
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

        // Same task id + list, new name → a change (name refresh) but the distinct-task count stays 1.
        Assert.True(ListFrequency.Accumulate(acc, [Task("t1", "L1", "Alpha!")]));
        Assert.Equal("Alpha!", acc["L1"].Name);
        Assert.Equal(1, acc["L1"].Count);
    }

    [Fact]
    public void Accumulate_IgnoresBlankListIdAndBlankListName()
    {
        var acc = Tally(
            Task("t1", "", "NoId"),
            Task("t2", "   ", "Whitespace"),
            Task("t3", "L3", "  "),
            Task("t4", "L4", "Kept"));

        Assert.Equal(["L4"], acc.Keys);
        Assert.Equal("Kept", acc["L4"].Name);
    }

    [Fact]
    public void Accumulate_IgnoresTaskWithBlankId()
    {
        var acc = Tally(Task("", "L1", "Alpha"));

        Assert.Empty(acc);
    }

    [Fact]
    public void Accumulate_TrimsIdAndName()
    {
        var acc = Tally(Task("t1", "  L1  ", "  Alpha  "));

        Assert.Equal(["L1"], acc.Keys);
        Assert.Equal("Alpha", acc["L1"].Name);
    }

    [Fact]
    public void Accumulate_ReturnsFalse_WhenNothingTallied()
    {
        var acc = new Dictionary<string, ListFrequencyEntry>(StringComparer.Ordinal);

        Assert.False(ListFrequency.Accumulate(acc, [Task("t1", "", "NoId"), Task("t2", "L2", "  ")]));
        Assert.Empty(acc);
    }

    [Fact]
    public void TopMostFrequent_RanksByCountThenName()
    {
        var acc = Tally(
            Task("t1", "L1", "Alpha"),
            Task("t2", "L2", "Beta"), Task("t3", "L2", "Beta"),
            Task("t4", "L3", "Gamma"), Task("t5", "L3", "Gamma"), Task("t6", "L3", "Gamma"));

        var top = ListFrequency.TopMostFrequent(acc.Values, 3);

        Assert.Equal(["L3", "L2", "L1"], top.Select(l => l.Id));
    }

    [Fact]
    public void TopMostFrequent_BreaksTiesByNameCaseInsensitiveThenId()
    {
        // All count 1 → tie broken by name (case-insensitive), then id (ordinal).
        var acc = Tally(
            Task("t1", "id7", "bravo"),
            Task("t2", "id3", "Alpha"),
            Task("t3", "id9", "alpha"));

        var top = ListFrequency.TopMostFrequent(acc.Values, 10);

        // "Alpha"/"alpha" sort together ahead of "bravo"; between the two alphas, "id3" < "id9" ordinally.
        Assert.Equal(["id3", "id9", "id7"], top.Select(l => l.Id));
    }

    [Fact]
    public void TopMostFrequent_ExcludesGivenIds()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"), Task("t2", "L2", "Beta"), Task("t3", "L3", "Gamma"));

        var top = ListFrequency.TopMostFrequent(acc.Values, 10, new HashSet<string>(StringComparer.Ordinal) { "L2" });

        Assert.DoesNotContain(top, l => l.Id == "L2");
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void TopMostFrequent_RespectsN()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"), Task("t2", "L2", "Beta"), Task("t3", "L3", "Gamma"));

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
            Task("t1", "L1", "Engineering"),
            Task("t2", "L2", "Marketing"),
            Task("t3", "L3", "Design"));

        var hits = ListFrequency.Match(acc.Values, "IN");

        // "Engineering" and "Marketing" contain "in" case-insensitively; "Design" (d-e-s-i-g-n) does not.
        Assert.Equal(["L1", "L2"], hits.Select(l => l.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Match_BlankQuery_ReturnsWholeRankedPool()
    {
        var acc = Tally(
            Task("t1", "L1", "Alpha"),
            Task("t2", "L2", "Beta"), Task("t3", "L2", "Beta"));

        var all = ListFrequency.Match(acc.Values, "  ");

        Assert.Equal(["L2", "L1"], all.Select(l => l.Id)); // Beta (2) ranks first
    }

    [Fact]
    public void Match_ExcludesGivenIds()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"), Task("t2", "L2", "Applied"));

        var hits = ListFrequency.Match(acc.Values, "a", new HashSet<string>(StringComparer.Ordinal) { "L1" });

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
        Assert.Equal(1, acc["L1"].Count);        // real distinct-task count preserved
        Assert.Equal("Alpha", acc["L1"].Name);   // known name preserved
        Assert.Equal(0, acc["L5"].Count);
        Assert.Equal("Newcomer", acc["L5"].Name);
    }

    [Fact]
    public void Seed_IgnoresInvalidAndReturnsFalse_WhenNothingAdded()
    {
        var acc = Tally(Task("t1", "L1", "Alpha"));

        var changed = ListFrequency.Seed(acc,
        [
            new NamedEntity("", "Blank id"),
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
}
