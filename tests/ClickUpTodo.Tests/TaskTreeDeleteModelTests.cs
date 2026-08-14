using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for <see cref="TaskTreeDeleteModel"/> (F, #594) — the pure classification + subtree-pruning behind
/// the Task Tree tab's contextual <c>Delete</c>. Rows are built through <see cref="TaskTreeArranger"/> (the
/// same producer the tab uses) so the ancestors → current → descendants ordering <c>Resolve</c> reads is the
/// real one.
/// </summary>
public sealed class TaskTreeDeleteModelTests
{
    private static TaskItem Item(string id, string? parent = null)
        => new() { Id = id, Name = $"name-{id}", ParentId = parent };

    // gp -> p -> current(T) -> {c1 -> gc, c2}: ancestors [gp, p], current T, descendants [c1, gc, c2].
    // Arranged order: gp(0) p(1) T(2,current) c1(3) gc(4) c2(3).
    private static IReadOnlyList<TaskTreeRow> FullTree()
    {
        var gp = Item("gp");
        var p = Item("p", "gp");
        var t = Item("T", "p");
        var c1 = Item("c1", "T");
        var c2 = Item("c2", "T");
        var gc = Item("gc", "c1");
        return TaskTreeArranger.Build("T", [gp, p], t, [c1, c2, gc]);
    }

    private static int IndexOf(IReadOnlyList<TaskTreeRow> rows, string id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Task.Id == id)
                return i;
        throw new InvalidOperationException($"no row {id}");
    }

    [Fact]
    public void Resolve_CurrentRow_IsCurrentKind()
    {
        var rows = FullTree();

        var target = TaskTreeDeleteModel.Resolve(rows, IndexOf(rows, "T"));

        Assert.NotNull(target);
        Assert.Equal("T", target!.Value.TaskId);
        Assert.Equal("name-T", target.Value.Name);
        Assert.Equal(TaskTreeDeleteKind.Current, target.Value.Kind);
    }

    [Theory]
    [InlineData("c1")]
    [InlineData("gc")]
    [InlineData("c2")]
    public void Resolve_DescendantRow_IsSubtaskKind(string id)
    {
        var rows = FullTree();

        var target = TaskTreeDeleteModel.Resolve(rows, IndexOf(rows, id));

        Assert.NotNull(target);
        Assert.Equal(id, target!.Value.TaskId);
        Assert.Equal(TaskTreeDeleteKind.Subtask, target.Value.Kind);
    }

    [Theory]
    [InlineData("gp")]
    [InlineData("p")]
    public void Resolve_AncestorRow_IsInert(string id)
    {
        var rows = FullTree();

        Assert.Null(TaskTreeDeleteModel.Resolve(rows, IndexOf(rows, id)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void Resolve_OutOfRangeIndex_IsInert(int index)
        => Assert.Null(TaskTreeDeleteModel.Resolve(FullTree(), index));

    [Fact]
    public void Resolve_EmptyTree_IsInert()
        => Assert.Null(TaskTreeDeleteModel.Resolve([], 0));

    [Fact]
    public void Resolve_NoCurrentRow_IsInert()
    {
        // Defensive: a tree with no IsCurrent row (shouldn't happen once loaded) resolves to nothing.
        IReadOnlyList<TaskTreeRow> rows =
        [
            new(Item("a"), 0, false),
            new(Item("b"), 1, false),
        ];

        Assert.Null(TaskTreeDeleteModel.Resolve(rows, 0));
        Assert.Null(TaskTreeDeleteModel.Resolve(rows, 1));
    }

    [Fact]
    public void Resolve_LoneCurrentTask_IsCurrentKind()
    {
        var rows = TaskTreeArranger.Build("T", [], Item("T"), []);

        var target = TaskTreeDeleteModel.Resolve(rows, 0);

        Assert.Equal(TaskTreeDeleteKind.Current, target!.Value.Kind);
    }

    [Fact]
    public void RemoveSubtree_DropsNodeAndItsDescendants_KeepsSiblingsAndAncestors()
    {
        var rows = FullTree();

        var result = TaskTreeDeleteModel.RemoveSubtree(rows, "c1");

        // c1 and its child gc are gone; the current task, its ancestry, and the c2 sibling remain in order.
        Assert.Equal(["gp", "p", "T", "c2"], result.Select(r => r.Task.Id));
    }

    [Fact]
    public void RemoveSubtree_LeafSubtask_DropsOnlyThatRow()
    {
        var rows = FullTree();

        var result = TaskTreeDeleteModel.RemoveSubtree(rows, "c2");

        Assert.Equal(["gp", "p", "T", "c1", "gc"], result.Select(r => r.Task.Id));
    }

    [Fact]
    public void RemoveSubtree_MissingId_ReturnsRowsUnchanged()
    {
        var rows = FullTree();

        var result = TaskTreeDeleteModel.RemoveSubtree(rows, "nope");

        Assert.Same(rows, result);
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(2, 3, 2)]
    [InlineData(5, 3, 2)] // removed past the new end → clamp to the last row.
    [InlineData(1, 0, -1)] // nothing remains.
    public void SelectAfterDelete_ClampsIntoTheNewTree(int removedIndex, int newCount, int expected)
        => Assert.Equal(expected, TaskTreeDeleteModel.SelectAfterDelete(removedIndex, newCount));
}
