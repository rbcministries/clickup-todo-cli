using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the not-mine / context classification that <see cref="TodoApp.UpdateTaskRow"/> uses to
/// keep an in-place row's trailing marker in sync with the full render path (#264). The mapping
/// from these flags to the actual marker text (and their precedence) is covered by
/// <see cref="TaskRowFormatterTests"/>.
/// </summary>
public sealed class ClassifyRowMarkerTests
{
    private static readonly IReadOnlyDictionary<string, TaskItem> NoParents =
        new Dictionary<string, TaskItem>();
    private static readonly IReadOnlyDictionary<string, TaskItem> NoForeign =
        new Dictionary<string, TaskItem>();

    private static TaskItem Task(string id, params long[] assigneeIds) => new()
    {
        Id = id,
        Name = $"Task {id}",
        Assignees = assigneeIds.Select(a => new TaskAssignee(a, $"User {a}")).ToList(),
    };

    private static Dictionary<string, TaskItem> Set(TaskItem task) =>
        new() { [task.Id] = task };

    [Fact]
    public void PlainSnapshotTask_HasNoMarker()
    {
        var task = Task("t1", 42);

        var (isContextParent, isForeignSubtask, isUnassignedSubtask) =
            TodoApp.ClassifyRowMarker(task, NoParents, NoForeign);

        Assert.False(isContextParent);
        Assert.False(isForeignSubtask);
        Assert.False(isUnassignedSubtask);
    }

    [Fact]
    public void ContextParent_IsClassifiedAsContextParentOnly()
    {
        var task = Task("p1", 7);

        var (isContextParent, isForeignSubtask, isUnassignedSubtask) =
            TodoApp.ClassifyRowMarker(task, Set(task), NoForeign);

        Assert.True(isContextParent);
        Assert.False(isForeignSubtask);
        Assert.False(isUnassignedSubtask);
    }

    [Fact]
    public void ForeignSubtaskAssignedToOthers_IsClassifiedAsForeignOnly()
    {
        var task = Task("s1", 99); // assigned, but to someone else

        var (isContextParent, isForeignSubtask, isUnassignedSubtask) =
            TodoApp.ClassifyRowMarker(task, NoParents, Set(task));

        Assert.False(isContextParent);
        Assert.True(isForeignSubtask);
        Assert.False(isUnassignedSubtask);
    }

    [Fact]
    public void UnassignedForeignSubtask_IsClassifiedAsUnassignedOnly()
    {
        var task = Task("s2"); // pulled in, no assignees

        var (isContextParent, isForeignSubtask, isUnassignedSubtask) =
            TodoApp.ClassifyRowMarker(task, NoParents, Set(task));

        Assert.False(isContextParent);
        Assert.False(isForeignSubtask);
        Assert.True(isUnassignedSubtask);
    }

    [Fact]
    public void ForeignAndUnassignedAreMutuallyExclusive_ByAssigneePresence()
    {
        // The same id in the visible-foreign set is classified foreign-vs-unassigned purely by whether
        // it has an assignee, mirroring SubtaskVisibility.IsUnassigned — the two are never both true, so
        // TaskRowFormatter's precedence ladder only ever picks one marker.
        var assigned = Task("s3", 5);
        var unassigned = Task("s3");

        var assignedResult = TodoApp.ClassifyRowMarker(assigned, NoParents, Set(assigned));
        var unassignedResult = TodoApp.ClassifyRowMarker(unassigned, NoParents, Set(unassigned));

        Assert.True(assignedResult.IsForeignSubtask);
        Assert.False(assignedResult.IsUnassignedSubtask);
        Assert.False(unassignedResult.IsForeignSubtask);
        Assert.True(unassignedResult.IsUnassignedSubtask);
    }
}
