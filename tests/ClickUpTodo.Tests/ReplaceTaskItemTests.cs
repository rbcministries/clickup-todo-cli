using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure wholesale snapshot-replace helper behind the cross-tab nudge reconcile
/// (#376): it must replace exactly the matching task with the authoritative fresh record — carrying
/// the assignee ids / parent / due date a per-field <see cref="TaskService.ApplyFieldChanges"/> fold
/// would leave stale — while leaving the rest and the ordering intact, and never mutating the input.
/// </summary>
public sealed class ReplaceTaskItemTests
{
    private static TaskItem Task(string id, string? status = "to do") =>
        new() { Id = id, Name = id, StatusName = status };

    [Fact]
    public void ReplaceTaskItem_ReplacesOnlyTheMatchingTask()
    {
        TaskItem[] tasks = [Task("1"), Task("2"), Task("3")];

        var updated = TaskService.ReplaceTaskItem(tasks, Task("2", "in progress"));

        Assert.Equal("to do", updated[0].StatusName);
        Assert.Equal("in progress", updated[1].StatusName);
        Assert.Equal("to do", updated[2].StatusName);
    }

    [Fact]
    public void ReplaceTaskItem_PreservesOrderAndCount()
    {
        TaskItem[] tasks = [Task("a"), Task("b"), Task("c")];

        var updated = TaskService.ReplaceTaskItem(tasks, Task("b", "done"));

        Assert.Equal(["a", "b", "c"], updated.Select(t => t.Id));
    }

    [Fact]
    public void ReplaceTaskItem_DoesNotMutateInput()
    {
        var original = Task("1");
        TaskItem[] tasks = [original];

        var updated = TaskService.ReplaceTaskItem(tasks, Task("1", "complete"));

        Assert.Equal("to do", original.StatusName);   // input record untouched
        Assert.NotSame(original, updated[0]);          // the fresh record took its place
        Assert.Equal("complete", updated[0].StatusName);
    }

    [Fact]
    public void ReplaceTaskItem_NoMatch_ReturnsEquivalentSnapshot()
    {
        TaskItem[] tasks = [Task("1"), Task("2", "in progress")];

        var updated = TaskService.ReplaceTaskItem(tasks, Task("missing", "done"));

        Assert.Equal(["1", "2"], updated.Select(t => t.Id));
        Assert.Equal("to do", updated[0].StatusName);
        Assert.Equal("in progress", updated[1].StatusName);
    }

    [Fact]
    public void ReplaceTaskItem_CarriesFullFidelityFieldsAPerFieldFoldWouldDrop()
    {
        // The stale row: an assignee by name only (id 0, as a TaskDetail overlay produced), no parent,
        // no due date — exactly what the old status+priority overlay left behind.
        var stale = new TaskItem
        {
            Id = "t1",
            Name = "Task 1",
            StatusName = "to do",
            Assignees = [new TaskAssignee(0, "Ada Lovelace")],
            ParentId = null,
            DueDateMs = null,
        };
        // The authoritative full item from GetTaskItemAsync: real assignee id, a parent, a due date.
        var fresh = new TaskItem
        {
            Id = "t1",
            Name = "Task 1 (renamed cross-tab)",
            StatusName = "in progress",
            Assignees = [new TaskAssignee(101, "Ada Lovelace")],
            ParentId = "p9",
            DueDateMs = 1751500000000,
        };

        var updated = TaskService.ReplaceTaskItem([stale], fresh);

        var row = Assert.Single(updated);
        Assert.Equal("Task 1 (renamed cross-tab)", row.Name);
        Assert.Equal(101, Assert.Single(row.Assignees).Id);   // real id, not the placeholder 0
        Assert.Equal("p9", row.ParentId);                     // parent now known
        Assert.Equal(1751500000000, row.DueDateMs);           // due date reflected
    }
}
