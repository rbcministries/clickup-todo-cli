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
}
