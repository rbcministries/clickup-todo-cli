using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.ForeignDescendants"/> — which list-scoped tasks get pulled in
/// as not-mine subtasks of an in-view parent (#70): those absent from the snapshot whose parent chain
/// reaches a snapshot task.
/// </summary>
public sealed class ForeignDescendantsTests
{
    private static TaskItem Task(string id, string? parent = null, string? list = "L")
        => new() { Id = id, Name = id, ParentId = parent, ListId = list };

    private static IReadOnlyList<string> Ids(IEnumerable<TaskItem> tasks) => tasks.Select(t => t.Id).ToList();

    [Fact]
    public void PullsInDirectChildOfSnapshotParent()
    {
        TaskItem[] snapshot = [Task("P")];
        TaskItem[] listTasks = [Task("P"), Task("c", parent: "P")];

        Assert.Equal(["c"], Ids(TaskService.ForeignDescendants(snapshot, listTasks)));
    }

    [Fact]
    public void PullsInGrandchildThroughANotInSnapshotChild()
    {
        TaskItem[] snapshot = [Task("P")];
        // Neither the child nor grandchild is assigned to me (absent from the snapshot); both chain up to P.
        TaskItem[] listTasks = [Task("c", parent: "P"), Task("gc", parent: "c")];

        Assert.Equal(["c", "gc"], Ids(TaskService.ForeignDescendants(snapshot, listTasks)));
    }

    [Fact]
    public void SkipsTasksAlreadyInSnapshot()
    {
        TaskItem[] snapshot = [Task("P"), Task("c", parent: "P")]; // c is mine — already shown
        TaskItem[] listTasks = [Task("P"), Task("c", parent: "P")];

        Assert.Empty(TaskService.ForeignDescendants(snapshot, listTasks));
    }

    [Fact]
    public void IgnoresUnrelatedTasksInTheSameList()
    {
        TaskItem[] snapshot = [Task("P")];
        // A teammate's top-level task and a subtask of a different (not-in-view) parent — neither
        // chains up to a snapshot task, so neither is pulled in.
        TaskItem[] listTasks = [Task("other"), Task("x", parent: "Q"), Task("c", parent: "P")];

        Assert.Equal(["c"], Ids(TaskService.ForeignDescendants(snapshot, listTasks)));
    }

    [Fact]
    public void DedupesRepeatedIdsAcrossLists_PreservesFirstAppearance()
    {
        TaskItem[] snapshot = [Task("P")];
        TaskItem[] listTasks = [Task("c", parent: "P"), Task("c", parent: "P")]; // same child seen twice

        Assert.Equal(["c"], Ids(TaskService.ForeignDescendants(snapshot, listTasks)));
    }

    [Fact]
    public void IgnoresTopLevelTasksWithNoParent()
    {
        TaskItem[] snapshot = [Task("P")];
        TaskItem[] listTasks = [Task("P"), Task("a"), Task("b")];

        Assert.Empty(TaskService.ForeignDescendants(snapshot, listTasks));
    }

    [Fact]
    public void ParentCycle_DoesNotLoopForever()
    {
        TaskItem[] snapshot = [Task("P")];
        // A pathological cycle a→b→a that never reaches P must terminate and pull in nothing.
        TaskItem[] listTasks = [Task("a", parent: "b"), Task("b", parent: "a")];

        Assert.Empty(TaskService.ForeignDescendants(snapshot, listTasks));
    }

    [Fact]
    public void EmptyInputs_ReturnEmpty()
    {
        Assert.Empty(TaskService.ForeignDescendants([], []));
        Assert.Empty(TaskService.ForeignDescendants([Task("P")], []));
    }
}
