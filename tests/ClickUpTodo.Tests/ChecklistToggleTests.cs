using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure unit tests for <see cref="ChecklistToggle"/> (D, #457): the immutable
/// <see cref="TaskChecklist"/>-tree transform that flips one item's <see cref="TaskChecklistItem.Resolved"/>
/// flag, backing the Checklists tab's optimistic <c>Space</c>-toggle and its revert. Asserts on the
/// <see cref="ChecklistArranger"/> projection (whose <see cref="ChecklistRow"/>s have value equality), which
/// is what the screen actually renders and how "counts update immediately" and "revert restores the exact
/// prior row state" are verified. No Terminal.Gui.
/// </summary>
public sealed class ChecklistToggleTests
{
    private static TaskChecklistItem Item(
        string id,
        string name = "item",
        bool resolved = false,
        double? orderIndex = null,
        string? parentId = null,
        IReadOnlyList<TaskChecklistItem>? children = null)
        => new(id, name, resolved, orderIndex, parentId, null, children);

    private static TaskChecklist List(string id, string name, double? orderIndex, params TaskChecklistItem[] items)
        => new(id, name, orderIndex, 0, 0, items);

    private static IReadOnlyList<ChecklistRow> Rows(IReadOnlyList<TaskChecklist> checklists)
        => ChecklistArranger.Project(checklists).Rows;

    private static ChecklistRow ItemRow(IReadOnlyList<TaskChecklist> checklists, string itemId)
        => Rows(checklists).Single(r => r.Kind == ChecklistRowKind.Item && r.ItemId == itemId);

    private static ChecklistRow HeaderRow(IReadOnlyList<TaskChecklist> checklists, string checklistId)
        => Rows(checklists).Single(r => r.Kind == ChecklistRowKind.Header && r.ChecklistId == checklistId);

    private static (int Resolved, int Total) Aggregate(IReadOnlyList<TaskChecklist> checklists)
    {
        var p = ChecklistArranger.Project(checklists);
        return (p.ResolvedCount, p.TotalCount);
    }

    private static IReadOnlyList<TaskChecklist> TwoItemChecklist() =>
        [List("c1", "Release", 0, Item("i1", "Cut the tag", resolved: true), Item("i2", "Draft notes"))];

    [Fact]
    public void SetResolved_FlipsTopLevelItem_AndUpdatesProjectedCounts()
    {
        var before = TwoItemChecklist();
        Assert.False(ItemRow(before, "i2").Resolved);
        Assert.Equal((1, 2), (HeaderRow(before, "c1").ResolvedCount, HeaderRow(before, "c1").TotalCount));
        Assert.Equal((1, 2), Aggregate(before));

        var after = ChecklistToggle.SetResolved(before, "c1", "i2", resolved: true);

        Assert.True(ItemRow(after, "i2").Resolved);
        Assert.True(ItemRow(after, "i1").Resolved); // the other item is untouched
        Assert.Equal((2, 2), (HeaderRow(after, "c1").ResolvedCount, HeaderRow(after, "c1").TotalCount));
        Assert.Equal((2, 2), Aggregate(after)); // aggregate moved by exactly one
    }

    [Fact]
    public void SetResolved_CanUnresolve()
    {
        var before = TwoItemChecklist();
        var after = ChecklistToggle.SetResolved(before, "c1", "i1", resolved: false);

        Assert.False(ItemRow(after, "i1").Resolved);
        Assert.Equal((0, 2), Aggregate(after));
    }

    [Fact]
    public void SetResolved_FlipsNestedChild_ViaChildren()
    {
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, Item("i1", "Parent", children: [Item("i2", "Child")]))];
        Assert.False(ItemRow(before, "i2").Resolved);

        var after = ChecklistToggle.SetResolved(before, "c1", "i2", resolved: true);

        Assert.True(ItemRow(after, "i2").Resolved);
        Assert.False(ItemRow(after, "i1").Resolved); // parent untouched
        Assert.Equal((1, 2), Aggregate(after));
    }

    [Fact]
    public void SetResolved_UpdatesItemPresentBothFlatAndAsChild()
    {
        // ClickUp can express one item in both a parent's Children array and a flat ParentId pointer; the
        // arranger collects it once, so the transform must set Resolved on both occurrences to stay
        // consistent regardless of which instance the arranger reads.
        IReadOnlyList<TaskChecklist> before =
        [
            List("c1", "Release", 0,
                Item("i1", "Parent", children: [Item("i2", "Child")]),
                Item("i2", "Child", parentId: "i1")),
        ];
        Assert.False(ItemRow(before, "i2").Resolved);

        var after = ChecklistToggle.SetResolved(before, "c1", "i2", resolved: true);

        Assert.True(ItemRow(after, "i2").Resolved);
    }

    [Fact]
    public void SetResolved_UnknownItem_IsProjectionNoOp()
    {
        var before = TwoItemChecklist();
        var after = ChecklistToggle.SetResolved(before, "c1", "does-not-exist", resolved: true);

        Assert.Equal(Rows(before), Rows(after));
    }

    [Fact]
    public void SetResolved_UnknownChecklist_IsProjectionNoOp()
    {
        var before = TwoItemChecklist();
        var after = ChecklistToggle.SetResolved(before, "nope", "i2", resolved: true);

        Assert.Equal(Rows(before), Rows(after));
    }

    [Fact]
    public void SetResolved_ToggleThenBack_RestoresExactPriorRows()
    {
        var before = TwoItemChecklist();
        var baseline = Rows(before);

        var toggled = ChecklistToggle.SetResolved(before, "c1", "i2", resolved: true);
        Assert.NotEqual(baseline, Rows(toggled)); // sanity: the toggle actually moved a row

        var reverted = ChecklistToggle.SetResolved(toggled, "c1", "i2", resolved: false);

        Assert.Equal(baseline, Rows(reverted)); // exact prior row state restored
    }

    [Fact]
    public void SetResolved_OnlyTargetChecklistIsAffected()
    {
        IReadOnlyList<TaskChecklist> before =
        [
            List("c1", "Release", 0, Item("i1", "A")),
            List("c2", "QA", 1, Item("i1", "A")), // same item id in a different checklist
        ];

        var after = ChecklistToggle.SetResolved(before, "c1", "i1", resolved: true);

        Assert.True(Rows(after).Single(r => r.ChecklistId == "c1" && r.ItemId == "i1").Resolved);
        Assert.False(Rows(after).Single(r => r.ChecklistId == "c2" && r.ItemId == "i1").Resolved);
    }

    [Fact]
    public void SetResolved_EmptyOrNull_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(ChecklistToggle.SetResolved([], "c1", "i1", resolved: true));
        Assert.Empty(ChecklistToggle.SetResolved(null!, "c1", "i1", resolved: true));
    }
}
