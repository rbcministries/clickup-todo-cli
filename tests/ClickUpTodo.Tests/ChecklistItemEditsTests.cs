using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure unit tests for <see cref="ChecklistItemEdits"/> (E, #458): the immutable
/// <see cref="TaskChecklist"/>-tree transforms behind the Checklists tab's item add / rename / delete
/// (plus the <see cref="ChecklistItemEdits.NormalizeName"/> and <see cref="ChecklistItemEdits.NewItemId"/>
/// helpers). Like <see cref="ChecklistToggleTests"/>, the tree transforms are asserted through the
/// <see cref="ChecklistArranger"/> projection — the rows the screen actually renders — so "the row appears
/// / disappears / renames and the counts follow" is verified end-to-end without a terminal.
/// </summary>
public sealed class ChecklistItemEditsTests
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

    private static bool HasItem(IReadOnlyList<TaskChecklist> checklists, string itemId)
        => Rows(checklists).Any(r => r.Kind == ChecklistRowKind.Item && r.ItemId == itemId);

    private static (int Resolved, int Total) Aggregate(IReadOnlyList<TaskChecklist> checklists)
    {
        var p = ChecklistArranger.Project(checklists);
        return (p.ResolvedCount, p.TotalCount);
    }

    // ── SetName (rename) ──────────────────────────────────────────────────────

    [Fact]
    public void SetName_RenamesTopLevelItem()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "Old name"), Item("i2", "Keep"))];

        var after = ChecklistItemEdits.SetName(before, "c1", "i1", "New name");

        Assert.Equal("New name", ItemRow(after, "i1").Text);
        Assert.Equal("Keep", ItemRow(after, "i2").Text); // sibling untouched
    }

    [Fact]
    public void SetName_RenamesNestedChild()
    {
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, Item("i1", "Parent", children: [Item("i2", "Old")]))];

        var after = ChecklistItemEdits.SetName(before, "c1", "i2", "Renamed");

        Assert.Equal("Renamed", ItemRow(after, "i2").Text);
        Assert.Equal("Parent", ItemRow(after, "i1").Text);
    }

    [Fact]
    public void SetName_UpdatesItemPresentBothFlatAndAsChild()
    {
        // One item expressed in both a parent's Children array and a flat ParentId pointer — the arranger
        // collects it once, so both occurrences must rename consistently regardless of which it reads.
        IReadOnlyList<TaskChecklist> before =
        [
            List("c1", "Release", 0,
                Item("i1", "Parent", children: [Item("i2", "Old")]),
                Item("i2", "Old", parentId: "i1")),
        ];

        var after = ChecklistItemEdits.SetName(before, "c1", "i2", "New");

        Assert.Equal("New", ItemRow(after, "i2").Text);
    }

    [Fact]
    public void SetName_UnknownItem_IsProjectionNoOp()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "A"))];
        Assert.Equal(Rows(before), Rows(ChecklistItemEdits.SetName(before, "c1", "nope", "X")));
        Assert.Equal(Rows(before), Rows(ChecklistItemEdits.SetName(before, "no-list", "i1", "X")));
    }

    // ── Remove (delete) ───────────────────────────────────────────────────────

    [Fact]
    public void Remove_DropsALeaf_AndUpdatesCounts()
    {
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, Item("i1", "A", resolved: true), Item("i2", "B", resolved: false))];
        Assert.Equal((1, 2), Aggregate(before));

        var after = ChecklistItemEdits.Remove(before, "c1", "i2");

        Assert.False(HasItem(after, "i2"));
        Assert.True(HasItem(after, "i1"));
        Assert.Equal((1, 1), Aggregate(after)); // total dropped by one; the survivor is still resolved
    }

    [Fact]
    public void Remove_DropsWholeSubtree_WhenParentDeleted_ChildrenForm()
    {
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, Item("i1", "Parent", children: [Item("i2", "Child"), Item("i3", "Child2")]))];
        Assert.Equal((0, 3), Aggregate(before));

        var after = ChecklistItemEdits.Remove(before, "c1", "i1");

        Assert.False(HasItem(after, "i1"));
        Assert.False(HasItem(after, "i2")); // nested children go with the parent
        Assert.False(HasItem(after, "i3"));
        Assert.Equal((0, 0), Aggregate(after));
    }

    [Fact]
    public void Remove_DropsWholeSubtree_WhenParentDeleted_FlatParentIdForm()
    {
        // Nesting expressed only via a flat ParentId pointer (no Children array). Deleting the parent must
        // still cascade so the child isn't left orphaned and resurfaced at top level by the arranger.
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, Item("i1", "Parent"), Item("i2", "Child", parentId: "i1"))];
        Assert.Equal(2, Rows(before).Count(r => r.Kind == ChecklistRowKind.Item));

        var after = ChecklistItemEdits.Remove(before, "c1", "i1");

        Assert.False(HasItem(after, "i1"));
        Assert.False(HasItem(after, "i2"));
        Assert.Equal((0, 0), Aggregate(after));
    }

    [Fact]
    public void Remove_DeletingChild_LeavesParentAndSiblings()
    {
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, Item("i1", "Parent", children: [Item("i2", "Child"), Item("i3", "Sib")]))];

        var after = ChecklistItemEdits.Remove(before, "c1", "i2");

        Assert.True(HasItem(after, "i1"));
        Assert.False(HasItem(after, "i2"));
        Assert.True(HasItem(after, "i3")); // sibling undisturbed
        Assert.Equal((0, 2), Aggregate(after));
    }

    [Fact]
    public void Remove_OnlyTargetChecklistIsAffected()
    {
        IReadOnlyList<TaskChecklist> before =
        [
            List("c1", "Release", 0, Item("i1", "A")),
            List("c2", "QA", 1, Item("i1", "A")), // same item id, different checklist
        ];

        var after = ChecklistItemEdits.Remove(before, "c1", "i1");

        Assert.DoesNotContain(Rows(after), r => r.ChecklistId == "c1" && r.ItemId == "i1");
        Assert.Contains(Rows(after), r => r.ChecklistId == "c2" && r.ItemId == "i1");
    }

    [Fact]
    public void Remove_UnknownItemOrChecklist_IsProjectionNoOp()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "A"))];
        Assert.Equal(Rows(before), Rows(ChecklistItemEdits.Remove(before, "c1", "nope")));
        Assert.Equal(Rows(before), Rows(ChecklistItemEdits.Remove(before, "no-list", "i1")));
    }

    // ── InsertProvisional (add) ───────────────────────────────────────────────

    [Fact]
    public void InsertProvisional_AppendsTopLevelItem_AndReprojects()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "A", resolved: true))];
        Assert.Equal((1, 1), Aggregate(before));

        var provisional = Item(ChecklistItemEdits.ProvisionalItemId, "Typing…", orderIndex: 99);
        var after = ChecklistItemEdits.InsertProvisional(before, "c1", provisional);

        Assert.True(HasItem(after, ChecklistItemEdits.ProvisionalItemId));
        Assert.Equal("Typing…", ItemRow(after, ChecklistItemEdits.ProvisionalItemId).Text);
        Assert.Equal(0, ItemRow(after, ChecklistItemEdits.ProvisionalItemId).Depth); // top level
        Assert.Equal((1, 2), Aggregate(after)); // total grew by one, the new item unresolved
    }

    [Fact]
    public void InsertProvisional_UnknownChecklist_IsProjectionNoOp()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "A"))];
        var after = ChecklistItemEdits.InsertProvisional(before, "no-list", Item("x", "X"));
        Assert.Equal(Rows(before), Rows(after));
    }

    // ── NormalizeName ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("  Draft notes  ", "Draft notes")]
    [InlineData("Ship it", "Ship it")]
    public void NormalizeName_TrimsAndKeepsContent(string raw, string expected)
        => Assert.Equal(expected, ChecklistItemEdits.NormalizeName(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    [InlineData(null)]
    public void NormalizeName_RejectsEmptyOrWhitespace(string? raw)
        => Assert.Null(ChecklistItemEdits.NormalizeName(raw));

    // ── NewItemId ─────────────────────────────────────────────────────────────

    [Fact]
    public void NewItemId_FindsTheSingleAddedId()
    {
        var before = List("c1", "Release", 0, Item("i1", "A"));
        var after = List("c1", "Release", 0, Item("i1", "A"), Item("i2", "B"));
        Assert.Equal("i2", ChecklistItemEdits.NewItemId(before, after));
    }

    [Fact]
    public void NewItemId_FindsNewNestedId()
    {
        var before = List("c1", "Release", 0, Item("i1", "A"));
        var after = List("c1", "Release", 0, Item("i1", "A", children: [Item("i1a", "child")]));
        Assert.Equal("i1a", ChecklistItemEdits.NewItemId(before, after));
    }

    [Fact]
    public void NewItemId_NullWhenNothingAdded()
    {
        var before = List("c1", "Release", 0, Item("i1", "A"));
        var after = List("c1", "Release", 0, Item("i1", "A"));
        Assert.Null(ChecklistItemEdits.NewItemId(before, after));
    }

    [Fact]
    public void NewItemId_NullWhenAmbiguousMultipleAdditions()
    {
        var before = List("c1", "Release", 0, Item("i1", "A"));
        var after = List("c1", "Release", 0, Item("i1", "A"), Item("i2", "B"), Item("i3", "C"));
        Assert.Null(ChecklistItemEdits.NewItemId(before, after));
    }

    [Fact]
    public void NewItemId_NullAfter_ReturnsNull()
        => Assert.Null(ChecklistItemEdits.NewItemId(List("c1", "Release", 0, Item("i1", "A")), null));

    [Fact]
    public void NewItemId_NullBefore_TreatsEveryIdAsNew()
    {
        // A create into an empty checklist: before has no items, after has exactly one → that one is new.
        var after = List("c1", "Release", 0, Item("i9", "Only"));
        Assert.Equal("i9", ChecklistItemEdits.NewItemId(null, after));
    }
}
