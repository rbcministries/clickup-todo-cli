using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.MissingParentIds"/> — which parents the nested subtasks view
/// (#46) must fetch as context headers.
/// </summary>
public sealed class MissingParentIdsTests
{
    private static TaskItem Task(string id, string? parent = null)
        => new() { Id = id, Name = id, ParentId = parent };

    [Fact]
    public void ReturnsParentReferencedButAbsent()
    {
        TaskItem[] snapshot = [Task("c", parent: "P")];

        Assert.Equal(["P"], TaskService.MissingParentIds(snapshot));
    }

    [Fact]
    public void SkipsParentAlreadyInSnapshot()
    {
        TaskItem[] snapshot = [Task("p"), Task("c", parent: "p")];

        Assert.Empty(TaskService.MissingParentIds(snapshot));
    }

    [Fact]
    public void DedupesRepeatedParent_PreservesFirstAppearanceOrder()
    {
        TaskItem[] snapshot =
        [
            Task("c1", parent: "B"),
            Task("c2", parent: "A"),
            Task("c3", parent: "B"),
        ];

        Assert.Equal(["B", "A"], TaskService.MissingParentIds(snapshot));
    }

    [Fact]
    public void IgnoresTasksWithoutParent()
    {
        TaskItem[] snapshot = [Task("a"), Task("b"), Task("c")];

        Assert.Empty(TaskService.MissingParentIds(snapshot));
    }

    [Fact]
    public void EmptySnapshot_ReturnsEmpty()
    {
        Assert.Empty(TaskService.MissingParentIds([]));
    }
}
