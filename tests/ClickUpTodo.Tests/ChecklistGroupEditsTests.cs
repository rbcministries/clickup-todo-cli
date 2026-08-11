using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure unit tests for <see cref="ChecklistGroupEdits"/> (F, #459): the immutable checklist-list transforms
/// behind the Checklists tab's group add / rename / delete (plus the <see cref="ChecklistGroupEdits.NewChecklistId"/>
/// create-response diff). Like <see cref="ChecklistItemEditsTests"/>, the transforms are asserted through the
/// <see cref="ChecklistArranger"/> projection — the rows the screen actually renders — so "the group appears /
/// disappears / renames and the counts follow" is verified end-to-end without a terminal.
/// </summary>
public sealed class ChecklistGroupEditsTests
{
    private static TaskChecklistItem Item(string id, string name = "item", bool resolved = false)
        => new(id, name, resolved, null, null, null, null);

    private static TaskChecklist List(string id, string name, double? orderIndex, params TaskChecklistItem[] items)
        => new(id, name, orderIndex, 0, 0, items);

    private static IReadOnlyList<ChecklistRow> Rows(IReadOnlyList<TaskChecklist> checklists)
        => ChecklistArranger.Project(checklists).Rows;

    private static ChecklistRow Header(IReadOnlyList<TaskChecklist> checklists, string checklistId)
        => Rows(checklists).Single(r => r.IsHeader && r.ChecklistId == checklistId);

    private static bool HasGroup(IReadOnlyList<TaskChecklist> checklists, string checklistId)
        => Rows(checklists).Any(r => r.IsHeader && r.ChecklistId == checklistId);

    // ── Rename ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_ChangesOnlyTheTargetGroupsName()
    {
        var before = new[] { List("c1", "Rel", 0, Item("i1")), List("c2", "QA", 1) };
        var after = ChecklistGroupEdits.Rename(before, "c1", "Release steps");
        Assert.Equal("Release steps", Header(after, "c1").Text);
        Assert.Equal("QA", Header(after, "c2").Text);              // sibling untouched
        Assert.Equal("Rel", Header(before, "c1").Text);            // input not mutated
    }

    [Fact]
    public void Rename_MissingGroup_IsNoOp()
    {
        var before = new[] { List("c1", "Rel", 0) };
        var after = ChecklistGroupEdits.Rename(before, "nope", "X");
        Assert.Equal("Rel", Header(after, "c1").Text);
    }

    [Fact]
    public void Rename_EmptyList_IsNoOp()
        => Assert.Empty(ChecklistGroupEdits.Rename([], "c1", "X"));

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_DropsTheGroupAndAllItsItems()
    {
        var before = new[] { List("c1", "Rel", 0, Item("i1"), Item("i2")), List("c2", "QA", 1, Item("j1")) };
        var after = ChecklistGroupEdits.Remove(before, "c1");
        Assert.False(HasGroup(after, "c1"));
        Assert.True(HasGroup(after, "c2"));
        // c1's items are gone with it; only c2's item remains.
        Assert.DoesNotContain(Rows(after), r => r.Kind == ChecklistRowKind.Item && r.ChecklistId == "c1");
        Assert.Contains(Rows(after), r => r.Kind == ChecklistRowKind.Item && r.ItemId == "j1");
    }

    [Fact]
    public void Remove_MissingGroup_IsNoOp()
    {
        var before = new[] { List("c1", "Rel", 0) };
        Assert.True(HasGroup(ChecklistGroupEdits.Remove(before, "nope"), "c1"));
    }

    [Fact]
    public void Remove_LastGroup_LeavesAnEmptyList()
    {
        var before = new[] { List("c1", "Rel", 0, Item("i1")) };
        var after = ChecklistGroupEdits.Remove(before, "c1");
        Assert.Empty(after);
        Assert.True(ChecklistArranger.Project(after).IsEmpty);      // the empty-state cue for the glue
    }

    // ── InsertProvisional ──────────────────────────────────────────────────────

    [Fact]
    public void InsertProvisional_AppendsTheNewGroup()
    {
        var before = new[] { List("c1", "Rel", 0) };
        var provisional = List(ChecklistGroupEdits.ProvisionalChecklistId, "New checklist", 1);
        var after = ChecklistGroupEdits.InsertProvisional(before, provisional);
        Assert.True(HasGroup(after, "c1"));
        Assert.True(HasGroup(after, ChecklistGroupEdits.ProvisionalChecklistId));
        Assert.Equal("New checklist", Header(after, ChecklistGroupEdits.ProvisionalChecklistId).Text);
    }

    [Fact]
    public void InsertProvisional_OntoAnEmptyList_CreatesTheFirstGroup()
    {
        var provisional = List(ChecklistGroupEdits.ProvisionalChecklistId, "First", 0);
        var after = ChecklistGroupEdits.InsertProvisional([], provisional);
        Assert.False(ChecklistArranger.Project(after).IsEmpty);
        Assert.True(HasGroup(after, ChecklistGroupEdits.ProvisionalChecklistId));
    }

    // ── NewChecklistId ──────────────────────────────────────────────────────────

    [Fact]
    public void NewChecklistId_FindsTheSingleAddedGroup()
    {
        var before = new[] { List("c1", "Rel", 0) };
        var after = new[] { List("c1", "Rel", 0), List("c2", "QA", 1) };
        Assert.Equal("c2", ChecklistGroupEdits.NewChecklistId(before, after));
    }

    [Fact]
    public void NewChecklistId_FromEmptyBefore_FindsTheFirstGroup()
        => Assert.Equal("c1", ChecklistGroupEdits.NewChecklistId([], [List("c1", "Rel", 0)]));

    [Fact]
    public void NewChecklistId_NoNewGroup_IsNull()
    {
        var same = new[] { List("c1", "Rel", 0) };
        Assert.Null(ChecklistGroupEdits.NewChecklistId(same, same));
    }

    [Fact]
    public void NewChecklistId_MultipleNewGroups_IsNull_Ambiguous()
    {
        var before = new[] { List("c1", "Rel", 0) };
        var after = new[] { List("c1", "Rel", 0), List("c2", "QA", 1), List("c3", "Docs", 2) };
        Assert.Null(ChecklistGroupEdits.NewChecklistId(before, after));
    }

    [Fact]
    public void NewChecklistId_NullAfter_IsNull()
        => Assert.Null(ChecklistGroupEdits.NewChecklistId([List("c1", "Rel", 0)], null));
}
