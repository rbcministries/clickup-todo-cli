using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="SubtaskFetchStrategy.Plan"/> — the adaptive choice of fetch shape (#87):
/// per-parent for few / sparse parents, whole-list for parents that cluster densely in a list, bounded by
/// caps that flag truncation rather than silently under-fetching. The pure <em>selection</em> of which
/// fetched tasks to keep is tested separately in <see cref="ForeignDescendantsTests"/>.
/// </summary>
public sealed class SubtaskFetchStrategyTests
{
    private static TaskItem Task(string id, string? list = "L")
        => new() { Id = id, Name = id, ListId = list };

    // n parents all in the same list, ids "{list}-0".."{list}-{n-1}".
    private static IEnumerable<TaskItem> InList(string list, int n)
        => Enumerable.Range(0, n).Select(i => Task($"{list}-{i}", list));

    [Fact]
    public void EmptySnapshot_EmptyPlan()
    {
        var plan = SubtaskFetchStrategy.Plan([]);

        Assert.Empty(plan.WholeListIds);
        Assert.Empty(plan.PerParentIds);
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void FewParents_AllPerParent_NoWholeList()
    {
        // At/under PerParentThreshold (default 8) we stay per-parent even if a list is dense — identical
        // to the pre-#87 behaviour and cross-list correct.
        TaskItem[] snapshot = [.. InList("A", 5), .. InList("B", 3)]; // 8 total == threshold
        var plan = SubtaskFetchStrategy.Plan(snapshot);

        Assert.Empty(plan.WholeListIds);
        Assert.Equal(snapshot.Select(t => t.Id), plan.PerParentIds); // stable snapshot order
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void DistinctIds_RepeatedIdCountedOnce_EmptyIdsSkipped()
    {
        TaskItem[] snapshot = [Task("a"), Task("a"), Task(""), Task("b")];
        var plan = SubtaskFetchStrategy.Plan(snapshot);

        Assert.Equal(["a", "b"], plan.PerParentIds);
    }

    [Fact]
    public void ManyParents_DenseList_RoutedWhole_SparseRemainderPerParent()
    {
        // 9 > threshold(8): list "A" holds 5 (>= WholeListMinParents 4) so it goes whole; the four
        // single-parent lists stay per-parent.
        TaskItem[] snapshot =
        [
            .. InList("A", 5),
            Task("b", "B"), Task("c", "C"), Task("d", "D"), Task("e", "E"),
        ];
        var plan = SubtaskFetchStrategy.Plan(snapshot);

        Assert.Equal(["A"], plan.WholeListIds);
        Assert.Equal(["b", "c", "d", "e"], plan.PerParentIds); // dense list's parents dropped from per-parent
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void MultipleDenseLists_OrderedByParentCountDesc_ThenListIdAsc()
    {
        // B(6) and A(4) are both dense; ordered by count desc so B leads. Total 10 > threshold.
        TaskItem[] snapshot = [.. InList("A", 4), .. InList("B", 6)];
        var plan = SubtaskFetchStrategy.Plan(snapshot);

        Assert.Equal(["B", "A"], plan.WholeListIds);
        Assert.Empty(plan.PerParentIds);
    }

    [Fact]
    public void DenseLists_TiedCount_OrderedByListIdAsc()
    {
        TaskItem[] snapshot = [.. InList("B", 5), .. InList("A", 5)]; // both 5, 10 total
        var plan = SubtaskFetchStrategy.Plan(snapshot);

        Assert.Equal(["A", "B"], plan.WholeListIds); // tie broken by list id ascending
    }

    [Fact]
    public void NullOrEmptyListId_AlwaysPerParent_NeverWhole()
    {
        // Ten parents with no known list: can only be reached per-parent, regardless of count.
        TaskItem[] snapshot = [.. Enumerable.Range(0, 10).Select(i => Task($"t{i}", list: null))];
        var plan = SubtaskFetchStrategy.Plan(snapshot);

        Assert.Empty(plan.WholeListIds);
        Assert.Equal(10, plan.PerParentIds.Count);
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void ListBelowWholeListMinParents_StaysPerParent()
    {
        // List "A" has 3 (< min 4); no list qualifies -> everything per-parent even though total > threshold.
        TaskItem[] snapshot =
        [
            .. InList("A", 3),
            Task("b", "B"), Task("c", "C"), Task("d", "D"), Task("e", "E"), Task("f", "F"), Task("g", "G"),
        ];
        var plan = SubtaskFetchStrategy.Plan(snapshot);

        Assert.Empty(plan.WholeListIds);
        Assert.Equal(9, plan.PerParentIds.Count);
    }

    [Fact]
    public void WholeListMinParents_ClampedToTwo_WhenConfiguredLower()
    {
        // WholeListMinParents=1 would make every non-empty list dense; clamp to 2 keeps singletons per-parent.
        var opts = new SubtaskFetchOptions(PerParentThreshold: 0, WholeListMinParents: 1);
        TaskItem[] snapshot = [.. InList("A", 2), Task("b", "B")];
        var plan = SubtaskFetchStrategy.Plan(snapshot, opts);

        Assert.Equal(["A"], plan.WholeListIds);
        Assert.Equal(["b"], plan.PerParentIds);
    }

    [Fact]
    public void WholeListCap_TruncatesAndFlags()
    {
        var opts = new SubtaskFetchOptions(PerParentThreshold: 0, WholeListMinParents: 2, MaxWholeListFetches: 2);
        TaskItem[] snapshot = [.. InList("A", 2), .. InList("B", 2), .. InList("C", 2)]; // 3 dense lists
        var plan = SubtaskFetchStrategy.Plan(snapshot, opts);

        Assert.Equal(2, plan.WholeListIds.Count);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void PerParentCap_TruncatesAndFlags()
    {
        var opts = new SubtaskFetchOptions(PerParentThreshold: 0, WholeListMinParents: 100, MaxPerParentFetches: 2);
        TaskItem[] snapshot = [Task("a", "A"), Task("b", "B"), Task("c", "C"), Task("d", "D")];
        var plan = SubtaskFetchStrategy.Plan(snapshot, opts);

        Assert.Equal(2, plan.PerParentIds.Count);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void UnderCaps_NotTruncated()
    {
        var opts = new SubtaskFetchOptions(PerParentThreshold: 0, WholeListMinParents: 2, MaxWholeListFetches: 5, MaxPerParentFetches: 5);
        TaskItem[] snapshot = [.. InList("A", 2), Task("b", "B")];
        var plan = SubtaskFetchStrategy.Plan(snapshot, opts);

        Assert.False(plan.Truncated);
    }
}
