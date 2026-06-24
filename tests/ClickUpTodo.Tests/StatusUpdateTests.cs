using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure in-place status-change helper used by the optimistic UI (issue #11):
/// it must update exactly the targeted task, leave the rest and the ordering intact, and never
/// mutate the input snapshot.
/// </summary>
public sealed class StatusUpdateTests
{
    private static TaskItem Task(string id, string? status) => new() { Id = id, Name = id, StatusName = status };

    [Fact]
    public void ApplyStatusChange_UpdatesOnlyTheMatchingTask()
    {
        TaskItem[] tasks = [Task("1", "to do"), Task("2", "to do"), Task("3", "to do")];

        var updated = TaskService.ApplyStatusChange(tasks, "2", "in progress");

        Assert.Equal("to do", updated[0].StatusName);
        Assert.Equal("in progress", updated[1].StatusName);
        Assert.Equal("to do", updated[2].StatusName);
    }

    [Fact]
    public void ApplyStatusChange_PreservesOrderAndCount()
    {
        TaskItem[] tasks = [Task("a", "x"), Task("b", "y"), Task("c", "z")];

        var updated = TaskService.ApplyStatusChange(tasks, "b", "done");

        Assert.Equal(["a", "b", "c"], updated.Select(t => t.Id));
    }

    [Fact]
    public void ApplyStatusChange_DoesNotMutateInput()
    {
        var original = Task("1", "to do");
        TaskItem[] tasks = [original];

        var updated = TaskService.ApplyStatusChange(tasks, "1", "complete");

        Assert.Equal("to do", original.StatusName);          // input record untouched
        Assert.NotSame(original, updated[0]);                // a new record was produced
        Assert.Equal("complete", updated[0].StatusName);
    }

    [Fact]
    public void ApplyStatusChange_NoMatch_ReturnsEquivalentSnapshot()
    {
        TaskItem[] tasks = [Task("1", "to do"), Task("2", "in progress")];

        var updated = TaskService.ApplyStatusChange(tasks, "missing", "done");

        Assert.Equal(["to do", "in progress"], updated.Select(t => t.StatusName));
    }

    [Fact]
    public void ApplyStatusChange_CanClearStatusToNull()
    {
        TaskItem[] tasks = [Task("1", "to do")];

        var updated = TaskService.ApplyStatusChange(tasks, "1", null);

        Assert.Null(updated[0].StatusName);
    }
}
