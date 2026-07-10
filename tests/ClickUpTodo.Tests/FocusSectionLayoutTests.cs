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

    private static FocusSection Build(IReadOnlyList<TaskItem> all, IReadOnlySet<string> pins, bool nest,
        IReadOnlySet<string>? expanded = null, IReadOnlyList<TaskItem>? foreign = null)
        => FocusSectionLayout.Build(all, pins, nest, sortField: null, SortDirection.Ascending, expanded, foreign);

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

    [Fact]
    public void NestOn_PinnedParentCollapsed_HidesSubtask_ButStillPulledFromTodo()
    {
        // Per-parent folding (#76) applies in Focus too: a collapsed pinned parent hides its subtask in
        // the section, yet the subtask stays pulled (excluded from the to-do set) so it never reappears
        // un-nested elsewhere — collapsed means hidden, not relocated.
        TaskItem[] all = [Task("p"), Task("c", parent: "p"), Task("o")];

        var s = Build(all, Pins("p"), nest: true, expanded: new HashSet<string>(/* p collapsed */));

        Assert.Equal(["p"], Ids(s));                 // c is hidden under the collapsed pin
        Assert.Equal(FoldState.Collapsed, s.Rows[0].Fold);
        Assert.Equal(Pins("c"), s.NestedSubtaskIds); // still pulled out of the to-do set
    }

    [Fact]
    public void NestOn_PinnedParentExpanded_ShowsSubtaskWithExpandedMarker()
    {
        TaskItem[] all = [Task("p"), Task("c", parent: "p")];

        var s = Build(all, Pins("p"), nest: true, expanded: new HashSet<string>(["p"]));

        Assert.Equal(["p", "c"], Ids(s));
        Assert.Equal(FoldState.Expanded, s.Rows[0].Fold);
    }

    // ── Foreign (teammate-owned) subtasks of a pinned parent (#70 → #85) ────────
    // These live outside the `all` snapshot; passing them in must nest a pin's foreign descendants under
    // it in Focus and return them in NestedSubtaskIds (the caller drops exactly those from the to-do set).

    [Fact]
    public void NestOn_ForeignChildOfPinnedParent_NestsAtDepth1_AndIsPulled()
    {
        TaskItem[] all = [Task("p")];                       // only the parent is mine / in-snapshot
        TaskItem[] foreign = [Task("fc", parent: "p")];     // teammate-owned child, not in the snapshot

        var s = Build(all, Pins("p"), nest: true, foreign: foreign);

        Assert.Equal(["p", "fc"], Ids(s));
        Assert.Equal([0, 1], Depths(s));
        Assert.Equal(Pins("fc"), s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_ForeignChildOfNonPinnedParent_IsNotPulledIntoFocus()
    {
        TaskItem[] all = [Task("p"), Task("q")];            // p pinned, q not
        TaskItem[] foreign = [Task("fq", parent: "q")];     // foreign child of the *unpinned* q

        var s = Build(all, Pins("p"), nest: true, foreign: foreign);

        // fq belongs in the to-do section (under q), so Focus neither shows nor claims it.
        Assert.Equal(["p"], Ids(s));
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_ForeignChildOfNonPinnedInSnapshotParentUnderAPin_IsPulled()
    {
        // The bug the old ancestor-only helper missed: m is in-snapshot but *not* pinned, yet it descends
        // from pinned p (so #75 nests m under p in Focus). A foreign child fm of m must follow m into
        // Focus — not stay in the to-do set where its parent no longer lives (which would render detached).
        TaskItem[] all = [Task("p"), Task("m", parent: "p")];
        TaskItem[] foreign = [Task("fm", parent: "m")];

        var s = Build(all, Pins("p"), nest: true, foreign: foreign);

        Assert.Equal(["p", "m", "fm"], Ids(s));
        Assert.Equal([0, 1, 2], Depths(s));
        Assert.Equal(Pins("m", "fm"), s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_ForeignGrandchildThroughForeignIntermediate_NestsUnderPin()
    {
        // Both the child and grandchild are teammate-owned (foreign); the chain reaches pinned p.
        TaskItem[] all = [Task("p")];
        TaskItem[] foreign = [Task("fc", parent: "p"), Task("fg", parent: "fc")];

        var s = Build(all, Pins("p"), nest: true, foreign: foreign);

        Assert.Equal(["p", "fc", "fg"], Ids(s));
        Assert.Equal([0, 1, 2], Depths(s));
        Assert.Equal(Pins("fc", "fg"), s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_CollapsedPinnedParent_HidesForeignChild_ButStillPullsIt()
    {
        // Collapsed ⇒ hidden, not relocated: the foreign child is absent from the rendered rows yet still
        // pulled (excluded from to-do), matching the in-snapshot behaviour.
        TaskItem[] all = [Task("p")];
        TaskItem[] foreign = [Task("fc", parent: "p")];

        var s = Build(all, Pins("p"), nest: true, expanded: new HashSet<string>(/* p collapsed */),
            foreign: foreign);

        Assert.Equal(["p"], Ids(s));
        Assert.Equal(Pins("fc"), s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOff_ForeignSubtasksIgnored()
    {
        TaskItem[] all = [Task("p")];
        TaskItem[] foreign = [Task("fc", parent: "p")];

        var s = Build(all, Pins("p"), nest: false, foreign: foreign);

        Assert.Equal(["p"], Ids(s));
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void NestOn_MixedForeign_OnlyThePinnedParentsChildIsPulled()
    {
        // One Build call with a foreign set that straddles the boundary: fp is under pinned p, fq under
        // unpinned q. NestedSubtaskIds must be *exactly* {fp} — proving the pulled set is the precise
        // subset the caller then excludes from the to-do list (complementarity within a single call).
        TaskItem[] all = [Task("p"), Task("q")];
        TaskItem[] foreign = [Task("fp", parent: "p"), Task("fq", parent: "q")];

        var s = Build(all, Pins("p"), nest: true, foreign: foreign);

        Assert.Equal(["p", "fp"], Ids(s)); // q (unpinned) and its foreign child fq stay out of Focus
        Assert.Equal([0, 1], Depths(s));
        Assert.Equal(Pins("fp"), s.NestedSubtaskIds);
    }

    // ── Show Completed toggle (F12, #178) ────────────────────────────────────

    private static TaskItem Sub(string id, string parent, string? statusType = null)
        => new() { Id = id, Name = id, ParentId = parent, StatusType = statusType };

    [Fact]
    public void ShowCompletedOff_CompletedInSnapshotSubtask_NotNestedUnderPin()
    {
        // The leak this guards: a closed-type subtask (kept server-side for chain integrity) that is
        // in-snapshot and whose parent is pinned must not nest under the pin when Show Completed is off.
        TaskItem[] all = [Task("p"), Sub("c", parent: "p", statusType: "closed"), Sub("o", parent: "p", statusType: "open")];

        var s = FocusSectionLayout.Build(all, Pins("p"), nest: true, sortField: null, SortDirection.Ascending, includeCompleted: false);

        Assert.Equal(["p", "o"], Ids(s));           // completed "c" dropped; open "o" still nests
        Assert.Equal(Pins("o"), s.NestedSubtaskIds);
    }

    [Fact]
    public void ShowCompletedOn_CompletedInSnapshotSubtask_NestsUnderPin()
    {
        TaskItem[] all = [Task("p"), Sub("c", parent: "p", statusType: "closed")];

        var s = FocusSectionLayout.Build(all, Pins("p"), nest: true, sortField: null, SortDirection.Ascending, includeCompleted: true);

        Assert.Equal(["p", "c"], Ids(s));
        Assert.Equal(Pins("c"), s.NestedSubtaskIds);
    }

    [Fact]
    public void ShowCompletedOff_CompletedForeignSubtask_NotNestedUnderPin()
    {
        TaskItem[] all = [Task("p")];
        TaskItem[] foreign = [Sub("fc", parent: "p", statusType: "closed")];

        var s = FocusSectionLayout.Build(all, Pins("p"), nest: true, sortField: null, SortDirection.Ascending, foreignSubtasks: foreign, includeCompleted: false);

        Assert.Equal(["p"], Ids(s));
        Assert.Empty(s.NestedSubtaskIds);
    }

    [Fact]
    public void ShowCompletedOff_PinnedCompletedTask_StillShows()
    {
        // Explicit pins don't vanish: a pinned task that is itself completed stays visible even when off.
        TaskItem[] all = [new() { Id = "p", Name = "p", StatusType = "closed" }];

        var s = FocusSectionLayout.Build(all, Pins("p"), nest: true, sortField: null, SortDirection.Ascending, includeCompleted: false);

        Assert.Equal(["p"], Ids(s));
    }
}
