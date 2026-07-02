using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="FocusSectionLayout.Build"/> — how the pinned "Current Focus" section
/// nests a pinned parent's in-snapshot subtasks beneath it when the F4 subtasks view is on (#75),
/// and which non-pinned subtasks it pulls out of the to-do set to avoid duplication.
/// </summary>
public sealed class FocusSectionLayoutTests
{
    private static TaskItem Task(string id, string? parent = null)
        => new() { Id = id, Name = id, ParentId = parent };

    private static IReadOnlySet<string> Pins(params string[] ids)
        => new HashSet<string>(ids, StringComparer.Ordinal);

    private static FocusSection Build(IReadOnlyList<TaskItem> all, IReadOnlySet<string> pins, bool nest)
        => FocusSectionLayout.Build(all, pins, nest, sortField: null, SortDirection.Ascending);

    private static IEnumerable<string> Ids(FocusSection s) => s.Rows.Select(r => r.Task.Id);
    private static IEnumerable<int> Depths(FocusSection s) => s.Rows.Select(r => r.Depth);

    [Fact]
    public void NestOn_PinnedParentWithSubtask_NestsSubtaskAtDepth1_AndMarksItPulled()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p"), Task("o")];

        var s = Build(all, Pins("p"), nest: true);

        Assert.Equal(["p", "c"], Ids(s));       // "o" (unrelated, unpinned) is not in Focus
        Assert.Equal([0, 1], Depths(s));
        Assert.All(s.Rows, r => Assert.False(r.IsContextParent));
        Assert.Equal(Pins("c"), s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_Grandchildren_NestRecursively()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p"), Task("g", parent: "c")];

        var s = Build(all, Pins("p"), nest: true);

        Assert.Equal(["p", "c", "g"], Ids(s));
        Assert.Equal([0, 1, 2], Depths(s));
        Assert.Equal(Pins("c", "g"), s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOff_PinnedShownFlat_NothingPulled()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p")];

        var s = Build(all, Pins("p"), nest: false);

        // Parity with pre-#75: only the pin shows, flat; its subtask stays in the to-do set (and is
        // dropped there by the subtasks-hidden filter), so nothing is pulled into Focus.
        Assert.Equal(["p"], Ids(s));
        Assert.Equal([0], Depths(s));
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOff_TwoPinsThatAreParentAndChild_StayFlat()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p")];

        var s = Build(all, Pins("p", "c"), nest: false);

        // With the subtasks view off the Focus section never indents — both pins render flat.
        Assert.All(s.Rows, r => Assert.Equal(0, r.Depth));
        Assert.Equal(["c", "p"], Ids(s)); // default sort (name) among top-level pins
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_PinnedSubtaskWhoseParentIsNotPinned_StaysFlat_ParentNotDraggedIn()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p")];

        var s = Build(all, Pins("c"), nest: true);

        // Decision #4: a pinned subtask whose parent isn't pinned stays flat in Focus; we don't pull
        // its (unpinned) parent in, and nothing is marked pulled (the parent stays in the to-do set).
        Assert.Equal(["c"], Ids(s));
        Assert.Equal([0], Depths(s));
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_ParentAndChildBothPinned_ChildAppearsOnceNested_NotMarkedPulled()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p")];

        var s = Build(all, Pins("p", "c"), nest: true);

        Assert.Equal(["p", "c"], Ids(s));       // exactly once
        Assert.Equal([0, 1], Depths(s));
        // c is itself a pin, not a pulled-in subtask, so it must NOT be excluded from the to-do set as
        // a "nested subtask" — it's already excluded by being pinned.
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_OnlyDescendantsOfPinsArePulled_UnrelatedSubtreesStayOut()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p"), Task("x"), Task("y", parent: "x")];

        var s = Build(all, Pins("p"), nest: true);

        Assert.Equal(["p", "c"], Ids(s));                 // x / y subtree is untouched
        Assert.Equal(Pins("c"), s.NestedSubtaskIds);      // only p's descendant is pulled
    }

    [Fact]
    public void NoPins_EmptySection()
    {
        TaskItem[] all = [Task("a"), Task("b", parent: "a")];

        var s = Build(all, Pins(), nest: true);

        Assert.Empty(s.Rows);
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_TopLevelPinsFollowSortOrder()
    {
        TaskItem[] all = [Task("b"), Task("a")];

        var s = Build(all, Pins("a", "b"), nest: true);

        Assert.Equal(["a", "b"], Ids(s)); // default (name-ascending) order among anchors
    }
}
