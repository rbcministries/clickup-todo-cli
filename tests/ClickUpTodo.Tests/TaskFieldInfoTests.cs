using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the F3 field metadata helpers (issue #19): which operators a field allows, the
/// human-readable rule rendering, and parsing user-entered date/numeric values into epoch ms.
/// </summary>
public sealed class TaskFieldInfoTests
{
    [Theory]
    [InlineData(TaskField.Status, false)]
    [InlineData(TaskField.List, false)]
    [InlineData(TaskField.LastActivity, true)]
    [InlineData(TaskField.Due, true)]
    public void IsNumeric_OnlyDateFields(TaskField field, bool expected)
        => Assert.Equal(expected, TaskFieldInfo.IsNumeric(field));

    [Fact]
    public void ValidOps_Categorical_IsAndIsNotOnly()
    {
        Assert.Equal([FilterOp.Is, FilterOp.IsNot], TaskFieldInfo.ValidOps(TaskField.Status));
        Assert.Equal([FilterOp.Is, FilterOp.IsNot], TaskFieldInfo.ValidOps(TaskField.List));
    }

    [Fact]
    public void ValidOps_Numeric_IncludesOrderingOperators()
    {
        var ops = TaskFieldInfo.ValidOps(TaskField.Due);

        Assert.Contains(FilterOp.GreaterThan, ops);
        Assert.Contains(FilterOp.LessOrEqual, ops);
        Assert.Equal(6, ops.Count);
    }

    [Fact]
    public void Describe_RendersFieldOpValue()
        => Assert.Equal("Status IS Done", TaskFieldInfo.Describe(new FilterRule { Field = TaskField.Status, Op = FilterOp.Is, Value = "Done" }));

    [Fact]
    public void TryParseNumeric_RawEpochMs()
    {
        Assert.True(TaskFieldInfo.TryParseNumeric("1751328000000", out var ms));
        Assert.Equal(1751328000000, ms);
    }

    [Fact]
    public void TryParseNumeric_DateOnly_IsUtcMidnight()
    {
        Assert.True(TaskFieldInfo.TryParseNumeric("2026-07-01", out var ms));
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00Z").ToUnixTimeMilliseconds(), ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tomorrow")]
    [InlineData("not-a-date")]
    public void TryParseNumeric_RejectsUnparseable(string value)
        => Assert.False(TaskFieldInfo.TryParseNumeric(value, out _));
}
