using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for <see cref="TaskTreeArranger"/> (#291) — the pure assembly of a task's ancestry chain + the
/// task itself + its descendants into an ordered, indented <see cref="TaskTreeRow"/> list for the Task
/// Tree tab. The nesting mechanics are already covered by <see cref="SubtaskArrangerTests"/>; these pin
/// the tab-specific behaviour: ancestry indents above the current task, the current row is flagged, and
/// duplicates/cycles are arranged once.
/// </summary>
public sealed class TaskTreeArrangerTests
{
    private static TaskItem Item(string id, string? parent = null)
        => new() { Id = id, Name = id, ParentId = parent };

    private static (string Id, int Depth, bool Current) Row(TaskTreeRow r) => (r.Task.Id, r.Depth, r.IsCurrent);

    [Fact]
    public void LoneTask_NoAncestryNoChildren_SingleCurrentRowAtDepthZero()
    {
        var current = Item("T");

        var rows = TaskTreeArranger.Build("T", [], current, []);

        Assert.Equal([("T", 0, true)], rows.Select(Row));
    }

    [Fact]
    public void AncestorsIndentAboveCurrent_TopMostAtDepthZero()
    {
        // gp -> p -> current (ancestorsTopDown is [gp, p]).
        var gp = Item("gp");
        var p = Item("p", parent: "gp");
        var current = Item("T", parent: "p");

        var rows = TaskTreeArranger.Build("T", [gp, p], current, []);

        Assert.Equal(
            [("gp", 0, false), ("p", 1, false), ("T", 2, true)],
            rows.Select(Row));
    }

    [Fact]
    public void DescendantsNestUnderCurrent_RecursivelyIndented()
    {
        var current = Item("T");
        var child = Item("c", parent: "T");
        var grandchild = Item("gc", parent: "c");

        var rows = TaskTreeArranger.Build("T", [], current, [child, grandchild]);

        Assert.Equal(
            [("T", 0, true), ("c", 1, false), ("gc", 2, false)],
            rows.Select(Row));
    }

    [Fact]
    public void FullTree_AncestryThenCurrentThenDescendants()
    {
        var gp = Item("gp");
        var p = Item("p", parent: "gp");
        var current = Item("T", parent: "p");
        var c1 = Item("c1", parent: "T");
        var c2 = Item("c2", parent: "T");
        var gc = Item("gc", parent: "c1");

        var rows = TaskTreeArranger.Build("T", [gp, p], current, [c1, c2, gc]);

        Assert.Equal(
            [("gp", 0, false), ("p", 1, false), ("T", 2, true), ("c1", 3, false), ("gc", 4, false), ("c2", 3, false)],
            rows.Select(Row));
        // Exactly one row is flagged current.
        Assert.Single(rows, r => r.IsCurrent);
    }

    [Fact]
    public void DescendantEchoingCurrent_IsArrangedOnce()
    {
        // A subtask fetch that echoed the current task back must not double it.
        var current = Item("T");
        var child = Item("c", parent: "T");

        var rows = TaskTreeArranger.Build("T", [], current, [Item("T"), child]);

        Assert.Equal([("T", 0, true), ("c", 1, false)], rows.Select(Row));
    }

    [Fact]
    public void CyclicAncestry_DoesNotDuplicateOrLoop()
    {
        // Pathological: an "ancestor" whose id repeats. De-dup keeps the first, and SubtaskArranger's
        // emitted-guard keeps it from recursing forever.
        var a = Item("a");
        var current = Item("T", parent: "a");

        var rows = TaskTreeArranger.Build("T", [a, Item("a")], current, []);

        Assert.Equal([("a", 0, false), ("T", 1, true)], rows.Select(Row));
    }

    [Fact]
    public void CurrentFlag_MatchesIdNotPosition()
    {
        var p = Item("p");
        var current = Item("T", parent: "p");

        var rows = TaskTreeArranger.Build("T", [p], current, []);

        Assert.False(rows.Single(r => r.Task.Id == "p").IsCurrent);
        Assert.True(rows.Single(r => r.Task.Id == "T").IsCurrent);
    }
}
