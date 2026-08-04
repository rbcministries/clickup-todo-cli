using System.Text;
using ClickUpTodo.ClickUp;
using ClickUpTodo.ClickUp.Generated.Models;
using Microsoft.Kiota.Serialization.Json;

namespace ClickUpTodo.Tests;

/// <summary>
/// Verifies the Kiota-touching seam of the checklist read model (#454):
/// <see cref="ClickUpClient.MapDetail"/> surfaces a task's <c>checklists</c> onto
/// <see cref="TaskDetail.Checklists"/>, with the container fields read from generated properties and the
/// loosely-typed <c>items</c> re-read from <c>AdditionalData</c> via <see cref="ChecklistReader"/>.
/// Exercises the real deserialize → map round-trip with no network (a plain <see cref="FactAttribute"/>),
/// mirroring <see cref="ClickUpClientCustomFieldTests"/>.
/// </summary>
public sealed class ClickUpClientChecklistMapTests
{
    private static async Task<TaskObject> DeserializeTaskAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var node = await new JsonParseNodeFactory().GetRootParseNodeAsync("application/json", stream);
        return node.GetObjectValue(TaskObject.CreateFromDiscriminatorValue)!;
    }

    [Fact]
    public async Task MapDetail_NoChecklistsKey_YieldsEmptyList()
    {
        var t = await DeserializeTaskAsync("""{"id":"t1","name":"Task"}""");

        var detail = ClickUpClient.MapDetail(t);

        Assert.NotNull(detail.Checklists);
        Assert.Empty(detail.Checklists);
    }

    [Fact]
    public async Task MapDetail_EmptyChecklistsArray_YieldsEmptyList()
    {
        var t = await DeserializeTaskAsync("""{"id":"t1","name":"Task","checklists":[]}""");

        Assert.Empty(ClickUpClient.MapDetail(t).Checklists);
    }

    [Fact]
    public async Task MapDetail_TwoChecklists_WithNestedItemsAndMixedResolved_MapEndToEnd()
    {
        var t = await DeserializeTaskAsync("""
            {"id":"t1","name":"Task","checklists":[
                {"id":"c1","name":"Release steps","orderindex":0,"resolved":1,"unresolved":1,
                 "items":[
                    {"id":"i1","name":"Cut tag","resolved":true,"orderindex":0,"assignee":null},
                    {"id":"i2","name":"Draft notes","resolved":false,"orderindex":1,
                     "assignee":{"id":7,"username":"Rel Bot"},
                     "children":[{"id":"i2a","name":"Sub note","resolved":false,"orderindex":0}]}]},
                {"id":"c2","name":"QA","orderindex":1,"resolved":0,"unresolved":0,"items":[]}]}
            """);

        var checklists = ClickUpClient.MapDetail(t).Checklists;

        Assert.Equal(2, checklists.Count);

        var release = checklists[0];
        Assert.Equal("c1", release.Id);
        Assert.Equal("Release steps", release.Name);
        Assert.Equal(0d, release.OrderIndex);
        Assert.Equal(1, release.Resolved);
        Assert.Equal(1, release.Unresolved);
        Assert.Equal(2, release.Items.Count);
        Assert.True(release.Items[0].Resolved);
        Assert.Null(release.Items[0].Assignee);
        Assert.False(release.Items[1].Resolved);
        Assert.Equal(7, release.Items[1].Assignee!.Id);
        Assert.Equal("Rel Bot", release.Items[1].Assignee!.Name);
        Assert.Single(release.Items[1].Children);
        Assert.Equal("i2a", release.Items[1].Children[0].Id);

        var qa = checklists[1];
        Assert.Equal("c2", qa.Id);
        Assert.Empty(qa.Items);
    }

    [Fact]
    public async Task MapDetail_ChecklistWithNullAssigneeAndNumericStringOrderIndex()
    {
        var t = await DeserializeTaskAsync("""
            {"id":"t1","name":"Task","checklists":[
                {"id":"c1","name":"List","orderindex":0,"resolved":0,"unresolved":1,
                 "items":[{"id":"i1","name":"Item","resolved":false,"orderindex":"4.00000","assignee":null}]}]}
            """);

        var item = Assert.Single(ClickUpClient.MapDetail(t).Checklists[0].Items);
        Assert.Equal(4d, item.OrderIndex);
        Assert.Null(item.Assignee);
    }

    [Fact]
    public async Task MapDetail_ChecklistMissingCounts_DefaultToZero()
    {
        var t = await DeserializeTaskAsync("""
            {"id":"t1","name":"Task","checklists":[{"id":"c1","name":"List"}]}
            """);

        var checklist = Assert.Single(ClickUpClient.MapDetail(t).Checklists);
        Assert.Equal(0, checklist.Resolved);
        Assert.Equal(0, checklist.Unresolved);
        Assert.Empty(checklist.Items);
    }
}
