using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the Assignee field (#68): it's a fetch-layer filter (client-side no-op) that
/// groups/sorts by first assignee, and <see cref="TaskService.ResolveAssigneeIds(ViewSettings, long)"/>
/// derives the server-side assignee set from the view.
/// </summary>
public sealed class AssigneeViewTests
{
    private static TaskItem Task(string id, string name, params (long Id, string Name)[] assignees)
        => new()
        {
            Id = id,
            Name = name,
            Assignees = assignees.Select(a => new TaskAssignee(a.Id, a.Name)).ToList(),
        };

    private static FilterRule Assignee(FilterOp op, string value) => new() { Field = TaskField.Assignee, Op = op, Value = value };

    // ── Client-side filter is a no-op (assignee is enforced at the fetch layer) ──

    [Fact]
    public void Filter_AssigneeRule_DoesNotDropAnyTasks()
    {
        // Even tasks assigned to nobody, or to someone other than "me", survive a client-side
        // "Assignee IS me" — so a Personal-list task owned by a teammate isn't hidden.
        TaskItem[] tasks = [Task("1", "mine", (10, "Me")), Task("2", "theirs", (20, "Them")), Task("3", "nobody")];

        var result = TaskView.Filter(tasks, [Assignee(FilterOp.Is, "me")]);

        Assert.Equal(["1", "2", "3"], result.Select(t => t.Id));
    }

    // ── Group / sort by first assignee ──────────────────────────────────────────

    [Fact]
    public void Group_ByAssignee_BucketsByFirstAssignee_NoneLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", (1, "Ada")),
            Task("2", "b"),                       // unassigned → (none)
            Task("3", "c", (2, "Bo"), (1, "Ada")), // first assignee = Bo
            Task("4", "d", (1, "Ada")),
        ];

        var sorted = TaskView.Sort(tasks, TaskField.Assignee, SortDirection.Ascending);
        var groups = TaskView.Group(sorted, TaskField.Assignee);

        Assert.Equal(["Ada", "Bo", "(none)"], groups.Select(g => g.Label));
        Assert.Equal(["1", "4"], groups[0].Tasks.Select(t => t.Id));
        Assert.Equal(["3"], groups[1].Tasks.Select(t => t.Id));
        Assert.Equal(["2"], groups[2].Tasks.Select(t => t.Id));
    }

    [Fact]
    public void Sort_ByAssignee_AlphabeticalByFirstAssignee_UnassignedLast()
    {
        TaskItem[] tasks = [Task("1", "a", (1, "Bo")), Task("2", "b"), Task("3", "c", (2, "Ada"))];

        var sorted = TaskView.Sort(tasks, TaskField.Assignee, SortDirection.Ascending);

        Assert.Equal(["3", "1", "2"], sorted.Select(t => t.Id)); // Ada, Bo, (none)
    }

    // ── ResolveAssigneeIds ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveAssigneeIds_DefaultMeRule_ResolvesToCurrentUser()
    {
        var view = new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] };

        Assert.Equal([42L], TaskService.ResolveAssigneeIds(view, currentUserId: 42));
    }

    [Fact]
    public void ResolveAssigneeIds_NumericAndMe_Unioned_Distinct()
    {
        var view = new ViewSettings
        {
            Filters =
            [
                Assignee(FilterOp.Is, "me"),
                Assignee(FilterOp.Is, "99"),
                Assignee(FilterOp.Is, "42"), // duplicate of "me" once resolved → collapses
            ],
        };

        var ids = TaskService.ResolveAssigneeIds(view, currentUserId: 42);

        Assert.Equal([42L, 99L], ids);
    }

    [Fact]
    public void ResolveAssigneeIds_NoAssigneeRule_IsEmpty_MeaningEveryone()
        => Assert.Empty(TaskService.ResolveAssigneeIds(new ViewSettings { Filters = [] }, 42));

    [Fact]
    public void ResolveAssigneeIds_UsernameValue_IsSkipped_PendingMembersLookup()
    {
        var view = new ViewSettings { Filters = [Assignee(FilterOp.Is, "teammate@example.com")] };

        Assert.Empty(TaskService.ResolveAssigneeIds(view, 42));
    }

    [Fact]
    public void SameAssigneeSet_IsOrderInsensitive()
    {
        Assert.True(TaskService.SameAssigneeSet([1, 2, 3], [3, 1, 2]));
        Assert.False(TaskService.SameAssigneeSet([1, 2], [1, 2, 3]));
        Assert.False(TaskService.SameAssigneeSet([1, 2], [1, 9]));
    }
}
