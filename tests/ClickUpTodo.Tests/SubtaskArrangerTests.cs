using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the nested subtasks arrangement (#46): parents keep their order, subtasks nest
/// beneath them, not-in-snapshot parents inject as context headers, and unknown parents fall back flat.
/// </summary>
public sealed class SubtaskArrangerTests
{
    private static TaskItem Task(string id, string? parent = null)
        => new() { Id = id, Name = id, ParentId = parent };

    private static IReadOnlyList<ArrangedRow> Arrange(
        IReadOnlyList<TaskItem> tasks, IReadOnlyDictionary<string, TaskItem>? context = null)
        => SubtaskArranger.Arrange(tasks, context ?? new Dictionary<string, TaskItem>());

    private static readonly Dictionary<string, TaskItem> NoContext = new();

    private static IReadOnlySet<string> Expanded(params string[] ids)
        => new HashSet<string>(ids, StringComparer.Ordinal);

    [Fact]
    public void Arrange_NestsChildImmediatelyUnderParent_WithDepth()
    {
        // Input order interleaves the child away from its parent (as due-date sorting would).
        TaskItem[] tasks = [Task("p"), Task("other"), Task("c", parent: "p")];

        var rows = Arrange(tasks);

        Assert.Equal(["p", "c", "other"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 0], rows.Select(r => r.Depth));
        Assert.All(rows, r => Assert.False(r.IsContextParent));
    }

    [Fact]
    public void Arrange_ParentAfterChildInInput_StillEmitsParentFirst()
    {
        TaskItem[] tasks = [Task("c", parent: "p"), Task("p")];

        var rows = Arrange(tasks);

        Assert.Equal(["p", "c"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1], rows.Select(r => r.Depth));
    }

    [Fact]
    public void Arrange_MultipleSiblings_KeepInputOrderUnderParent()
    {
        TaskItem[] tasks = [Task("p"), Task("c1", parent: "p"), Task("c2", parent: "p")];

        var rows = Arrange(tasks);

        Assert.Equal(["p", "c1", "c2"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 1], rows.Select(r => r.Depth));
    }

    [Fact]
    public void Arrange_DeepNesting_IndentsByDepth()
    {
        TaskItem[] tasks = [Task("a"), Task("b", parent: "a"), Task("c", parent: "b")];

        var rows = Arrange(tasks);

        Assert.Equal(["a", "b", "c"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Depth));
    }

    [Fact]
    public void Arrange_UnknownParent_FallsBackToTopLevelFlat()
    {
        TaskItem[] tasks = [Task("orphan", parent: "missing")];

        var rows = Arrange(tasks);

        var only = Assert.Single(rows);
        Assert.Equal("orphan", only.Task.Id);
        Assert.Equal(0, only.Depth);
        Assert.False(only.IsContextParent);
    }

    [Fact]
    public void Arrange_ContextParent_InjectedOnceAsHeaderWithChildrenBeneath()
    {
        TaskItem[] tasks = [Task("c1", parent: "P"), Task("c2", parent: "P")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["P", "c1", "c2"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 1], rows.Select(r => r.Depth));
        Assert.True(rows[0].IsContextParent);
        Assert.False(rows[1].IsContextParent);
        Assert.False(rows[2].IsContextParent);
    }

    [Fact]
    public void Arrange_ContextParent_HeaderAppearsAtFirstChildPosition()
    {
        TaskItem[] tasks = [Task("top"), Task("c", parent: "P")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["top", "P", "c"], rows.Select(r => r.Task.Id));
    }

    [Fact]
    public void Arrange_ContextParent_ScatteredChildren_CollapseUnderHeaderAtFirstChild()
    {
        // The two children of context parent P are separated by an unrelated top-level task.
        TaskItem[] tasks = [Task("c1", parent: "P"), Task("other"), Task("c2", parent: "P")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Arrange(tasks, context);

        // Both children collapse under the injected header at the first child's position.
        Assert.Equal(["P", "c1", "c2", "other"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 1, 0], rows.Select(r => r.Depth));
        Assert.True(rows[0].IsContextParent);
    }

    [Fact]
    public void Arrange_ContextParent_WithGrandchild_IndentsGrandchildDeeper()
    {
        // c is a child of context parent P; g is a child of c → g nests two levels under the header.
        TaskItem[] tasks = [Task("c", parent: "P"), Task("g", parent: "c")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["P", "c", "g"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Depth));
        Assert.True(rows[0].IsContextParent);
    }

    [Fact]
    public void Arrange_ParentPresentInSnapshot_DoesNotUseContextHeader()
    {
        TaskItem[] tasks = [Task("p"), Task("c", parent: "p")];
        // Even if a context entry exists, the in-snapshot parent wins (no duplicate/context row).
        var context = new Dictionary<string, TaskItem> { ["p"] = Task("p") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["p", "c"], rows.Select(r => r.Task.Id));
        Assert.All(rows, r => Assert.False(r.IsContextParent));
    }

    [Fact]
    public void Arrange_NoSubtasks_ReturnsInputOrderUnchanged()
    {
        TaskItem[] tasks = [Task("a"), Task("b"), Task("c")];

        var rows = Arrange(tasks);

        Assert.Equal(["a", "b", "c"], rows.Select(r => r.Task.Id));
        Assert.All(rows, r => Assert.Equal(0, r.Depth));
    }

    [Fact]
    public void Arrange_ParentCycle_TerminatesAndEmitsEachOnce()
    {
        // Pathological: a↔b reference each other. Must not loop forever; each emitted once.
        TaskItem[] tasks = [Task("a", parent: "b"), Task("b", parent: "a")];

        var rows = Arrange(tasks);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows.Select(r => r.Task.Id).OrderBy(x => x));
    }

    [Fact]
    public void Arrange_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Arrange([]));
    }

    [Fact]
    public void Arrange_PresentChildThatIsAlsoAContextParent_NestsChainWithoutContextHeader()
    {
        // #70 edge case: X is a foreign child folded into the set (so it's present) *and* it's listed as
        // a context parent (it parents the in-snapshot subtask Y). Because X is present, X nests under P
        // and Y under X — X is never injected as a duplicate context header.
        TaskItem[] tasks = [Task("P"), Task("X", parent: "P"), Task("Y", parent: "X")];
        var context = new Dictionary<string, TaskItem> { ["X"] = Task("X") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["P", "X", "Y"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Depth));
        Assert.All(rows, r => Assert.False(r.IsContextParent));
    }

    // ── Per-parent fold state (#76) ──────────────────────────────────────────

    [Fact]
    public void Arrange_NullExpanded_KeepsLegacyAllExpanded_AndMarksParentsExpanded()
    {
        TaskItem[] tasks = [Task("p"), Task("c", parent: "p"), Task("leaf")];

        var rows = Arrange(tasks); // expanded == null ⇒ everything expanded (pre-#76)

        Assert.Equal(["p", "c", "leaf"], rows.Select(r => r.Task.Id));
        Assert.Equal(FoldState.Expanded, rows[0].Fold); // parent with children
        Assert.Equal(FoldState.None, rows[1].Fold);     // leaf child
        Assert.Equal(FoldState.None, rows[2].Fold);     // top-level leaf
    }

    [Fact]
    public void Arrange_CollapsedParent_HidesChildren_AndShowsCollapsedMarker()
    {
        TaskItem[] tasks = [Task("p"), Task("c", parent: "p"), Task("other")];

        var rows = SubtaskArranger.Arrange(tasks, NoContext, Expanded(/* none */));

        // 'p' is collapsed: its child 'c' is hidden and must NOT leak out as a flat top-level row.
        Assert.Equal(["p", "other"], rows.Select(r => r.Task.Id));
        Assert.Equal(FoldState.Collapsed, rows[0].Fold);
        Assert.Equal(FoldState.None, rows[1].Fold);
    }

    [Fact]
    public void Arrange_ExpandedParent_NestsChildren()
    {
        TaskItem[] tasks = [Task("p"), Task("c", parent: "p"), Task("other")];

        var rows = SubtaskArranger.Arrange(tasks, NoContext, Expanded("p"));

        Assert.Equal(["p", "c", "other"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 0], rows.Select(r => r.Depth));
        Assert.Equal(FoldState.Expanded, rows[0].Fold);
    }

    [Fact]
    public void Arrange_CollapsedParent_SuppressesDeepSubtree_NoLeak()
    {
        // p → c → g. Collapsing p must hide the whole subtree, not just the direct child.
        TaskItem[] tasks = [Task("p"), Task("c", parent: "p"), Task("g", parent: "c")];

        var rows = SubtaskArranger.Arrange(tasks, NoContext, Expanded(/* none */));

        var only = Assert.Single(rows);
        Assert.Equal("p", only.Task.Id);
        Assert.Equal(FoldState.Collapsed, only.Fold);
    }

    [Fact]
    public void Arrange_MixedFold_OneExpandedOneCollapsed()
    {
        TaskItem[] tasks =
        [
            Task("p1"), Task("c1", parent: "p1"),
            Task("p2"), Task("c2", parent: "p2"),
        ];

        var rows = SubtaskArranger.Arrange(tasks, NoContext, Expanded("p1"));

        Assert.Equal(["p1", "c1", "p2"], rows.Select(r => r.Task.Id));
        Assert.Equal(FoldState.Expanded, rows[0].Fold);
        Assert.Equal(FoldState.Collapsed, rows[2].Fold);
    }

    [Fact]
    public void Arrange_ContextParent_AlwaysExpanded_RegardlessOfSet_AndNoFoldMarker()
    {
        // A context parent isn't in the set, yet its assigned child must still show (it exists only to
        // display that child) and the context parent itself carries no fold marker.
        TaskItem[] tasks = [Task("c", parent: "P")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = SubtaskArranger.Arrange(tasks, context, Expanded(/* none */));

        Assert.Equal(["P", "c"], rows.Select(r => r.Task.Id));
        Assert.True(rows[0].IsContextParent);
        Assert.Equal(FoldState.None, rows[0].Fold); // never user-foldable
    }

    [Fact]
    public void Arrange_CollapsedParent_UnderExpandedContextParent_IsHidden()
    {
        // Context parent P (always shown) → child c (a collapsed parent) → grandchild g (hidden).
        TaskItem[] tasks = [Task("c", parent: "P"), Task("g", parent: "c")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = SubtaskArranger.Arrange(tasks, context, Expanded(/* c not expanded */));

        Assert.Equal(["P", "c"], rows.Select(r => r.Task.Id));
        Assert.Equal(FoldState.Collapsed, rows[1].Fold); // c is a collapsed parent
    }

    // ── Foldable-parent ids for expand-all / collapse-all (#83) ──────────────

    [Fact]
    public void FoldableParentIds_FlatList_IsEmpty()
    {
        TaskItem[] tasks = [Task("a"), Task("b"), Task("c")];

        Assert.Empty(SubtaskArranger.FoldableParentIds(tasks));
    }

    [Fact]
    public void FoldableParentIds_ParentWithChild_IsJustTheParent()
    {
        TaskItem[] tasks = [Task("p"), Task("c", parent: "p"), Task("other")];

        Assert.Equal(Expanded("p"), SubtaskArranger.FoldableParentIds(tasks));
    }

    [Fact]
    public void FoldableParentIds_DeepChain_IncludesEveryIntermediateParent_AtAllDepths()
    {
        // a → b → c → d: a, b, c are foldable (each has a present child); d (leaf) is not. The point of
        // #83 is that this reaches c even though c's subtree is hidden when a/b are collapsed.
        TaskItem[] tasks = [Task("a"), Task("b", parent: "a"), Task("c", parent: "b"), Task("d", parent: "c")];

        Assert.Equal(Expanded("a", "b", "c"), SubtaskArranger.FoldableParentIds(tasks));
    }

    [Fact]
    public void FoldableParentIds_ParentWhoseOnlyChildIsAbsent_IsExcluded()
    {
        // 'p' is present but its child was filtered out of the view → p isn't foldable in this set.
        TaskItem[] tasks = [Task("p"), Task("other")];

        Assert.Empty(SubtaskArranger.FoldableParentIds(tasks));
    }

    [Fact]
    public void FoldableParentIds_OrphanPointingAtMissingParent_IsExcluded()
    {
        // The referenced parent isn't present, so it can't be a foldable row (it'd be a context parent,
        // which is never user-foldable). The orphan itself has no children → not foldable either.
        TaskItem[] tasks = [Task("orphan", parent: "missing")];

        Assert.Empty(SubtaskArranger.FoldableParentIds(tasks));
    }

    [Fact]
    public void FoldableParentIds_MultipleParents_ReturnsEachOnce()
    {
        TaskItem[] tasks =
        [
            Task("p1"), Task("c1", parent: "p1"),
            Task("p2"), Task("c2a", parent: "p2"), Task("c2b", parent: "p2"),
        ];

        Assert.Equal(Expanded("p1", "p2"), SubtaskArranger.FoldableParentIds(tasks));
    }

    [Fact]
    public void FoldableParentIds_ParentCycle_TerminatesAndReturnsBoth()
    {
        // Pathological a↔b: both are "present with a present child", so both are foldable. Must not loop.
        TaskItem[] tasks = [Task("a", parent: "b"), Task("b", parent: "a")];

        Assert.Equal(Expanded("a", "b"), SubtaskArranger.FoldableParentIds(tasks));
    }

    [Fact]
    public void FoldableParentIds_MatchesTheParentsArrangeMarksExpanded()
    {
        // Cross-check the helper against the arranger's own notion of "foldable": expanding exactly the
        // helper's set must mark every one of those rows Expanded and leave no other row folded.
        TaskItem[] tasks =
        [
            Task("p1"), Task("c1", parent: "p1"), Task("g1", parent: "c1"),
            Task("p2"), Task("c2", parent: "p2"),
            Task("leaf"),
        ];

        var foldable = SubtaskArranger.FoldableParentIds(tasks);
        var rows = SubtaskArranger.Arrange(tasks, NoContext, foldable);

        var markedFoldable = rows
            .Where(r => r.Fold is FoldState.Expanded or FoldState.Collapsed)
            .Select(r => r.Task.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(foldable, markedFoldable);
        // With every foldable parent expanded, none is left Collapsed.
        Assert.DoesNotContain(rows, r => r.Fold == FoldState.Collapsed);
        Assert.Equal(Expanded("p1", "c1", "p2"), foldable);
    }
}
