using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.FindById"/>, the pure edit-target resolver behind Quick
/// Updates on tasks that aren't the user's own work (#160): it must resolve from the canonical
/// snapshot first, then fall back to the visible rows (where foreign subtasks / context parents live,
/// outside the snapshot), skip null header rows, and never mutate its inputs.
/// </summary>
public sealed class TaskServiceFindByIdTests
{
    private static TaskItem Task(string id, string? status = null) => new() { Id = id, Name = id, StatusName = status };

    [Fact]
    public void FindById_ResolvesFromPrimarySnapshot()
    {
        TaskItem[] primary = [Task("1"), Task("2"), Task("3")];
        TaskItem?[] rows = [Task("1"), Task("2"), Task("3")];

        var found = TaskService.FindById(primary, rows, "2");

        Assert.NotNull(found);
        Assert.Equal("2", found!.Id);
    }

    [Fact]
    public void FindById_FallsBackToRows_WhenNotInPrimary()
    {
        // A foreign subtask / context parent lives only in the visible rows, not the snapshot.
        TaskItem[] primary = [Task("1"), Task("2")];
        TaskItem?[] rows = [Task("1"), Task("2"), Task("foreign")];

        var found = TaskService.FindById(primary, rows, "foreign");

        Assert.NotNull(found);
        Assert.Equal("foreign", found!.Id);
    }

    [Fact]
    public void FindById_PrefersPrimary_OverASameIdRow()
    {
        // The snapshot carries the current optimistic value; the row copy may lag — primary wins.
        TaskItem[] primary = [Task("1", "in progress")];
        TaskItem?[] rows = [Task("1", "to do")];

        var found = TaskService.FindById(primary, rows, "1");

        Assert.NotNull(found);
        Assert.Equal("in progress", found!.StatusName);
    }

    [Fact]
    public void FindById_SkipsNullHeaderRows()
    {
        TaskItem[] primary = [];
        TaskItem?[] rows = [null, Task("foreign"), null];

        var found = TaskService.FindById(primary, rows, "foreign");

        Assert.NotNull(found);
        Assert.Equal("foreign", found!.Id);
    }

    [Fact]
    public void FindById_ReturnsNull_WhenAbsentFromBoth()
    {
        TaskItem[] primary = [Task("1")];
        TaskItem?[] rows = [null, Task("1")];

        var found = TaskService.FindById(primary, rows, "missing");

        Assert.Null(found);
    }

    [Fact]
    public void FindById_DoesNotMutateInputs()
    {
        var a = Task("1", "to do");
        var b = Task("foreign", "to do");
        TaskItem[] primary = [a];
        TaskItem?[] rows = [a, b];

        _ = TaskService.FindById(primary, rows, "foreign");

        Assert.Equal("to do", a.StatusName);
        Assert.Equal("to do", b.StatusName);
        Assert.Equal(["1"], primary.Select(t => t.Id));
        Assert.Equal(["1", "foreign"], rows.Select(r => r!.Id));
    }
}
