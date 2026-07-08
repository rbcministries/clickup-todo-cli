using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure adaptive foreign-subtask fetch planner (#87):
/// <see cref="TaskService.ChooseSubtaskFetchStrategy"/> (per-parent vs whole-list) and
/// <see cref="TaskService.PlanSubtaskFetch"/> (id selection, dedup order, worst-case cap).
/// </summary>
public sealed class SubtaskFetchPlanTests
{
    private static readonly TaskService.SubtaskFetchTuning Tune =
        new(MinParentsForWholeList: 8, ClusterRatio: 3, MaxRoundTrips: 200);

    private static TaskItem Task(string id, string? list = "L")
        => new() { Id = id, Name = id, ListId = list };

    private static IReadOnlyList<string> Ids(IEnumerable<TaskItem> tasks) => tasks.Select(t => t.Id).ToList();

    // ── ChooseSubtaskFetchStrategy ──────────────────────────────────────────

    [Fact]
    public void FewParents_StaysPerParent_EvenWhenTightlyClustered()
    {
        // 7 parents in 1 list is very clustered, but below MinParentsForWholeList the per-parent
        // fetch is already cheap (and cross-list-correct), so it wins.
        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 7, listCount: 1, Tune));
    }

    [Fact]
    public void AtParentThreshold_WithRealClustering_SwitchesToWholeList()
    {
        // 8 parents (== MinParentsForWholeList) across 2 lists → 4 per list ≥ ClusterRatio(3).
        Assert.Equal(TaskService.SubtaskFetchStrategy.WholeList,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 8, listCount: 2, Tune));
    }

    [Fact]
    public void ClusterRatioExactlyMet_SwitchesToWholeList()
    {
        // 9 parents across 3 lists → exactly 3 per list == ClusterRatio.
        Assert.Equal(TaskService.SubtaskFetchStrategy.WholeList,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 9, listCount: 3, Tune));
    }

    [Fact]
    public void ClusterRatioJustUnder_StaysPerParent()
    {
        // 8 parents across 3 lists → 2.67 per list < ClusterRatio(3): whole-list wouldn't cut
        // round-trips enough to justify the larger payloads, so per-parent wins.
        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 8, listCount: 3, Tune));
    }

    [Fact]
    public void ManyParentsSpreadAcrossManyLists_StaysPerParent()
    {
        // 20 parents in 20 distinct lists: L == P, whole-list saves nothing and only adds payload.
        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 20, listCount: 20, Tune));
    }

    [Fact]
    public void ManyParentsHeavilyClustered_SwitchesToWholeList()
    {
        // 60 parents in 2 lists: whole-list turns 60 round-trips into 2.
        Assert.Equal(TaskService.SubtaskFetchStrategy.WholeList,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 60, listCount: 2, Tune));
    }

    [Fact]
    public void DegenerateCounts_FallBackToPerParent()
    {
        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 0, listCount: 0, Tune));
        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent,
            TaskService.ChooseSubtaskFetchStrategy(parentCount: 100, listCount: 0, Tune));
    }

    // ── PlanSubtaskFetch ────────────────────────────────────────────────────

    [Fact]
    public void PerParentPlan_ListsInViewTaskIds_InFirstAppearanceOrder()
    {
        TaskItem[] snapshot = [Task("a"), Task("b"), Task("c")];
        var plan = TaskService.PlanSubtaskFetch(snapshot, Tune);

        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent, plan.Strategy);
        Assert.Equal(["a", "b", "c"], plan.Ids);
        Assert.False(plan.Capped);
    }

    [Fact]
    public void WholeListPlan_ListsDistinctNonBlankListIds_InFirstAppearanceOrder()
    {
        // 9 parents clustered in 2 lists (L1 ×5, L2 ×4) → whole-list; plan fetches the two lists.
        TaskItem[] snapshot =
        [
            Task("a", "L1"), Task("b", "L2"), Task("c", "L1"), Task("d", "L1"), Task("e", "L2"),
            Task("f", "L1"), Task("g", "L2"), Task("h", "L1"), Task("i", "L2"),
        ];
        var plan = TaskService.PlanSubtaskFetch(snapshot, Tune);

        Assert.Equal(TaskService.SubtaskFetchStrategy.WholeList, plan.Strategy);
        Assert.Equal(["L1", "L2"], plan.Ids); // distinct, first-appearance order
        Assert.False(plan.Capped);
    }

    [Fact]
    public void Plan_IgnoresBlankAndNullListIds()
    {
        TaskItem[] snapshot = [Task("a", list: null), Task("b", list: "  "), Task("c", list: "L")];
        // Only "L" is a real list, so this is 3 parents in 1 list → below the parent threshold anyway.
        var plan = TaskService.PlanSubtaskFetch(snapshot, Tune);

        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent, plan.Strategy);
        Assert.Equal(["a", "b", "c"], plan.Ids);
    }

    [Fact]
    public void Plan_DedupesRepeatedTaskIds()
    {
        TaskItem[] snapshot = [Task("a"), Task("a"), Task("b")];
        var plan = TaskService.PlanSubtaskFetch(snapshot, Tune);

        Assert.Equal(["a", "b"], plan.Ids);
    }

    [Fact]
    public void Plan_CapsPerParentRootsAndFlagsCapped()
    {
        var tiny = Tune with { MaxRoundTrips = 2 };
        TaskItem[] snapshot = [Task("a"), Task("b"), Task("c"), Task("d")];
        var plan = TaskService.PlanSubtaskFetch(snapshot, tiny);

        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent, plan.Strategy);
        Assert.Equal(["a", "b"], plan.Ids); // truncated to MaxRoundTrips, first-appearance order
        Assert.True(plan.Capped);
    }

    [Fact]
    public void Plan_CapsWholeListsAndFlagsCapped()
    {
        // Force whole-list (12 parents, 4 lists ×3) then cap the list fetches at 2.
        var tiny = Tune with { MaxRoundTrips = 2 };
        TaskItem[] snapshot =
        [
            Task("a", "L1"), Task("b", "L2"), Task("c", "L3"), Task("d", "L4"),
            Task("e", "L1"), Task("f", "L2"), Task("g", "L3"), Task("h", "L4"),
            Task("i", "L1"), Task("j", "L2"), Task("k", "L3"), Task("l", "L4"),
        ];
        var plan = TaskService.PlanSubtaskFetch(snapshot, tiny);

        Assert.Equal(TaskService.SubtaskFetchStrategy.WholeList, plan.Strategy);
        Assert.Equal(["L1", "L2"], plan.Ids);
        Assert.True(plan.Capped);
    }

    [Fact]
    public void Plan_ExactlyAtCap_IsNotFlaggedCapped()
    {
        var tune = Tune with { MaxRoundTrips = 3 };
        TaskItem[] snapshot = [Task("a"), Task("b"), Task("c")];
        var plan = TaskService.PlanSubtaskFetch(snapshot, tune);

        Assert.Equal(["a", "b", "c"], plan.Ids);
        Assert.False(plan.Capped);
    }

    [Fact]
    public void EmptySnapshot_PlansEmptyPerParent()
    {
        var plan = TaskService.PlanSubtaskFetch([], Tune);

        Assert.Equal(TaskService.SubtaskFetchStrategy.PerParent, plan.Strategy);
        Assert.Empty(plan.Ids);
        Assert.False(plan.Capped);
    }
}
