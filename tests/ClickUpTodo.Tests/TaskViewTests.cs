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
        string id, string name, string? status = null, string? list = null, long? due = null, long? updated = null,
        long? created = null, int? priority = null)
        => new()
        {
            Id = id,
            Name = name,
            StatusName = status,
            ListName = list,
            DueDateMs = due,
            UpdatedMs = updated,
            CreatedMs = created,
            PriorityLevel = priority,
            PriorityName = ClickUpPriority.NameFromLevel(priority),
        };

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
    public void Filter_ByCreated_GreaterOrEqual_UsesEpochMs_NullExcluded()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", created: Ms("2026-06-01T00:00:00Z")),
            Task("2", "b", created: Ms("2026-06-20T00:00:00Z")),
            Task("3", "c", created: null),
        ];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Created, FilterOp.GreaterOrEqual, "2026-06-10")]);

        Assert.Equal(["2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_ByCreated_IsNot_KeepsNullValues()
    {
        var target = Ms("2026-06-01T00:00:00Z");
        TaskItem[] tasks = [Task("1", "a", created: target), Task("2", "b", created: target + 1), Task("3", "c", created: null)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Created, FilterOp.IsNot, "2026-06-01")]);

        Assert.Equal(["2", "3"], result.Select(t => t.Id));
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
    public void Sort_ByCreatedAscending_OldestFirst_NullsLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", created: Ms("2026-06-20T00:00:00Z")),
            Task("2", "b", created: null),
            Task("3", "c", created: Ms("2026-06-01T00:00:00Z")),
        ];

        var result = TaskView.Sort(tasks, TaskField.Created, SortDirection.Ascending);

        Assert.Equal(["3", "1", "2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Sort_ByCreatedDescending_NewestFirst_NullsStillLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", created: Ms("2026-06-01T00:00:00Z")),
            Task("2", "b", created: Ms("2026-06-20T00:00:00Z")),
            Task("3", "c", created: null),
        ];

        var result = TaskView.Sort(tasks, TaskField.Created, SortDirection.Descending);

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

    [Fact]
    public void Group_ByCreated_BucketsByUtcCalendarDay_NoDateLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", created: Ms("2026-06-01T09:00:00Z")),
            Task("2", "b", created: Ms("2026-06-01T23:00:00Z")),
            Task("3", "c", created: Ms("2026-06-03T00:00:00Z")),
            Task("4", "d", created: null),
        ];

        var groups = TaskView.Group(tasks, TaskField.Created);

        Assert.Equal(["2026-06-01", "2026-06-03", "No date"], groups.Select(g => g.Label));
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

    // ── Priority (ordinal) ─────────────────────────────────────────────────────

    [Fact]
    public void Sort_ByPriorityAscending_UrgentFirst_NoPriorityLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", priority: 3),    // Normal
            Task("2", "b", priority: null), // none
            Task("3", "c", priority: 1),    // Urgent
            Task("4", "d", priority: 4),    // Low
            Task("5", "e", priority: 2),    // High
        ];

        var result = TaskView.Sort(tasks, TaskField.Priority, SortDirection.Ascending);

        // Urgent → High → Normal → Low, then no-priority last (not alphabetical).
        Assert.Equal(["3", "5", "1", "4", "2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Sort_ByPriorityDescending_LowFirst_NoPriorityStillLast()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", priority: 1),    // Urgent
            Task("2", "b", priority: null), // none
            Task("3", "c", priority: 4),    // Low
            Task("4", "d", priority: 3),    // Normal
        ];

        var result = TaskView.Sort(tasks, TaskField.Priority, SortDirection.Descending);

        // Low → Normal → … → Urgent, but missing values remain last regardless of direction.
        Assert.Equal(["3", "4", "1", "2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Group_ByPriority_OrdersByImportance_NoneLast()
    {
        // Deliberately out of importance order to prove grouping keys off the level, not input order.
        TaskItem[] tasks =
        [
            Task("1", "a", priority: 3),    // Normal
            Task("2", "b", priority: 1),    // Urgent
            Task("3", "c", priority: null), // none
            Task("4", "d", priority: 3),    // Normal
            Task("5", "e", priority: 1),    // Urgent
        ];

        var groups = TaskView.Group(tasks, TaskField.Priority);

        Assert.Equal(["Urgent", "Normal", "(none)"], groups.Select(g => g.Label));
        Assert.Equal(["2", "5"], groups[0].Tasks.Select(t => t.Id)); // within-group order preserved
        Assert.Equal(["3"], groups[2].Tasks.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityIs_MatchesByName_CaseInsensitive()
    {
        TaskItem[] tasks = [Task("1", "a", priority: 1), Task("2", "b", priority: 2), Task("3", "c", priority: null)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.Is, "urgent")]);

        Assert.Equal(["1"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityIsNone_MatchesNoPriorityTasks()
    {
        TaskItem[] tasks = [Task("1", "a", priority: 2), Task("2", "b", priority: null)];

        // "(none)" (an unrecognised priority name) targets the no-priority bucket.
        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.Is, "(none)")]);

        Assert.Equal(["2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityIsNot_ExcludesMatch_KeepsOthersIncludingNone()
    {
        TaskItem[] tasks = [Task("1", "a", priority: 1), Task("2", "b", priority: 2), Task("3", "c", priority: null)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.IsNot, "High")]);

        Assert.Equal(["1", "3"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityGreaterThan_MeansMoreUrgent()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", priority: 1),    // Urgent
            Task("2", "b", priority: 2),    // High
            Task("3", "c", priority: 3),    // Normal
            Task("4", "d", priority: 4),    // Low
            Task("5", "e", priority: null), // none
        ];

        // "higher than Normal" = more urgent than Normal → Urgent, High.
        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.GreaterThan, "Normal")]);

        Assert.Equal(["1", "2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityGreaterOrEqual_IsInclusive()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", priority: 1), // Urgent
            Task("2", "b", priority: 2), // High
            Task("3", "c", priority: 3), // Normal
        ];

        // "at least High" → Urgent, High.
        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.GreaterOrEqual, "High")]);

        Assert.Equal(["1", "2"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityLessThan_MeansLessUrgent_ExcludesNoPriority()
    {
        TaskItem[] tasks =
        [
            Task("1", "a", priority: 2),    // High
            Task("2", "b", priority: 3),    // Normal
            Task("3", "c", priority: 4),    // Low
            Task("4", "d", priority: null), // none — never satisfies an ordering op
        ];

        // "lower than High" = less urgent than High → Normal, Low.
        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.LessThan, "High")]);

        Assert.Equal(["2", "3"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityIs_AlsoAcceptsLevelNumberValue()
    {
        TaskItem[] tasks = [Task("1", "a", priority: 1), Task("2", "b", priority: 2), Task("3", "c", priority: null)];

        // A level string ("1") is accepted as an alternative to the name ("Urgent").
        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.Is, "1")]);

        Assert.Equal(["1"], result.Select(t => t.Id));
    }

    [Fact]
    public void Filter_PriorityOrderingWithUnparseableTarget_IsNoOp()
    {
        TaskItem[] tasks = [Task("1", "a", priority: 1), Task("2", "b", priority: null)];

        var result = TaskView.Filter(tasks, [Rule(TaskField.Priority, FilterOp.GreaterThan, "banana")]);

        Assert.Equal(2, result.Count);
    }
}
