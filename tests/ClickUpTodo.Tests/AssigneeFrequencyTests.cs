using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tally / ranking / matching rules for the assignee-frequency cache (#155) —
/// <see cref="AssigneeFrequency"/>. No I/O, no persistence; the stateful glue is covered by
/// <see cref="AssigneeFrequencyCacheTests"/>.
/// </summary>
public sealed class AssigneeFrequencyTests
{
    private static TaskItem Task(string id, params (long Id, string Name)[] assignees) => new()
    {
        Id = id,
        Name = $"Task {id}",
        Assignees = assignees.Select(a => new TaskAssignee(a.Id, a.Name)).ToList(),
    };

    private static Dictionary<long, AssigneeFrequencyEntry> Tally(params TaskItem[] tasks)
    {
        var acc = new Dictionary<long, AssigneeFrequencyEntry>();
        AssigneeFrequency.Accumulate(acc, tasks);
        return acc;
    }

    [Fact]
    public void Accumulate_CountsOccurrencesAcrossTasks()
    {
        var acc = Tally(
            Task("t1", (1, "Ada"), (2, "Bo")),
            Task("t2", (1, "Ada")),
            Task("t3", (1, "Ada"), (2, "Bo")));

        Assert.Equal(3, acc[1].Count);
        Assert.Equal(2, acc[2].Count);
    }

    [Fact]
    public void Accumulate_RefreshesNameToLatestNonBlank()
    {
        var acc = new Dictionary<long, AssigneeFrequencyEntry>();
        AssigneeFrequency.Accumulate(acc, [Task("t1", (1, "Ada"))]);
        AssigneeFrequency.Accumulate(acc, [Task("t2", (1, "Ada Lovelace"))]);

        Assert.Equal("Ada Lovelace", acc[1].Name);
        Assert.Equal(2, acc[1].Count);
    }

    [Fact]
    public void Accumulate_IgnoresNonPositiveIdAndBlankName()
    {
        var acc = Tally(Task("t1", (0, "Zero"), (-5, "Neg"), (3, "  "), (4, "Cid")));

        Assert.Equal([4], acc.Keys.OrderBy(k => k));
        Assert.Equal("Cid", acc[4].Name);
    }

    [Fact]
    public void Accumulate_TrimsName()
    {
        var acc = Tally(Task("t1", (1, "  Ada  ")));

        Assert.Equal("Ada", acc[1].Name);
    }

    [Fact]
    public void Accumulate_ReturnsFalse_WhenNothingTallied()
    {
        var acc = new Dictionary<long, AssigneeFrequencyEntry>();

        Assert.False(AssigneeFrequency.Accumulate(acc, [Task("t1"), Task("t2", (0, "Zero"))]));
        Assert.Empty(acc);
    }

    [Fact]
    public void TopMostFrequent_RanksByCountThenName()
    {
        var acc = Tally(
            Task("t1", (1, "Ada"), (2, "Bo"), (3, "Cid")),
            Task("t2", (2, "Bo"), (3, "Cid")),
            Task("t3", (3, "Cid")));

        var top = AssigneeFrequency.TopMostFrequent(acc.Values, 3);

        Assert.Equal([3, 2, 1], top.Select(a => a.Id));
    }

    [Fact]
    public void TopMostFrequent_BreaksTiesByNameCaseInsensitiveThenId()
    {
        // All count 1 → tie broken by name (case-insensitive), then id.
        var acc = Tally(Task("t1", (7, "bravo"), (3, "Alpha"), (9, "alpha")));

        var top = AssigneeFrequency.TopMostFrequent(acc.Values, 10);

        // "Alpha"/"alpha" sort together ahead of "bravo"; between the two alphas, lower id (3) wins.
        Assert.Equal([3, 9, 7], top.Select(a => a.Id));
    }

    [Fact]
    public void TopMostFrequent_ExcludesGivenIds()
    {
        var acc = Tally(Task("t1", (1, "Ada"), (2, "Bo"), (3, "Cid")));

        var top = AssigneeFrequency.TopMostFrequent(acc.Values, 10, new HashSet<long> { 2 });

        Assert.DoesNotContain(top, a => a.Id == 2);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void TopMostFrequent_RespectsN()
    {
        var acc = Tally(Task("t1", (1, "Ada"), (2, "Bo"), (3, "Cid")));

        Assert.Equal(2, AssigneeFrequency.TopMostFrequent(acc.Values, 2).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TopMostFrequent_NonPositiveN_IsEmpty(int n)
    {
        var acc = Tally(Task("t1", (1, "Ada")));

        Assert.Empty(AssigneeFrequency.TopMostFrequent(acc.Values, n));
    }

    [Fact]
    public void Match_IsCaseInsensitiveSubstring()
    {
        var acc = Tally(Task("t1", (1, "Ada Lovelace"), (2, "Alan Turing"), (3, "Grace Hopper")));

        var hits = AssigneeFrequency.Match(acc.Values, "la");

        // "Ada Lovelace" (Lovelace) and "Alan Turing" (Alan) both contain "la" case-insensitively.
        Assert.Equal([1, 2], hits.Select(a => a.Id).OrderBy(x => x));
    }

    [Fact]
    public void Match_BlankQuery_ReturnsWholeRankedPool()
    {
        var acc = Tally(
            Task("t1", (1, "Ada"), (2, "Bo")),
            Task("t2", (2, "Bo")));

        var all = AssigneeFrequency.Match(acc.Values, "  ");

        Assert.Equal([2, 1], all.Select(a => a.Id)); // Bo (2) ranks first
    }

    [Fact]
    public void Match_ExcludesGivenIds()
    {
        var acc = Tally(Task("t1", (1, "Ada"), (2, "Alan")));

        var hits = AssigneeFrequency.Match(acc.Values, "a", new HashSet<long> { 1 });

        Assert.Equal([2], hits.Select(a => a.Id));
    }

    [Fact]
    public void Seed_AddsNewPeopleAtCountZero_WithoutClobberingRealCounts()
    {
        var acc = Tally(Task("t1", (1, "Ada")));

        var changed = AssigneeFrequency.Seed(acc,
        [
            new AssigneeFrequencyEntry(1, "Different Name", 0), // existing — must not be clobbered
            new AssigneeFrequencyEntry(5, "Newcomer", 0),       // genuinely new
        ]);

        Assert.True(changed);
        Assert.Equal(1, acc[1].Count);          // real count preserved
        Assert.Equal("Ada", acc[1].Name);       // known name preserved
        Assert.Equal(0, acc[5].Count);
        Assert.Equal("Newcomer", acc[5].Name);
    }

    [Fact]
    public void Seed_IgnoresInvalidAndReturnsFalse_WhenNothingAdded()
    {
        var acc = Tally(Task("t1", (1, "Ada")));

        var changed = AssigneeFrequency.Seed(acc,
        [
            new AssigneeFrequencyEntry(0, "Zero", 0),
            new AssigneeFrequencyEntry(2, "  ", 0),
            new AssigneeFrequencyEntry(1, "Ada", 0), // already present
        ]);

        Assert.False(changed);
        Assert.Single(acc);
    }

    [Fact]
    public void SeededZeroCountPeople_RankBelowTallied_ButStillAppear()
    {
        var acc = Tally(Task("t1", (1, "Ada")));
        AssigneeFrequency.Seed(acc, [new AssigneeFrequencyEntry(5, "Newcomer", 0)]);

        var top = AssigneeFrequency.TopMostFrequent(acc.Values, 10);

        Assert.Equal([1, 5], top.Select(a => a.Id)); // Ada (count 1) ahead of Newcomer (count 0)
    }
}
