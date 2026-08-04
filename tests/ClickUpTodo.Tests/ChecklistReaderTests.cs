using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure unit tests for <see cref="ChecklistReader"/> (#454): the raw checklist-<c>items</c> JSON → domain
/// <see cref="TaskChecklistItem"/> reader, exercised with hand-built JSON (no network). Covers the
/// documented ClickUp inconsistencies the reader must tolerate — number-or-numeric-string
/// <c>orderindex</c>, <c>assignee</c> as null / bare id / user object, and nesting expressed via a
/// <c>parent</c> id-pointer and/or a populated <c>children</c> array.
/// </summary>
public sealed class ChecklistReaderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReadItems_NoItemsKey_ReturnsEmpty()
        => Assert.Empty(ChecklistReader.ReadItems(Parse("""{"id":"c1","name":"Checklist"}""")));

    [Fact]
    public void ReadItems_ItemsNotArray_ReturnsEmpty()
        => Assert.Empty(ChecklistReader.ReadItems(Parse("""{"items":{"nope":true}}""")));

    [Fact]
    public void ReadItems_NonObjectChecklist_ReturnsEmpty()
        => Assert.Empty(ChecklistReader.ReadItems(Parse("\"just a string\"")));

    [Fact]
    public void ReadItems_PreservesApiOrder_AndMixedResolvedState()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[
                {"id":"a","name":"first","resolved":false,"orderindex":0},
                {"id":"b","name":"second","resolved":true,"orderindex":1}]}
            """));

        Assert.Equal(["a", "b"], items.Select(i => i.Id));
        Assert.Equal(["first", "second"], items.Select(i => i.Name));
        Assert.False(items[0].Resolved);
        Assert.True(items[1].Resolved);
    }

    [Fact]
    public void ReadItems_ItemWithoutId_IsSkipped()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[{"name":"no id"},{"id":"keep","name":"has id"}]}
            """));

        Assert.Equal(["keep"], items.Select(i => i.Id));
    }

    [Fact]
    public void ReadItems_OrderIndex_NumberOrNumericString()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[
                {"id":"a","orderindex":2},
                {"id":"b","orderindex":"3.5"},
                {"id":"c"}]}
            """));

        Assert.Equal(2d, items[0].OrderIndex);
        Assert.Equal(3.5d, items[1].OrderIndex);
        Assert.Null(items[2].OrderIndex);
    }

    [Fact]
    public void ReadItems_AssigneeNull_YieldsNoAssignee()
    {
        var items = ChecklistReader.ReadItems(Parse("""{"items":[{"id":"a","assignee":null}]}"""));

        Assert.Null(items[0].Assignee);
    }

    [Fact]
    public void ReadItems_AssigneeAbsent_YieldsNoAssignee()
    {
        var items = ChecklistReader.ReadItems(Parse("""{"items":[{"id":"a"}]}"""));

        Assert.Null(items[0].Assignee);
    }

    [Fact]
    public void ReadItems_AssigneeBareNumericId_KeepsIdWithEmptyName()
    {
        var items = ChecklistReader.ReadItems(Parse("""{"items":[{"id":"a","assignee":183}]}"""));

        Assert.NotNull(items[0].Assignee);
        Assert.Equal(183, items[0].Assignee!.Id);
        Assert.Equal("", items[0].Assignee!.Name);
    }

    [Fact]
    public void ReadItems_AssigneeBareNumericStringId_KeepsId()
    {
        var items = ChecklistReader.ReadItems(Parse("""{"items":[{"id":"a","assignee":"999"}]}"""));

        Assert.Equal(999, items[0].Assignee!.Id);
    }

    [Fact]
    public void ReadItems_AssigneeUserObject_UsesUsername()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[{"id":"a","assignee":{"id":42,"username":"Ben Seymour","email":"ben@example.com"}}]}
            """));

        Assert.Equal(42, items[0].Assignee!.Id);
        Assert.Equal("Ben Seymour", items[0].Assignee!.Name);
    }

    [Fact]
    public void ReadItems_AssigneeUserObject_FallsBackToEmailLocalPart()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[{"id":"a","assignee":{"id":42,"email":"jane.doe@example.com"}}]}
            """));

        Assert.Equal("jane.doe", items[0].Assignee!.Name);
    }

    [Fact]
    public void ReadItems_NestingViaChildrenArray_IsReadRecursively()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[
                {"id":"parent","name":"P","children":[
                    {"id":"child","name":"C","children":[
                        {"id":"grandchild","name":"G"}]}]}]}
            """));

        Assert.Single(items);
        Assert.Equal("parent", items[0].Id);
        Assert.Single(items[0].Children);
        Assert.Equal("child", items[0].Children[0].Id);
        Assert.Equal("grandchild", items[0].Children[0].Children[0].Id);
    }

    [Fact]
    public void ReadItems_NestingViaParentPointer_KeepsFlatWithParentIds()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[
                {"id":"parent","name":"P","parent":null},
                {"id":"child","name":"C","parent":"parent"}]}
            """));

        // Expressed via parent-pointers only: items stay flat (each carries ParentId), children empty —
        // the row projection (B, #455) is free to reconstruct the tree from ParentId.
        Assert.Equal(2, items.Count);
        Assert.Null(items[0].ParentId);
        Assert.Equal("parent", items[1].ParentId);
        Assert.All(items, i => Assert.Empty(i.Children));
    }

    [Fact]
    public void ReadItems_ResolvedTolerance_NumericAndString()
    {
        var items = ChecklistReader.ReadItems(Parse("""
            {"items":[
                {"id":"a","resolved":1},
                {"id":"b","resolved":0},
                {"id":"c","resolved":"true"},
                {"id":"d","resolved":"false"}]}
            """));

        Assert.True(items[0].Resolved);
        Assert.False(items[1].Resolved);
        Assert.True(items[2].Resolved);
        Assert.False(items[3].Resolved);
    }
}
