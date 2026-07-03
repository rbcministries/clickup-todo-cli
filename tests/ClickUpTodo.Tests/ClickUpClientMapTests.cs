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
