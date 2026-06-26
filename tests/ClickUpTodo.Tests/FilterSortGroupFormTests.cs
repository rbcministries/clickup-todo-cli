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
}
