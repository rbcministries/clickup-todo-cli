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
    public void Map_CarriesCustomId_WhenSet()
    {
        // The list row shows the Space custom id beside the badges, so Map must carry it through.
        var mapped = ClickUpClient.Map(new TaskObject { Id = "abc", CustomId = "ABC-123", Name = "A task" });

        Assert.Equal("ABC-123", mapped.CustomId);
    }

    [Fact]
    public void Map_NullCustomId_LeavesItNull_SoTheRowFallsBackToTaskId()
    {
        var mapped = ClickUpClient.Map(new TaskObject { Id = "abc", Name = "A task" });

        Assert.Null(mapped.CustomId);
    }

    [Fact]
    public void Map_CarriesStatusType_ForTheShowCompletedToggle()
    {
        // The F12 "Show Completed" toggle (#178) keys off ClickUp's status.type; Map must carry it.
        var mapped = ClickUpClient.Map(new TaskObject
        {
            Id = "abc",
            Name = "A task",
            Status = new Status { StatusProp = "complete", Color = "#008844", Type = "closed" },
        });

        Assert.Equal("complete", mapped.StatusName);
        Assert.Equal("closed", mapped.StatusType);
    }

    [Fact]
    public void Map_NullStatus_LeavesStatusTypeNull()
    {
        var mapped = ClickUpClient.Map(new TaskObject { Id = "abc", Name = "A task" });

        Assert.Null(mapped.StatusType);
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

    // ── MapMembers (#73): Workspace members → WorkspaceMember for username/email → id resolution ──

    [Fact]
    public void MapMembers_CarriesIdUsernameAndEmail()
    {
        List<Member> members =
        [
            new() { User = new User { Id = 42, Username = "Ben", Email = "ben@example.com" } },
            new() { User = new User { Id = 7, Email = "teammate@example.com" } }, // username may be absent
        ];

        var mapped = ClickUpClient.MapMembers(members);

        Assert.Equal(2, mapped.Count);
        Assert.Equal(new WorkspaceMember(42, "Ben", "ben@example.com"), mapped[0]);
        Assert.Equal(new WorkspaceMember(7, null, "teammate@example.com"), mapped[1]);
    }

    [Fact]
    public void MapMembers_DropsEntriesWithNoId()
    {
        // An id is required to build an assignees[] filter, so a member without one is dropped.
        List<Member> members =
        [
            new() { User = new User { Username = "no-id" } },
            new() { User = null },
            new() { User = new User { Id = 5, Username = "keep" } },
        ];

        var mapped = ClickUpClient.MapMembers(members);

        Assert.Equal([new WorkspaceMember(5, "keep", null)], mapped);
    }

    [Fact]
    public void MapMembers_Null_YieldsEmptyList() => Assert.Empty(ClickUpClient.MapMembers(null));
}
