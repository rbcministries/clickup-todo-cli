using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the nested subtasks arrangement (#46): parents keep their order, subtasks nest
/// beneath them, not-in-snapshot parents inject as context headers, and unknown parents fall back flat.
/// </summary>
public sealed class SubtaskArrangerTests
{
    private static TaskItem Task(string id, string? parent = null)
        => new() { Id = id, Name = id, ParentId = parent };

    private static IReadOnlyList<ArrangedRow> Arrange(
        IReadOnlyList<TaskItem> tasks, IReadOnlyDictionary<string, TaskItem>? context = null)
        => SubtaskArranger.Arrange(tasks, context ?? new Dictionary<string, TaskItem>());

    private static readonly Dictionary<string, TaskItem> NoContext = new();

    [Fact]
    public void Arrange_NestsChildImmediatelyUnderParent_WithDepth()
    {
        // Input order interleaves the child away from its parent (as due-date sorting would).
        TaskItem[] tasks = [Task("p"), Task("other"), Task("c", parent: "p")];

        var rows = Arrange(tasks);

        Assert.Equal(["p", "c", "other"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 0], rows.Select(r => r.Depth));
        Assert.All(rows, r => Assert.False(r.IsContextParent));
    }

    [Fact]
    public void Arrange_ParentAfterChildInInput_StillEmitsParentFirst()
    {
        TaskItem[] tasks = [Task("c", parent: "p"), Task("p")];

        var rows = Arrange(tasks);

        Assert.Equal(["p", "c"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1], rows.Select(r => r.Depth));
    }

    [Fact]
    public void Arrange_MultipleSiblings_KeepInputOrderUnderParent()
    {
        TaskItem[] tasks = [Task("p"), Task("c1", parent: "p"), Task("c2", parent: "p")];

        var rows = Arrange(tasks);

        Assert.Equal(["p", "c1", "c2"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 1], rows.Select(r => r.Depth));
    }

    [Fact]
    public void Arrange_DeepNesting_IndentsByDepth()
    {
        TaskItem[] tasks = [Task("a"), Task("b", parent: "a"), Task("c", parent: "b")];

        var rows = Arrange(tasks);

        Assert.Equal(["a", "b", "c"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Depth));
    }

    [Fact]
    public void Arrange_UnknownParent_FallsBackToTopLevelFlat()
    {
        TaskItem[] tasks = [Task("orphan", parent: "missing")];

        var rows = Arrange(tasks);

        var only = Assert.Single(rows);
        Assert.Equal("orphan", only.Task.Id);
        Assert.Equal(0, only.Depth);
        Assert.False(only.IsContextParent);
    }

    [Fact]
    public void Arrange_ContextParent_InjectedOnceAsHeaderWithChildrenBeneath()
    {
        TaskItem[] tasks = [Task("c1", parent: "P"), Task("c2", parent: "P")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["P", "c1", "c2"], rows.Select(r => r.Task.Id));
        Assert.Equal([0, 1, 1], rows.Select(r => r.Depth));
        Assert.True(rows[0].IsContextParent);
        Assert.False(rows[1].IsContextParent);
        Assert.False(rows[2].IsContextParent);
    }

    [Fact]
    public void Arrange_ContextParent_HeaderAppearsAtFirstChildPosition()
    {
        TaskItem[] tasks = [Task("top"), Task("c", parent: "P")];
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["top", "P", "c"], rows.Select(r => r.Task.Id));
    }

    [Fact]
    public void Arrange_ParentPresentInSnapshot_DoesNotUseContextHeader()
    {
        TaskItem[] tasks = [Task("p"), Task("c", parent: "p")];
        // Even if a context entry exists, the in-snapshot parent wins (no duplicate/context row).
        var context = new Dictionary<string, TaskItem> { ["p"] = Task("p") };

        var rows = Arrange(tasks, context);

        Assert.Equal(["p", "c"], rows.Select(r => r.Task.Id));
        Assert.All(rows, r => Assert.False(r.IsContextParent));
    }

    [Fact]
    public void Arrange_NoSubtasks_ReturnsInputOrderUnchanged()
    {
        TaskItem[] tasks = [Task("a"), Task("b"), Task("c")];

        var rows = Arrange(tasks);

        Assert.Equal(["a", "b", "c"], rows.Select(r => r.Task.Id));
        Assert.All(rows, r => Assert.Equal(0, r.Depth));
    }

    [Fact]
    public void Arrange_ParentCycle_TerminatesAndEmitsEachOnce()
    {
        // Pathological: a↔b reference each other. Must not loop forever; each emitted once.
        TaskItem[] tasks = [Task("a", parent: "b"), Task("b", parent: "a")];

        var rows = Arrange(tasks);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows.Select(r => r.Task.Id).OrderBy(x => x));
    }

    [Fact]
    public void Arrange_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Arrange([]));
    }
}
