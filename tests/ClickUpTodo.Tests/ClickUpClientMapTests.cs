using ClickUpTodo.ClickUp;
using ClickUpTodo.ClickUp.Generated.Models;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="ClickUpClient.Map"/> — the (offline) mapping from the generated
/// <see cref="TaskObject"/> onto the stable <see cref="TaskItem"/>. Focused on assignees (#68), which
/// the list item now carries so the F3 view can filter/sort/group by them.
/// </summary>
public sealed class ClickUpClientMapTests
{
    [Fact]
    public void Map_CarriesAssignees_IdAndDisplayName()
    {
        var t = new TaskObject
        {
            Id = "abc",
            Name = "A task",
            Assignees =
            [
                new User { Id = 42, Username = "Ben" },
                new User { Id = 7, Email = "teammate@example.com" }, // no username → falls back to email
            ],
        };

        var mapped = ClickUpClient.Map(t);

        Assert.Equal(2, mapped.Assignees.Count);
        Assert.Equal(new TaskAssignee(42, "Ben"), mapped.Assignees[0]);
        Assert.Equal(new TaskAssignee(7, "teammate@example.com"), mapped.Assignees[1]);
    }

    [Fact]
    public void Map_NoAssignees_YieldsEmptyList()
    {
        var mapped = ClickUpClient.Map(new TaskObject { Id = "abc", Name = "A task" });

        Assert.Empty(mapped.Assignees);
    }

    [Fact]
    public void MapDetail_CarriesStatusAndPriorityColors()
    {
        // The detail Other tab colours the Status/Priority values (#66), so MapDetail must carry both
        // hex colours through from the generated Status/Priority objects.
        var t = new TaskObject
        {
            Id = "abc",
            Name = "A task",
            Status = new Status { StatusProp = "in progress", Color = "#00ff00" },
            Priority = new Priority { PriorityProp = "high", Color = "#ff0000" },
        };

        var detail = ClickUpClient.MapDetail(t);

        Assert.Equal("in progress", detail.StatusName);
        Assert.Equal("#00ff00", detail.StatusColor);
        Assert.Equal("high", detail.Priority);
        Assert.Equal("#ff0000", detail.PriorityColor);
    }

    [Fact]
    public void MapDetail_NoStatusOrPriority_LeavesColorsNull()
    {
        var detail = ClickUpClient.MapDetail(new TaskObject { Id = "abc", Name = "A task" });

        Assert.Null(detail.StatusColor);
        Assert.Null(detail.PriorityColor);
    }
}
