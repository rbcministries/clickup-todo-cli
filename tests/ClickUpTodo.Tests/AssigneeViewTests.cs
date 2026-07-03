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
    public void ResolveAssigneeIds_UsernameValue_WithoutMembers_IsSkipped()
    {
        // The 2-arg overload (fast path / members-fetch-failure fallback) can't resolve a name.
        var view = new ViewSettings { Filters = [Assignee(FilterOp.Is, "teammate@example.com")] };

        Assert.Empty(TaskService.ResolveAssigneeIds(view, 42));
    }

    // ── Username/email resolution via workspace members (#73) ──────────────────────

    private static readonly WorkspaceMember[] Members =
    [
        new(10, "ada", "ada@example.com"),
        new(20, "bo", "bo@example.com"),
        new(30, "cy", null),
    ];

    [Fact]
    public void ResolveAssigneeIds_UsernameOrEmail_ResolvesToMemberId()
    {
        Assert.Equal([20L],
            TaskService.ResolveAssigneeIds(new ViewSettings { Filters = [Assignee(FilterOp.Is, "bo")] }, 42, Members));
        Assert.Equal([10L],
            TaskService.ResolveAssigneeIds(new ViewSettings { Filters = [Assignee(FilterOp.Is, "ada@example.com")] }, 42, Members));
    }

    [Fact]
    public void ResolveAssigneeIds_NameMatch_IsCaseInsensitive()
        => Assert.Equal([10L],
            TaskService.ResolveAssigneeIds(new ViewSettings { Filters = [Assignee(FilterOp.Is, "ADA")] }, 42, Members));

    [Fact]
    public void ResolveAssigneeIds_UnknownName_IsSkipped_BestEffort()
        => Assert.Empty(
            TaskService.ResolveAssigneeIds(new ViewSettings { Filters = [Assignee(FilterOp.Is, "nobody")] }, 42, Members));

    [Fact]
    public void ResolveAssigneeIds_MixOfMeNumericAndName_UnionedDistinct()
    {
        var view = new ViewSettings
        {
            Filters =
            [
                Assignee(FilterOp.Is, "me"),   // → 42
                Assignee(FilterOp.Is, "20"),   // numeric id
                Assignee(FilterOp.Is, "bo"),   // name → 20 (collapses with the numeric)
                Assignee(FilterOp.Is, "ada"),  // name → 10
            ],
        };

        Assert.Equal([42L, 20L, 10L], TaskService.ResolveAssigneeIds(view, 42, Members));
    }

    // ── HasUnresolvedAssigneeNames (gates the members round-trip) ──────────────────

    [Fact]
    public void HasUnresolvedAssigneeNames_TrueOnlyForNonMeNonNumericValue()
    {
        Assert.False(TaskService.HasUnresolvedAssigneeNames(new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] }));
        Assert.False(TaskService.HasUnresolvedAssigneeNames(new ViewSettings { Filters = [Assignee(FilterOp.Is, "99")] }));
        Assert.False(TaskService.HasUnresolvedAssigneeNames(new ViewSettings { Filters = [] }));
        Assert.True(TaskService.HasUnresolvedAssigneeNames(new ViewSettings { Filters = [Assignee(FilterOp.Is, "ada@example.com")] }));
    }

    // ── AssigneeRuleValues (drives the F3 reload decision) ─────────────────────────

    [Fact]
    public void AssigneeRuleValues_IsDistinctCaseInsensitiveSetOfIsValues()
    {
        var view = new ViewSettings
        {
            Filters =
            [
                Assignee(FilterOp.Is, "me"),
                Assignee(FilterOp.Is, "ME"),               // dupe (case-insensitive)
                Assignee(FilterOp.Is, "ada"),
                new() { Field = TaskField.Status, Op = FilterOp.IsNot, Value = "done" }, // ignored
            ],
        };

        var values = TaskService.AssigneeRuleValues(view);

        Assert.True(values.SetEquals(["me", "ada"]));
    }

    [Fact]
    public void AssigneeRuleValues_DetectsAddingANameEvenBeforeItResolves()
    {
        // The reason for comparing raw values: adding "bo" changes the fetch, and an id-set comparison
        // (which can't resolve "bo" without members) would wrongly see no change and skip the reload.
        var before = TaskService.AssigneeRuleValues(new ViewSettings { Filters = [Assignee(FilterOp.Is, "me")] });
        var after = TaskService.AssigneeRuleValues(new ViewSettings { Filters = [Assignee(FilterOp.Is, "me"), Assignee(FilterOp.Is, "bo")] });

        Assert.False(before.SetEquals(after));
    }
}
