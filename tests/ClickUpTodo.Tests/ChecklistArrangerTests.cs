using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure unit tests for <see cref="ChecklistArranger"/> (#455): the domain
/// <see cref="TaskChecklist"/> list → flat, display-ordered <see cref="ChecklistRow"/> projection. No
/// Terminal.Gui. Covers the acceptance bullets — ordering across and within checklists, tie-break
/// stability, two/three levels of nesting via both <c>Children</c> and <c>ParentId</c>, an orphaned child
/// surfacing rather than vanishing, a parent/child cycle terminating, per-checklist and aggregate progress
/// counts, an item with no assignee, zero checklists, and a checklist with zero items.
/// </summary>
public sealed class ChecklistArrangerTests
{
    private static TaskChecklistItem Item(
        string id,
        string name = "item",
        bool resolved = false,
        double? orderIndex = null,
        string? parentId = null,
        TaskAssignee? assignee = null,
        IReadOnlyList<TaskChecklistItem>? children = null)
        => new(id, name, resolved, orderIndex, parentId, assignee, children);

    private static TaskChecklist List(
        string id,
        string name = "checklist",
        double? orderIndex = null,
        params TaskChecklistItem[] items)
        => new(id, name, orderIndex, 0, 0, items);

    private static IReadOnlyList<ChecklistRow> Items(ChecklistProjection p)
        => p.Rows.Where(r => r.Kind == ChecklistRowKind.Item).ToList();

    [Fact]
    public void ZeroChecklists_IsEmpty()
    {
        var p = ChecklistArranger.Project([]);

        Assert.True(p.IsEmpty);
        Assert.Empty(p.Rows);
        Assert.Equal(0, p.ChecklistCount);
        Assert.Equal((0, 0), (p.ResolvedCount, p.TotalCount));
    }

    [Fact]
    public void NullInput_IsEmpty()
        => Assert.True(ChecklistArranger.Project(null).IsEmpty);

    [Fact]
    public void ChecklistWithZeroItems_EmitsHeaderOnly()
    {
        var p = ChecklistArranger.Project([List("c1", "Empty")]);

        var row = Assert.Single(p.Rows);
        Assert.Equal(ChecklistRowKind.Header, row.Kind);
        Assert.Equal("Empty", row.Text);
        Assert.Equal("c1", row.ChecklistId);
        Assert.Null(row.ItemId);
        Assert.Equal((0, 0), (row.ResolvedCount, row.TotalCount));
        Assert.False(p.IsEmpty); // a checklist exists even though it has no items.
    }

    [Fact]
    public void HeaderCarriesChecklistProgress_ItemRowsDoNot()
    {
        var p = ChecklistArranger.Project([
            List("c1", "Release", null,
                Item("a", resolved: true, orderIndex: 0),
                Item("b", resolved: false, orderIndex: 1),
                Item("c", resolved: true, orderIndex: 2)),
        ]);

        var header = p.Rows[0];
        Assert.Equal(ChecklistRowKind.Header, header.Kind);
        Assert.Equal((2, 3), (header.ResolvedCount, header.TotalCount));

        foreach (var item in Items(p))
            Assert.Equal((0, 0), (item.ResolvedCount, item.TotalCount));
    }

    [Fact]
    public void AggregateProgress_SumsAcrossChecklists()
    {
        var p = ChecklistArranger.Project([
            List("c1", "One", 0, Item("a", resolved: true), Item("b", resolved: false)),
            List("c2", "Two", 1, Item("c", resolved: true), Item("d", resolved: true), Item("e", resolved: false)),
        ]);

        Assert.Equal(2, p.ChecklistCount);
        Assert.Equal((3, 5), (p.ResolvedCount, p.TotalCount));
    }

    [Fact]
    public void ChecklistsOrderedByOrderIndex()
    {
        var p = ChecklistArranger.Project([
            List("c3", "Third", 2, Item("x")),
            List("c1", "First", 0, Item("y")),
            List("c2", "Second", 1, Item("z")),
        ]);

        var headers = p.Rows.Where(r => r.IsHeader).Select(r => r.ChecklistId).ToList();
        Assert.Equal(["c1", "c2", "c3"], headers);
    }

    [Fact]
    public void ItemsOrderedByOrderIndexWithinChecklist()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("b", orderIndex: 1),
                Item("c", orderIndex: 2),
                Item("a", orderIndex: 0)),
        ]);

        Assert.Equal(["a", "b", "c"], Items(p).Select(r => r.ItemId));
    }

    [Fact]
    public void EqualOrderIndex_BreaksTieOnId_Stable()
    {
        // All the same order index → deterministic ordinal-id order regardless of input order.
        var p1 = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("gamma", orderIndex: 5),
                Item("alpha", orderIndex: 5),
                Item("beta", orderIndex: 5)),
        ]);
        var p2 = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("beta", orderIndex: 5),
                Item("gamma", orderIndex: 5),
                Item("alpha", orderIndex: 5)),
        ]);

        Assert.Equal(["alpha", "beta", "gamma"], Items(p1).Select(r => r.ItemId));
        Assert.Equal(["alpha", "beta", "gamma"], Items(p2).Select(r => r.ItemId));
    }

    [Fact]
    public void NullOrderIndex_SortsAfterPresentOnes()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("none", orderIndex: null),
                Item("first", orderIndex: 0),
                Item("second", orderIndex: 1)),
        ]);

        Assert.Equal(["first", "second", "none"], Items(p).Select(r => r.ItemId));
    }

    [Fact]
    public void TwoLevelNesting_ViaChildrenArray()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("parent", orderIndex: 0, children: [
                    Item("child1", orderIndex: 0),
                    Item("child2", orderIndex: 1),
                ])),
        ]);

        var items = Items(p);
        Assert.Equal(["parent", "child1", "child2"], items.Select(r => r.ItemId));
        Assert.Equal([0, 1, 1], items.Select(r => r.Depth));
    }

    [Fact]
    public void TwoLevelNesting_ViaParentPointer()
    {
        // Flat list, nesting expressed only by ParentId pointers.
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("parent", orderIndex: 0),
                Item("child", orderIndex: 0, parentId: "parent")),
        ]);

        var items = Items(p);
        Assert.Equal(["parent", "child"], items.Select(r => r.ItemId));
        Assert.Equal([0, 1], items.Select(r => r.Depth));
    }

    [Fact]
    public void ThreeLevelNesting_IndentsEachLevel()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("a", orderIndex: 0, children: [
                    Item("b", orderIndex: 0, children: [
                        Item("c", orderIndex: 0),
                    ]),
                ])),
        ]);

        var items = Items(p);
        Assert.Equal(["a", "b", "c"], items.Select(r => r.ItemId));
        Assert.Equal([0, 1, 2], items.Select(r => r.Depth));
    }

    [Fact]
    public void DualRepresentation_ChildInBothChildrenAndFlat_NotDoubled()
    {
        // ClickUp may send the child both inside the parent's Children array and again flat with a
        // ParentId pointer. It must appear exactly once, nested under its parent.
        var child = Item("child", orderIndex: 0, parentId: "parent");
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("parent", orderIndex: 0, children: [child]),
                child),
        ]);

        var items = Items(p);
        Assert.Equal(["parent", "child"], items.Select(r => r.ItemId));
        Assert.Equal([0, 1], items.Select(r => r.Depth));
        Assert.Equal(2, p.TotalCount); // parent + child; the child is not doubled (would be 3).
    }

    [Fact]
    public void OrphanedChild_SurfacesAtTopLevel_NotDropped()
    {
        // ParentId points at an id that isn't in the checklist → surface at top level rather than vanish.
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("keep", orderIndex: 0),
                Item("orphan", orderIndex: 1, parentId: "ghost")),
        ]);

        var items = Items(p);
        Assert.Equal(["keep", "orphan"], items.Select(r => r.ItemId));
        Assert.Equal([0, 0], items.Select(r => r.Depth)); // orphan is a root, depth 0.
        Assert.Equal(2, p.TotalCount);
    }

    [Fact]
    public void ParentChildCycle_TerminatesWithEveryItemPresent()
    {
        // A ↔ B mutual parents (ClickUp doesn't produce this, but the arranger must terminate and keep both).
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("A", orderIndex: 0, parentId: "B"),
                Item("B", orderIndex: 1, parentId: "A")),
        ]);

        var ids = Items(p).Select(r => r.ItemId).OrderBy(x => x).ToList();
        Assert.Equal(["A", "B"], ids); // both present, exactly once, and the call returned (no hang).
        Assert.Equal(2, p.TotalCount);
    }

    [Fact]
    public void SelfParentCycle_Terminates()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0, Item("self", orderIndex: 0, parentId: "self")),
        ]);

        var item = Assert.Single(Items(p));
        Assert.Equal("self", item.ItemId);
    }

    [Fact]
    public void ItemWithNoAssignee_HasNullAssigneeText()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0, Item("a", assignee: null)),
        ]);

        Assert.Null(Assert.Single(Items(p)).Assignee);
    }

    [Fact]
    public void ItemAssignee_RendersDisplayName()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0, Item("a", assignee: new TaskAssignee(42, "Ada"))),
        ]);

        Assert.Equal("Ada", Assert.Single(Items(p)).Assignee);
    }

    [Fact]
    public void BareIdAssignee_HasNullText_UntilResolvedInG()
    {
        // The read model leaves a bare-id assignee's name empty for a later slice to resolve.
        var p = ChecklistArranger.Project([
            List("c1", "L", 0, Item("a", assignee: new TaskAssignee(42, ""))),
        ]);

        Assert.Null(Assert.Single(Items(p)).Assignee);
    }

    [Fact]
    public void ResolvedFlagCarriedPerItem()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("done", resolved: true, orderIndex: 0),
                Item("todo", resolved: false, orderIndex: 1)),
        ]);

        var items = Items(p);
        Assert.True(items[0].Resolved);
        Assert.False(items[1].Resolved);
    }

    [Fact]
    public void NestedResolvedItems_CountTowardProgress()
    {
        var p = ChecklistArranger.Project([
            List("c1", "L", 0,
                Item("parent", resolved: false, orderIndex: 0, children: [
                    Item("child1", resolved: true, orderIndex: 0),
                    Item("child2", resolved: true, orderIndex: 1),
                ])),
        ]);

        Assert.Equal((2, 3), (p.Rows[0].ResolvedCount, p.Rows[0].TotalCount));
        Assert.Equal((2, 3), (p.ResolvedCount, p.TotalCount));
    }
}
