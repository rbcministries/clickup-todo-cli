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

    // ── ApplyPriorityChange (the priority sibling, #157) ─────────────────────────

    private static TaskItem Pri(string id, int? level) => new()
    {
        Id = id,
        Name = id,
        PriorityLevel = level,
        PriorityName = ClickUpPriority.NameFromLevel(level),
        PriorityColor = ClickUpPriority.ColorFromLevel(level),
    };

    [Fact]
    public void ApplyPriorityChange_UpdatesOnlyTheMatchingTask_WithLevelNameAndColor()
    {
        TaskItem[] tasks = [Pri("1", 3), Pri("2", 3), Pri("3", 3)];

        var updated = TaskService.ApplyPriorityChange(tasks, "2", 1, "Urgent", "#f50000");

        Assert.Equal(3, updated[0].PriorityLevel);
        Assert.Equal(1, updated[1].PriorityLevel);
        Assert.Equal("Urgent", updated[1].PriorityName);
        Assert.Equal("#f50000", updated[1].PriorityColor);
        Assert.Equal(3, updated[2].PriorityLevel);
    }

    [Fact]
    public void ApplyPriorityChange_PreservesOrderAndCount_AndDoesNotMutateInput()
    {
        var original = Pri("b", 2);
        TaskItem[] tasks = [Pri("a", 1), original, Pri("c", 4)];

        var updated = TaskService.ApplyPriorityChange(tasks, "b", null, null, null);

        Assert.Equal(["a", "b", "c"], updated.Select(t => t.Id));
        Assert.Equal(2, original.PriorityLevel);   // input record untouched
        Assert.NotSame(original, updated[1]);
    }

    [Fact]
    public void ApplyPriorityChange_CanClearPriorityToNull()
    {
        TaskItem[] tasks = [Pri("1", 1)];

        var updated = TaskService.ApplyPriorityChange(tasks, "1", null, null, null);

        Assert.Null(updated[0].PriorityLevel);
        Assert.Null(updated[0].PriorityName);
        Assert.Null(updated[0].PriorityColor);
    }

    [Fact]
    public void ApplyPriorityChange_NoMatch_ReturnsEquivalentSnapshot()
    {
        // Editing a task that isn't in the canonical snapshot — a foreign subtask / context parent
        // (#160) — must leave the snapshot untouched (the priority sibling of the status no-match test).
        TaskItem[] tasks = [Pri("1", 2), Pri("2", 4)];

        var updated = TaskService.ApplyPriorityChange(tasks, "missing", 1, "Urgent", "#f50000");

        Assert.Equal(["1", "2"], updated.Select(t => t.Id));
        Assert.Equal([2, 4], updated.Select(t => t.PriorityLevel));
    }

    // ── ApplyAssigneesChange (the assignee sibling, #158) ────────────────────────

    private static TaskItem Asg(string id, params TaskAssignee[] assignees) => new()
    {
        Id = id,
        Name = id,
        Assignees = assignees,
    };

    [Fact]
    public void ApplyAssigneesChange_UpdatesOnlyTheMatchingTask()
    {
        TaskItem[] tasks =
        [
            Asg("1", new TaskAssignee(1, "Ada")),
            Asg("2", new TaskAssignee(1, "Ada")),
            Asg("3", new TaskAssignee(1, "Ada")),
        ];

        var confirmed = new[] { new TaskAssignee(1, "Ada"), new TaskAssignee(2, "Grace") };
        var updated = TaskService.ApplyAssigneesChange(tasks, "2", confirmed);

        Assert.Equal([1], updated[0].Assignees.Select(a => a.Id));
        Assert.Equal([1, 2], updated[1].Assignees.Select(a => a.Id));
        Assert.Equal([1], updated[2].Assignees.Select(a => a.Id));
    }

    [Fact]
    public void ApplyAssigneesChange_PreservesOrderAndCount_AndDoesNotMutateInput()
    {
        var original = Asg("b", new TaskAssignee(1, "Ada"));
        TaskItem[] tasks = [Asg("a", new TaskAssignee(9, "Nine")), original, Asg("c")];

        var updated = TaskService.ApplyAssigneesChange(tasks, "b", [new TaskAssignee(2, "Grace")]);

        Assert.Equal(["a", "b", "c"], updated.Select(t => t.Id));
        Assert.Equal([1], original.Assignees.Select(a => a.Id));   // input record untouched
        Assert.NotSame(original, updated[1]);
        Assert.Equal([2], updated[1].Assignees.Select(a => a.Id));
    }

    [Fact]
    public void ApplyAssigneesChange_NoMatch_ReturnsEquivalentSnapshot()
    {
        TaskItem[] tasks = [Asg("1", new TaskAssignee(1, "Ada")), Asg("2", new TaskAssignee(2, "Grace"))];

        var updated = TaskService.ApplyAssigneesChange(tasks, "missing", []);

        Assert.Equal([1], updated[0].Assignees.Select(a => a.Id));
        Assert.Equal([2], updated[1].Assignees.Select(a => a.Id));
    }

    [Fact]
    public void ApplyAssigneesChange_CanClearAssigneesToEmpty()
    {
        TaskItem[] tasks = [Asg("1", new TaskAssignee(1, "Ada"), new TaskAssignee(2, "Grace"))];

        var updated = TaskService.ApplyAssigneesChange(tasks, "1", []);

        Assert.Empty(updated[0].Assignees);
    }
}
