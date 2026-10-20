using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure helpers of the shared Quick Updates orchestration
/// (<see cref="QuickUpdatesCoordinator"/>) — the parts of the dashboard's inline Quick Updates block that
/// stayed testable when it was lifted into a host-agnostic coordinator so the single-task launch host
/// (#296) could reuse it. The Terminal.Gui glue around them isn't CI-testable (see CONTRIBUTING) and is
/// covered end-to-end by the <c>tui-validate</c> checks.
/// </summary>
public sealed class QuickUpdatesCoordinatorTests
{
    private static TaskItem Task(string id, string? listId = "L1") => new()
    {
        Id = id,
        Name = id,
        ListId = listId,
    };

    private static TaskDetail Detail(params NamedEntity[] lists) => new()
    {
        Id = "abc",
        Name = "abc",
        Lists = lists,
    };

    // ── ColorForStatus (colours the status reflected onto the detail view, #159) ──

    [Fact]
    public void ColorForStatus_FindsTheOptionsColour_CaseInsensitively()
    {
        IReadOnlyList<StatusOption> statuses = [new("to do", "#d3d3d3"), new("In Review", "#a875ff")];

        Assert.Equal("#a875ff", QuickUpdatesCoordinator.ColorForStatus(statuses, "in review"));
    }

    [Fact]
    public void ColorForStatus_IsNull_ForAStatusOutsideTheWorkflow()
    {
        IReadOnlyList<StatusOption> statuses = [new("to do", "#d3d3d3")];

        Assert.Null(QuickUpdatesCoordinator.ColorForStatus(statuses, "shipped"));
    }

    [Fact]
    public void ColorForStatus_IsNull_ForANullStatusOrNullOptions()
    {
        IReadOnlyList<StatusOption> statuses = [new("to do", "#d3d3d3")];

        Assert.Null(QuickUpdatesCoordinator.ColorForStatus(statuses, null));
        Assert.Null(QuickUpdatesCoordinator.ColorForStatus(null, "to do"));
    }

    // ── WithPriority (the priority commit's optimistic record) ────────────────────

    [Fact]
    public void WithPriority_CarriesTheCanonicalNameAndColourForTheLevel()
    {
        var result = QuickUpdatesCoordinator.WithPriority(Task("abc"), 1);

        Assert.Equal(1, result.PriorityLevel);
        Assert.Equal("Urgent", result.PriorityName);
        Assert.Equal(ClickUpPriority.ColorFromLevel(1), result.PriorityColor);
    }

    [Fact]
    public void WithPriority_ClearsAllThreeFields_ForTheNoPriorityCommit()
    {
        var prioritized = QuickUpdatesCoordinator.WithPriority(Task("abc"), 2);

        var cleared = QuickUpdatesCoordinator.WithPriority(prioritized, null);

        Assert.Null(cleared.PriorityLevel);
        Assert.Null(cleared.PriorityName);
        Assert.Null(cleared.PriorityColor);
    }

    [Fact]
    public void WithPriority_LeavesEveryOtherFieldUntouched()
    {
        var task = Task("abc") with { StatusName = "to do", Name = "Ship it" };

        var result = QuickUpdatesCoordinator.WithPriority(task, 4);

        Assert.Equal(task with { PriorityLevel = 4, PriorityName = "Low", PriorityColor = result.PriorityColor },
            result);
    }

    // ── AdditionalLists (the List pane's seeded "Tasks in Multiple Lists" rows, #242) ──

    [Fact]
    public void AdditionalLists_ExcludesTheHomeList_WhichThePaneSeedsSeparately()
    {
        var detail = Detail(new NamedEntity("L1", "Home"), new NamedEntity("L2", "Elsewhere"));

        var result = QuickUpdatesCoordinator.AdditionalLists(Task("abc", listId: "L1"), detail);

        Assert.Equal(["L2"], result.Select(l => l.Id));
    }

    [Fact]
    public void AdditionalLists_IsEmpty_WhenNoDetailWasLoaded()
    {
        // A root- or tree-row launch has no loaded detail for the task; the coordinator enriches the pane
        // from a background fetch instead, so the seed starts empty rather than guessing.
        Assert.Empty(QuickUpdatesCoordinator.AdditionalLists(Task("abc"), null));
    }

    [Fact]
    public void AdditionalLists_KeepsEveryMembership_WhenTheTaskHasNoHomeList()
    {
        var detail = Detail(new NamedEntity("L1", "One"), new NamedEntity("L2", "Two"));

        var result = QuickUpdatesCoordinator.AdditionalLists(Task("abc", listId: null), detail);

        Assert.Equal(["L1", "L2"], result.Select(l => l.Id));
    }
}
