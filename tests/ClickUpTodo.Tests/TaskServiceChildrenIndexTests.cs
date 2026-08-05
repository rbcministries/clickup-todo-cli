using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskService.BuildChildrenIndex"/> — the pure per-parent children lookup the
/// TUI hands to <see cref="TaskService.GetTaskTreeAsync"/> as the #450 descendant seed. Pins the
/// completeness contract at the boundary: a present key returns its (complete) children set, a present
/// <b>empty</b> list is a trusted "no children" (distinct from a miss), and an <b>absent</b> key returns
/// <c>null</c> so the descendant BFS falls back to a fetch.
/// </summary>
public sealed class TaskServiceChildrenIndexTests
{
    private static TaskItem Item(string id, string? parent = null) => new() { Id = id, Name = id, ParentId = parent };

    private static IReadOnlyDictionary<string, IReadOnlyList<TaskItem>> Map(
        params (string Parent, TaskItem[] Children)[] entries)
        => entries.ToDictionary(
            e => e.Parent, e => (IReadOnlyList<TaskItem>)e.Children, StringComparer.Ordinal);

    [Fact]
    public void PresentKey_ReturnsItsChildren()
    {
        var index = TaskService.BuildChildrenIndex(Map(("p", [Item("c1", "p"), Item("c2", "p")])));

        var children = index("p");
        Assert.NotNull(children);
        Assert.Equal(["c1", "c2"], children!.Select(c => c.Id));
    }

    [Fact]
    public void AbsentKey_ReturnsNull_SoTheBfsFetches()
    {
        var index = TaskService.BuildChildrenIndex(Map(("p", [Item("c", "p")])));

        Assert.Null(index("other"));
    }

    [Fact]
    public void PresentEmptyList_IsTrustedNoChildren_NotAMiss()
    {
        // A parent known to have no children returns an empty list (skip the fetch, add nothing), which the
        // caller must treat differently from an absent key (null → fetch). The two are distinguishable here.
        var index = TaskService.BuildChildrenIndex(Map(("p", [])));

        var children = index("p");
        Assert.NotNull(children);      // present, not a miss…
        Assert.Empty(children!);       // …and vouched-for empty
    }

    [Fact]
    public void EmptyMap_IsAnAllMissLookup()
    {
        var index = TaskService.BuildChildrenIndex(Map());

        Assert.Null(index("anything"));
    }

    [Fact]
    public void FrozenAtBuild_LaterMapMutationDoesNotLeak()
    {
        // Snapshotting into a fresh dictionary (like BuildSnapshotLookup) matters because the tree load runs
        // off the UI thread: a mutation of the source after the build must not change what the delegate sees.
        var source = new Dictionary<string, IReadOnlyList<TaskItem>>(StringComparer.Ordinal)
        {
            ["p"] = new[] { Item("c", "p") },
        };
        var index = TaskService.BuildChildrenIndex(source);

        source["q"] = new[] { Item("d", "q") }; // added after the build
        source.Remove("p");

        Assert.NotNull(index("p"));  // still resolves the frozen entry
        Assert.Null(index("q"));     // the post-build addition is not visible
    }
}
