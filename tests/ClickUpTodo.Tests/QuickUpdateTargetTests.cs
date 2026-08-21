using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the Quick Updates write-target seam (#297) that decouples the status/priority/assignee
/// write path from the main-list snapshot: <see cref="TaskService.ApplyFieldChanges"/> (the shared pure
/// reconcile) and <see cref="SingleTaskUpdateTarget"/> (the no-list unit of truth). The parity tests
/// exercise the same reconcile from both entry modes — the snapshot (list) side and the single-task
/// side must settle a committed field identically.
/// </summary>
public sealed class QuickUpdateTargetTests
{
    private static TaskItem Task(
        string id, string? status = null, int? priorityLevel = null, params long[] assigneeIds)
        => new()
        {
            Id = id,
            Name = id,
            StatusName = status,
            PriorityLevel = priorityLevel,
            PriorityName = ClickUpPriority.NameFromLevel(priorityLevel),
            PriorityColor = priorityLevel is null ? null : "#f50000",
            Assignees = [.. assigneeIds.Select(a => new TaskAssignee(a, $"user{a}"))],
        };

    // ---- TaskService.ApplyFieldChanges (the shared reconcile) ----

    [Fact]
    public void ApplyFieldChanges_FoldsAllThreeFields_OntoTheMatchingTask()
    {
        TaskItem[] snapshot = [Task("1", "to do"), Task("2", "to do")];
        var updated = Task("2", "in progress", priorityLevel: 1, 7);

        var result = TaskService.ApplyFieldChanges(snapshot, updated);

        var edited = result.Single(t => t.Id == "2");
        Assert.Equal("in progress", edited.StatusName);
        Assert.Equal(1, edited.PriorityLevel);
        Assert.Equal("Urgent", edited.PriorityName);
        Assert.Equal(["user7"], edited.Assignees.Select(a => a.Name));
    }

    [Fact]
    public void ApplyFieldChanges_LeavesOtherTasksAndOrderUntouched()
    {
        TaskItem[] snapshot = [Task("1", "to do"), Task("2", "to do"), Task("3", "to do")];
        var updated = Task("2", "done");

        var result = TaskService.ApplyFieldChanges(snapshot, updated);

        Assert.Equal(["1", "2", "3"], result.Select(t => t.Id));
        Assert.Equal("to do", result.Single(t => t.Id == "1").StatusName);
        Assert.Equal("to do", result.Single(t => t.Id == "3").StatusName);
    }

    [Fact]
    public void ApplyFieldChanges_IsPure_DoesNotMutateInput()
    {
        var original = Task("1", "to do");
        TaskItem[] snapshot = [original];

        _ = TaskService.ApplyFieldChanges(snapshot, Task("1", "done"));

        Assert.Equal("to do", original.StatusName);
        Assert.Same(original, snapshot[0]);
    }

    [Fact]
    public void ApplyFieldChanges_NoMatchingId_ReturnsEquivalentSnapshot()
    {
        TaskItem[] snapshot = [Task("1", "to do")];

        var result = TaskService.ApplyFieldChanges(snapshot, Task("missing", "done"));

        Assert.Equal(["1"], result.Select(t => t.Id));
        Assert.Equal("to do", result.Single().StatusName);
    }

    // ---- SingleTaskUpdateTarget (the no-list unit of truth) ----

    [Fact]
    public void SingleTarget_Resolve_ReturnsTheTask_ForMatchingId()
    {
        var target = new SingleTaskUpdateTarget(Task("abc", "to do"));

        var found = target.Resolve("abc");

        Assert.NotNull(found);
        Assert.Equal("abc", found!.Id);
    }

    [Fact]
    public void SingleTarget_Resolve_ReturnsNull_ForOtherId()
    {
        var target = new SingleTaskUpdateTarget(Task("abc"));

        Assert.Null(target.Resolve("xyz"));
    }

    [Fact]
    public void SingleTarget_Apply_UpdatesCurrent_WithNoList()
    {
        var target = new SingleTaskUpdateTarget(Task("abc", "to do"));

        target.Apply(target.Resolve("abc")! with { StatusName = "in progress" }, sending: true);

        Assert.Equal("in progress", target.Current.StatusName);
    }

    [Fact]
    public void SingleTarget_ConsecutiveEdits_Compose()
    {
        // Status, then priority, then assignees — each edit builds on the last (the "loaded task is the
        // unit of truth" contract), so the final record carries all three, mirroring a list snapshot.
        var target = new SingleTaskUpdateTarget(Task("abc", "to do"));

        target.Apply(target.Resolve("abc")! with { StatusName = "in progress" }, sending: false);
        target.Apply(
            target.Resolve("abc")! with { PriorityLevel = 2, PriorityName = "High", PriorityColor = "#ffcc00" },
            sending: false);
        target.Apply(
            target.Resolve("abc")! with { Assignees = [new TaskAssignee(9, "user9")] },
            sending: false);

        Assert.Equal("in progress", target.Current.StatusName);
        Assert.Equal(2, target.Current.PriorityLevel);
        Assert.Equal("High", target.Current.PriorityName);
        Assert.Equal(["user9"], target.Current.Assignees.Select(a => a.Name));
    }

    [Fact]
    public void SingleTarget_Apply_IgnoresAMismatchedId()
    {
        var target = new SingleTaskUpdateTarget(Task("abc", "to do"));

        target.Apply(Task("other", "done"), sending: false);

        Assert.Equal("abc", target.Current.Id);
        Assert.Equal("to do", target.Current.StatusName);
    }

    // ---- Parity: both entry modes settle a commit identically ----

    [Fact]
    public void SnapshotAndSingleTarget_SettleTheSameCommit_Identically()
    {
        // The list (snapshot) mode and the single-task mode run the same commit through the shared
        // reconcile; the edited record must come out identical — the "exercised from both entry modes"
        // guarantee (#297).
        var seed = Task("abc", "to do", priorityLevel: 3, 1);
        var commit = seed with { StatusName = "in progress", PriorityLevel = 1, PriorityName = "Urgent", PriorityColor = "#f50000" };

        TaskItem[] snapshot = [Task("z"), seed, Task("y")];
        var viaSnapshot = TaskService.ApplyFieldChanges(snapshot, commit).Single(t => t.Id == "abc");

        var single = new SingleTaskUpdateTarget(seed);
        single.Apply(commit, sending: true);
        var viaSingle = single.Current;

        Assert.Equal(viaSnapshot, viaSingle);
        // Assignees are an IReadOnlyList (reference equality under record ==), so assert the element
        // values explicitly — the parity guarantee must survive a future change that copies the list.
        Assert.Equal(
            viaSnapshot.Assignees.Select(a => (a.Id, a.Name)),
            viaSingle.Assignees.Select(a => (a.Id, a.Name)));
    }

    // ---- ReflectingQuickUpdateTarget (the extra-surface decorator) ----

    /// <summary>A recording target that resolves a fixed record and remembers each apply — stands in for
    /// whichever target the host wrapped (the main-list snapshot, or a single-task record).</summary>
    private sealed class SpyTarget(TaskItem? resolved) : IQuickUpdateTarget
    {
        public TaskItem? Resolved { get; set; } = resolved;
        public List<(TaskItem Task, bool Sending)> Applies { get; } = [];

        public TaskItem? Resolve(string taskId)
            => Resolved is { } t && t.Id == taskId ? t : null;

        public void Apply(TaskItem updated, bool sending)
        {
            Applies.Add((updated, sending));
            // Stand in for a real target's fold: the settled record is what a later Resolve returns.
            if (Resolved is not null && Resolved.Id == updated.Id)
                Resolved = updated;
        }
    }

    [Fact]
    public void Reflecting_ForwardsResolveToTheInnerTarget()
    {
        var inner = new SpyTarget(Task("abc", "to do"));
        var target = new ReflectingQuickUpdateTarget(inner, _ => { });

        Assert.Equal("to do", target.Resolve("abc")?.StatusName);
        Assert.Null(target.Resolve("other"));
    }

    [Fact]
    public void Reflecting_ForwardsApplyToTheInnerTarget()
    {
        var inner = new SpyTarget(Task("abc", "to do"));
        var target = new ReflectingQuickUpdateTarget(inner, _ => { });

        target.Apply(Task("abc", "in progress"), sending: true);

        var (applied, sending) = Assert.Single(inner.Applies);
        Assert.Equal("in progress", applied.StatusName);
        Assert.True(sending);
    }

    [Fact]
    public void Reflecting_ReflectsTheSettledRecord_NotTheCallersArgument()
    {
        // The point of reading back through the inner target: what reaches the extra surface carries the
        // inner reconcile's fold, so a row repainted from it can't disagree with the write target.
        var seed = Task("abc", "to do", priorityLevel: 3, 1);
        var inner = new SingleTaskUpdateTarget(seed);
        var reflected = new List<TaskItem>();
        var target = new ReflectingQuickUpdateTarget(inner, t => reflected.Add(t));

        // A commit that only names the status: the fold keeps the priority/assignees from the held record.
        target.Apply(seed with { StatusName = "blocked" }, sending: true);

        var settled = Assert.Single(reflected);
        Assert.Equal("blocked", settled.StatusName);
        Assert.Equal(3, settled.PriorityLevel);
        Assert.Equal(["user1"], settled.Assignees.Select(a => a.Name));
    }

    [Fact]
    public void Reflecting_ReflectsEveryApply_OptimisticConfirmedAndRevert()
    {
        // The host applies three times per commit — optimistic, server-confirmed, or reverted on failure.
        // A surface that only followed the optimistic one would strand on a value the server rejected.
        var inner = new SingleTaskUpdateTarget(Task("abc", "to do"));
        var reflected = new List<string?>();
        var target = new ReflectingQuickUpdateTarget(inner, t => reflected.Add(t.StatusName));

        target.Apply(Task("abc", "in progress"), sending: true);   // optimistic
        target.Apply(Task("abc", "to do"), sending: false);        // revert (the write failed)

        Assert.Equal(["in progress", "to do"], reflected);
    }

    [Fact]
    public void Reflecting_ReflectsNothing_WhenTheInnerTargetCannotResolveTheTask()
    {
        // Mirrors the inner apply being a no-op (e.g. a list target whose row fell out of the working
        // set): with nothing settled there is nothing to repaint.
        var inner = new SpyTarget(resolved: null);
        var reflected = new List<TaskItem>();
        var target = new ReflectingQuickUpdateTarget(inner, t => reflected.Add(t));

        target.Apply(Task("abc", "in progress"), sending: true);

        Assert.Empty(reflected);
    }
}
