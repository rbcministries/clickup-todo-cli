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

    // ── SetAssignee (per-item assignee, G #460) ───────────────────────────────

    private static readonly TaskAssignee Ada = new(7, "Ada Lovelace");

    /// <summary>Finds an item by id anywhere in the domain tree (flat list + nested children) — the
    /// SetAssignee transform touches the domain record, not the projected row text, so assertions read the
    /// <see cref="TaskChecklistItem.Assignee"/> straight off the tree.</summary>
    private static TaskChecklistItem FindItem(IReadOnlyList<TaskChecklist> checklists, string itemId)
    {
        TaskChecklistItem? Walk(IReadOnlyList<TaskChecklistItem> items)
        {
            foreach (var item in items)
            {
                if (item.Id == itemId)
                    return item;
                if (item.Children.Count > 0 && Walk(item.Children) is { } hit)
                    return hit;
            }
            return null;
        }
        return checklists.Select(c => Walk(c.Items)).FirstOrDefault(x => x is not null)
               ?? throw new InvalidOperationException($"item '{itemId}' not found");
    }

    [Fact]
    public void SetAssignee_SetsTopLevelItem_LeavesSiblingUntouched()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "A"), Item("i2", "B"))];

        var after = ChecklistItemEdits.SetAssignee(before, "c1", "i1", Ada);

        Assert.Equal(Ada, FindItem(after, "i1").Assignee);
        Assert.Null(FindItem(after, "i2").Assignee); // sibling untouched
    }

    [Fact]
    public void SetAssignee_ClearsAssignee_WhenNull()
    {
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, new TaskChecklistItem("i1", "A", false, null, null, Ada))];
        Assert.Equal(Ada, FindItem(before, "i1").Assignee); // precondition

        var after = ChecklistItemEdits.SetAssignee(before, "c1", "i1", null);

        Assert.Null(FindItem(after, "i1").Assignee);
    }

    [Fact]
    public void SetAssignee_UpdatesNestedChild()
    {
        IReadOnlyList<TaskChecklist> before =
            [List("c1", "Release", 0, Item("i1", "Parent", children: [Item("i2", "Child")]))];

        var after = ChecklistItemEdits.SetAssignee(before, "c1", "i2", Ada);

        Assert.Equal(Ada, FindItem(after, "i2").Assignee);
        Assert.Null(FindItem(after, "i1").Assignee); // parent untouched
    }

    [Fact]
    public void SetAssignee_UnknownItemOrChecklist_IsNoOp()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "A"))];

        // Unknown item id / unknown checklist id: the target item keeps its (null) assignee and the rows
        // are value-identical — a stray call (e.g. against a header row) can never corrupt the tree.
        Assert.Null(FindItem(ChecklistItemEdits.SetAssignee(before, "c1", "nope", Ada), "i1").Assignee);
        Assert.Null(FindItem(ChecklistItemEdits.SetAssignee(before, "no-list", "i1", Ada), "i1").Assignee);
        Assert.Equal(Rows(before), Rows(ChecklistItemEdits.SetAssignee(before, "no-list", "i1", Ada)));
    }

    // ── FindItem (rename-overlay assignee seed / server-confirm reduce, #572) ─────────────────────────

    [Fact]
    public void FindItem_ReturnsTopLevelItem_WithItsAssignee()
    {
        IReadOnlyList<TaskChecklist> checklists =
            [List("c1", "Release", 0, new TaskChecklistItem("i1", "A", false, null, null, Ada), Item("i2", "B"))];

        var found = ChecklistItemEdits.FindItem(checklists, "c1", "i1");

        Assert.NotNull(found);
        Assert.Equal("A", found!.Name);
        Assert.Equal(Ada, found.Assignee);
    }

    [Fact]
    public void FindItem_FindsNestedChild()
    {
        IReadOnlyList<TaskChecklist> checklists =
            [List("c1", "Release", 0, Item("i1", "Parent", children: [Item("i2", "Child")]))];

        Assert.Equal("Child", ChecklistItemEdits.FindItem(checklists, "c1", "i2")!.Name);
    }

    [Fact]
    public void FindItem_IsScopedToTheNamedChecklist()
    {
        IReadOnlyList<TaskChecklist> checklists =
            [List("c1", "Release", 0, Item("i1", "A")), List("c2", "QA", 1, Item("i2", "B"))];

        // i2 lives in c2, so looking for it under c1 must miss (no cross-checklist match).
        Assert.Null(ChecklistItemEdits.FindItem(checklists, "c1", "i2"));
        Assert.Equal("B", ChecklistItemEdits.FindItem(checklists, "c2", "i2")!.Name);
    }

    [Fact]
    public void FindItem_UnknownItemOrChecklist_ReturnsNull()
    {
        IReadOnlyList<TaskChecklist> checklists = [List("c1", "Release", 0, Item("i1", "A"))];

        Assert.Null(ChecklistItemEdits.FindItem(checklists, "c1", "nope"));
        Assert.Null(ChecklistItemEdits.FindItem(checklists, "no-list", "i1"));
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

    // ── Move (reorder / reparent, G #569) ─────────────────────────────────────
    // (reuses the FindItem tree-search helper defined above for the SetAssignee tests)

    [Fact]
    public void Move_UpDown_SetsOrderIndex_LeavesParentUntouched()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0,
            Item("i1", "A", orderIndex: 0), Item("i2", "B", orderIndex: 1, parentId: "i1"))];

        var after = ChecklistItemEdits.Move(before, "c1", "i2", newParentId: null, newOrderIndex: 5, clearParent: false);

        var moved = FindItem(after, "i2");
        Assert.Equal(5, moved.OrderIndex);
        Assert.Equal("i1", moved.ParentId); // untouched
    }

    [Fact]
    public void Move_Reparent_SetsNewParent_AndOrderIndex()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0,
            Item("i1", "A", orderIndex: 0), Item("i2", "B", orderIndex: 1))];

        var after = ChecklistItemEdits.Move(before, "c1", "i2", newParentId: "i1", newOrderIndex: 2, clearParent: false);

        var moved = FindItem(after, "i2");
        Assert.Equal("i1", moved.ParentId);
        Assert.Equal(2, moved.OrderIndex);
        // Re-projects nested under i1.
        var row = Rows(after).Single(r => r.ItemId == "i2");
        Assert.Equal(1, row.Depth);
    }

    [Fact]
    public void Move_ClearParent_OutdentsToTopLevel()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0,
            Item("i1", "A", orderIndex: 0), Item("i2", "B", orderIndex: 0, parentId: "i1"))];

        var after = ChecklistItemEdits.Move(before, "c1", "i2", newParentId: null, newOrderIndex: 1, clearParent: true);

        var moved = FindItem(after, "i2");
        Assert.Null(moved.ParentId);
        Assert.Equal(1, moved.OrderIndex);
        Assert.Equal(0, Rows(after).Single(r => r.ItemId == "i2").Depth); // now a root row
    }

    [Fact]
    public void Move_UpdatesANestedChildrenArrayMatch()
    {
        // Nesting expressed via a Children array rather than a flat ParentId pointer.
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0,
            Item("i1", "A", orderIndex: 0, children: [Item("i1a", "child", orderIndex: 0)]))];

        var after = ChecklistItemEdits.Move(before, "c1", "i1a", newParentId: null, newOrderIndex: 9, clearParent: false);

        Assert.Equal(9, FindItem(after, "i1a").OrderIndex);
    }

    [Fact]
    public void Move_MissingChecklistOrItem_IsValueIdenticalNoOp()
    {
        IReadOnlyList<TaskChecklist> before = [List("c1", "Release", 0, Item("i1", "A", orderIndex: 0))];

        // Missing checklist: nothing matches, so i1 keeps its order index.
        Assert.Equal(0, FindItem(ChecklistItemEdits.Move(before, "cX", "i1", null, 3, false), "i1").OrderIndex);
        // Missing item: the tree is rebuilt but value-identical (no item matches).
        Assert.Equal(0, FindItem(ChecklistItemEdits.Move(before, "c1", "nope", null, 3, false), "i1").OrderIndex);
    }
}
