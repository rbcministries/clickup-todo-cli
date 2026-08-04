using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.BuildSnapshotLookup"/> — the pure id→task map the TUI hands to
/// <see cref="TaskService.GetTaskTreeAsync"/> as the #419 ancestry seed. Pins its precedence
/// (primary wins, like <see cref="TaskService.FindById"/>), its <c>rows</c> fallback for context rows
/// that live outside the snapshot (foreign subtasks #70/#179, context parents #46), that null (header)
/// row entries are skipped, and that a miss returns null (the tree then fetches that level).
/// </summary>
public sealed class TaskServiceSnapshotLookupTests
{
    private static TaskItem Item(string id, string? name = null) => new() { Id = id, Name = name ?? id };

    [Fact]
    public void ResolvesFromPrimary()
    {
        var lookup = TaskService.BuildSnapshotLookup([Item("a"), Item("b")], []);

        Assert.Equal("a", lookup("a")?.Id);
        Assert.Equal("b", lookup("b")?.Id);
    }

    [Fact]
    public void FallsBackToRows_ForContextRowsOutsidePrimary()
    {
        // "ctx" lives only in the visible rows (a foreign subtask / context parent), not the snapshot.
        var lookup = TaskService.BuildSnapshotLookup([Item("a")], [Item("ctx"), null]);

        Assert.Equal("ctx", lookup("ctx")?.Id);
        Assert.Equal("a", lookup("a")?.Id);
    }

    [Fact]
    public void PrimaryWins_OnIdCollision()
    {
        // Same id in both sides — primary carries the canonical (optimistic) record, so it must win.
        var lookup = TaskService.BuildSnapshotLookup(
            [Item("x", "from-primary")], [Item("x", "from-rows")]);

        Assert.Equal("from-primary", lookup("x")?.Name);
    }

    [Fact]
    public void SkipsNullRowEntries()
    {
        var lookup = TaskService.BuildSnapshotLookup([], [null, Item("a"), null]);

        Assert.Equal("a", lookup("a")?.Id);
    }

    [Fact]
    public void Miss_ReturnsNull()
    {
        var lookup = TaskService.BuildSnapshotLookup([Item("a")], []);

        Assert.Null(lookup("nope"));
    }
}
