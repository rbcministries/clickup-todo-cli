using ClickUpTodo.Configuration;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure input logic behind the F3 screen (issue #19): index↔field mapping for the
/// sort/group pickers, selection clamping, and filter-rule validation (non-blank value; ordering
/// operators only on numeric/date fields).
/// </summary>
public sealed class FilterSortGroupFormTests
{
    [Fact]
    public void FieldChoices_NoneFirstThenFields()
    {
        var choices = FilterSortGroupForm.FieldChoices();

        Assert.Equal("(none)", choices[0]);
        Assert.Equal(FilterSortGroupForm.Fields.Count + 1, choices.Count);
    }

    [Fact]
    public void Fields_IncludesCreated()
    {
        Assert.Contains(TaskField.Created, FilterSortGroupForm.Fields);
        Assert.Contains("Created", FilterSortGroupForm.FieldChoices());
    }

    [Fact]
    public void Fields_IncludesAssignee()
    {
        Assert.Contains(TaskField.Assignee, FilterSortGroupForm.Fields);
        Assert.Contains("Assignee", FilterSortGroupForm.FieldChoices());
    }

    [Fact]
    public void TryBuildRule_AssigneeIs_Valid()
    {
        var ok = FilterSortGroupForm.TryBuildRule(TaskField.Assignee, FilterOp.Is, " me ", out var rule, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(TaskField.Assignee, rule!.Field);
        Assert.Equal("me", rule.Value);
    }

    [Theory]
    [InlineData(FilterOp.IsNot)]
    [InlineData(FilterOp.GreaterThan)]
    public void TryBuildRule_AssigneeNonIsOperator_Rejected(FilterOp op)
    {
        var ok = FilterSortGroupForm.TryBuildRule(TaskField.Assignee, op, "me", out var rule, out var error);

        Assert.False(ok);
        Assert.Null(rule);
        Assert.Contains("IS", error);
    }

    [Fact]
    public void FieldIndex_RoundTrips()
    {
        Assert.Equal(0, FilterSortGroupForm.FieldToIndex(null));
        Assert.Null(FilterSortGroupForm.IndexToField(0));

        foreach (var field in FilterSortGroupForm.Fields)
        {
            var idx = FilterSortGroupForm.FieldToIndex(field);
            Assert.Equal(field, FilterSortGroupForm.IndexToField(idx));
        }
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(-1, 0)]
    [InlineData(99, 0)]
    [InlineData(2, 2)]
    public void Clamp_KeepsValidElseZero(int? selected, int expected)
        => Assert.Equal(expected, FilterSortGroupForm.Clamp(selected, count: 4));

    [Fact]
    public void TryBuildRule_Valid_CategoricalIs()
    {
        var ok = FilterSortGroupForm.TryBuildRule(TaskField.Status, FilterOp.Is, "  to do  ", out var rule, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(TaskField.Status, rule!.Field);
        Assert.Equal(FilterOp.Is, rule.Op);
        Assert.Equal("to do", rule.Value); // trimmed
    }

    [Fact]
    public void TryBuildRule_Valid_NumericOrdering()
    {
        var ok = FilterSortGroupForm.TryBuildRule(TaskField.Due, FilterOp.GreaterThan, "2026-07-01", out var rule, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(FilterOp.GreaterThan, rule!.Op);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryBuildRule_BlankValue_Rejected(string? value)
    {
        var ok = FilterSortGroupForm.TryBuildRule(TaskField.Status, FilterOp.Is, value, out var rule, out var error);

        Assert.False(ok);
        Assert.Null(rule);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryBuildRule_OrderingOnCategorical_Rejected()
    {
        var ok = FilterSortGroupForm.TryBuildRule(TaskField.List, FilterOp.GreaterThan, "Work", out var rule, out var error);

        Assert.False(ok);
        Assert.Null(rule);
        Assert.Contains("IS / IS NOT", error);
    }

    [Fact]
    public void TryBuildRule_Valid_PriorityOrdering()
    {
        // Priority is ordinal, so ordering operators are allowed (unlike categorical fields).
        var ok = FilterSortGroupForm.TryBuildRule(TaskField.Priority, FilterOp.GreaterOrEqual, "High", out var rule, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(TaskField.Priority, rule!.Field);
        Assert.Equal(FilterOp.GreaterOrEqual, rule.Op);
    }

    [Fact]
    public void Fields_IncludesPriority()
        => Assert.Contains(TaskField.Priority, FilterSortGroupForm.Fields);

    // ── BuildResult: the Save-time marshalling shared by the _screens form and the #554 native modal ──

    [Fact]
    public void BuildResult_MapsIndicesFiltersAndDirection()
    {
        var filters = new List<FilterRule> { new() { Field = TaskField.Priority, Op = FilterOp.Is, Value = "High" } };
        var current = new ViewSettings();

        var result = FilterSortGroupForm.BuildResult(
            filters,
            FilterSortGroupForm.FieldToIndex(TaskField.Priority),
            SortDirection.Descending,
            FilterSortGroupForm.FieldToIndex(TaskField.Status),
            current);

        Assert.Equal(TaskField.Priority, result.SortField);
        Assert.Equal(SortDirection.Descending, result.SortDirection);
        Assert.Equal(TaskField.Status, result.GroupField);
        Assert.Single(result.Filters);
        Assert.Equal("High", result.Filters[0].Value);
    }

    [Fact]
    public void BuildResult_ZeroIndex_IsNoneField()
    {
        var result = FilterSortGroupForm.BuildResult(
            [], sortIndex: 0, SortDirection.Ascending, groupIndex: 0, new ViewSettings());

        Assert.Null(result.SortField);
        Assert.Null(result.GroupField);
        Assert.Empty(result.Filters);
    }

    [Fact]
    public void BuildResult_PreservesSubtasksAndCompletedFromCurrent()
    {
        // F3 doesn't edit the F4 subtasks view (#179) or the F12 completed view (#191); reconstructing
        // ViewSettings without carrying them would silently reset both on any save.
        var current = new ViewSettings { Subtasks = SubtaskView.MineAndUnassigned, Completed = CompletedView.All };

        var result = FilterSortGroupForm.BuildResult([], 0, SortDirection.Ascending, 0, current);

        Assert.Equal(SubtaskView.MineAndUnassigned, result.Subtasks);
        Assert.Equal(CompletedView.All, result.Completed);
    }

    [Fact]
    public void BuildResult_CopiesFilters_NotTheSameInstance()
    {
        // The caller's working list must not alias the saved view's — a later edit of one must not mutate
        // the other (the _screens form and the native modal both hand in their live working list).
        var filters = new List<FilterRule> { new() { Field = TaskField.Status, Op = FilterOp.Is, Value = "Open" } };

        var result = FilterSortGroupForm.BuildResult(filters, 0, SortDirection.Ascending, 0, new ViewSettings());

        Assert.NotSame(filters, result.Filters);
        Assert.Equal(filters.Count, result.Filters.Count);
    }
}
