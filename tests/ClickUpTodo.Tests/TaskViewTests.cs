using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure F3 filter/sort/group engine (issue #19): filtering (per operator,
/// categorical vs numeric, null handling, ANDing), sorting (per field, direction, nulls-last,
/// default order), and grouping (categorical, by date-day, ordering of the missing bucket).
/// </summary>
public sealed class TaskViewTests
{
    private static long Ms(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeMilliseconds();

    private static TaskItem Task(
        string id, string name, string? status = null, string? list = null, long? due = null, long? updated = null)
        => new() { Id = id, Name = name, StatusName = status, ListName = list, DueDateMs = due, UpdatedMs = updated };

    private static FilterRule Rule(TaskField field, FilterOp op, string value) => new() { Field = field, Op = op, Value = value };

    // ── Filter: categorical ──────────────────────────────────────────────────

    [Fact]
    public void Filter_CategoricalIs_KeepsOnlyMatches_CaseInsensitive()
    {
        TaskItem[] tasks = [Task("1", "a", status: "Done"), Task("2", "b", status: "to do"), Task("3", "c", status: "done")];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Status, FilterOp.Is, "done")]);

        Assert.Equal(["1", "3"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_CategoricalIsNot_ExcludesMatches_AndKeepsNulls()
    {
        TaskItem[] tasks = [Task("1", "a", status: "Done"), Task("2", "b", status: null), Task("3", "c", status: "to do")];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Status, FilterOp.IsNot, "done")]);

        Assert.Equal(["2", "3"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_OrderingOperatorOnCategorical_IsIgnored()
    {
        TaskItem[] tasks = [Task("1", "a", status: "Done"), Task("2", "b", status: "to do")];

        // ">" is invalid for a categorical field — the rule should be a no-op, not hide everything.
        var result = TaskView.Filter(tasks, [Rule(TaskField.Status, FilterOp.GreaterThan, "done")]);

        Assert.Equal(2, result.Count);
    }

    // ── Filter: numeric/date ─────────────────────────────────────────────────

    [Fact]
    public void Filter_NumericGreaterOrEqual_UsesEpochMs()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", due: Ms("2026-07-01T00:00:00Z")),
            Task("2", "b", due: Ms("2026-07-10T00:00:00Z")),
            Task("3", "c", due: null),
        ];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Due, FilterOp.GreaterOrEqual, "2026-07-05")]);

        Assert.Equal(["2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_NumericLessThan_ExcludesNullValues()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", updated: Ms("2026-06-01T00:00:00Z")),
            Task("2", "b", updated: null),
        ];

        var result = TaskView.Filter(tasks, [Rule(TaskField.LastActivity, FilterOp.LessThan, "2026-06-15")]);

        Assert.Equal(["1"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_NumericIs_MatchesExactMs_NullExcluded()
    {
        var target = Ms("2026-07-01T00:00:00Z");
        TaskItem[] tasks = [Task("1", "a", due: target), Task("2", "b", due: target + 1), Task("3", "c", due: null)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Due, FilterOp.Is, "2026-07-01")]);

        Assert.Equal(["1"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_NumericIsNot_KeepsNullValues()
    {
        var target = Ms("2026-07-01T00:00:00Z");
        TaskItem[] tasks = [Task("1", "a", due: target), Task("2", "b", due: target + 1), Task("3", "c", due: null)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Due, FilterOp.IsNot, "2026-07-01")]);

        // The exact-match task is excluded; a different date and an undated task both survive.
        Assert.Equal(["2", "3"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_NumericRule_AcceptsRawEpochMs()
    {
        var cutoff = Ms("2026-07-05T00:00:00Z");
        TaskItem[] tasks = [Task("1", "a", due: cutoff - 1), Task("2", "b", due: cutoff + 1)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Due, FilterOp.GreaterThan, cutoff.ToString())]);

        Assert.Equal(["2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_UnparseableNumericValue_IsNoOp()
    {
        TaskItem[] tasks = [Task("1", "a", due: Ms("2026-07-01T00:00:00Z")), Task("2", "b", due: null)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Due, FilterOp.GreaterThan, "not-a-date")]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_MultipleRules_AreAnded()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", status: "to do", due: Ms("2026-07-01T00:00:00Z")),
            Task("2", "b", status: "to do", due: Ms("2026-07-20T00:00:00Z")),
            Task("3", "c", status: "done", due: Ms("2026-07-01T00:00:00Z")),
        ];

        var result = TaskView.Filter(tasks,
        [
            Rule(TaskField.Status, FilterOp.Is, "to do"),
            Rule(TaskField.Due, FilterOp.LessThan, "2026-07-10"),
        ]);

        Assert.Equal(["1"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_NoRules_ReturnsAll()
    {
        TaskItem[] tasks = [Task("1", "a"), Task("2", "b")];

        Assert.Equal(2, TaskView.Filter(tasks, []).Count);
        Assert.Equal(2, TaskView.Filter(tasks, null).Count);
    }

    // ── Sort ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Sort_DefaultOrder_IsDueThenName_UndatedLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "Zebra", due: null),
            Task("2", "Apple", due: Ms("2026-07-10T00:00:00Z")),
            Task("3", "Mango", due: Ms("2026-07-01T00:00:00Z")),
            Task("4", "Berry", due: null),
        ];

        var result = TaskView.Sort(tasks, field: null, SortDirection.Ascending);

        // Dated first (soonest→latest), then undated by name.
        Assert.Equal(["3", "2", "4", "1"], result.Select(t => t.Id));
    }

    [Fact]
    public void Sort_ByStatusAscending_AlphaWithNullsLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", status: "review"),
            Task("2", "b", status: null),
            Task("3", "c", status: "blocked"),
        ];

        var result = TaskView.Sort(tasks, TaskField.Status, SortDirection.Ascending);

        Assert.Equal(["3", "1", "2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Sort_ByLastActivityDescending_NewestFirst_NullsStillLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", updated: Ms("2026-06-01T00:00:00Z")),
            Task("2", "b", updated: Ms("2026-06-20T00:00:00Z")),
            Task("3", "c", updated: null),
        ];

        var result = TaskView.Sort(tasks, TaskField.LastActivity, SortDirection.Descending);

        Assert.Equal(["2", "1", "3"], result.Select(t => t.Id));
    }

    [Fact]
    public void Sort_TieBreaksByNameThenId()
    {
        TaskItem[] tasks =
        [
            Task("z", "Same", status: "to do"),
            Task("a", "Same", status: "to do"),
            Task("m", "Same", status: "to do"),
        ];

        var result = TaskView.Sort(tasks, TaskField.Status, SortDirection.Ascending);

        Assert.Equal(["a", "m", "z"], result.Select(t => t.Id));
    }

    // ── Group ────────────────────────────────────────────────────────────────

    [Fact]
    public void Group_Null_ReturnsSingleUngroupedSection()
    {
        TaskItem[] tasks = [Task("1", "a"), Task("2", "b")];

        var groups = TaskView.Group(tasks, field: null);

        Assert.Single(groups);
        Assert.Null(groups[0].Label);
        Assert.Equal(2, groups[0].Tasks.Count);
    }

    [Fact]
    public void Group_ByList_OrdersAlpha_MissingBucketLast_PreservesWithinGroupOrder()
    {
        // Pre-sorted input; grouping must keep each group's incoming order.
        TaskItem[] tasks =
        [
            Task("1", "a", list: "Work"),
            Task("2", "b", list: null),
            Task("3", "c", list: "Admin"),
            Task("4", "d", list: "Work"),
        ];

        var groups = TaskView.Group(tasks, TaskField.List);

        Assert.Equal(["Admin", "Work", "(none)"], groups.Select(g => g.Label));
        Assert.Equal(["1", "4"], groups[1].Tasks.Select(t => t.Id));
        Assert.Equal(["2"], groups[2].Tasks.Select(t => t.Id));
    }

    [Fact]
    public void Group_ByDate_BucketsByUtcCalendarDay_NoDateLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", due: Ms("2026-07-01T09:00:00Z")),
            Task("2", "b", due: Ms("2026-07-01T23:00:00Z")),
            Task("3", "c", due: Ms("2026-07-03T00:00:00Z")),
            Task("4", "d", due: null),
        ];

        var groups = TaskView.Group(tasks, TaskField.Due);

        Assert.Equal(["2026-07-01", "2026-07-03", "No date"], groups.Select(g => g.Label));
        Assert.Equal(["1", "2"], groups[0].Tasks.Select(t => t.Id));
    }

    // ── Apply (filter → sort → group together) ───────────────────────────────

    [Fact]
    public void Apply_RunsFilterThenSortThenGroup()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", status: "to do", list: "Work", due: Ms("2026-07-10T00:00:00Z")),
            Task("2", "b", status: "done", list: "Work", due: Ms("2026-07-01T00:00:00Z")),
            Task("3", "c", status: "to do", list: "Admin", due: Ms("2026-07-05T00:00:00Z")),
            Task("4", "d", status: "to do", list: "Work", due: Ms("2026-07-02T00:00:00Z")),
        ];

        var settings = new ViewSettings
        {
            Filters = [Rule(TaskField.Status, FilterOp.Is, "to do")],
            SortField = TaskField.Due,
            SortDirection = SortDirection.Ascending,
            GroupField = TaskField.List,
        };

        var groups = TaskView.Apply(tasks, settings);

        // "done" task #2 filtered out; groups alpha by list; within group sorted by due asc.
        Assert.Equal(["Admin", "Work"], groups.Select(g => g.Label));
        Assert.Equal(["3"], groups[0].Tasks.Select(t => t.Id));
        Assert.Equal(["4", "1"], groups[1].Tasks.Select(t => t.Id));
    }
}
