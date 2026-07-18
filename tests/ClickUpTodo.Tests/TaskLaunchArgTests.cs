using ClickUpTodo;

namespace ClickUpTodo.Tests;

public sealed class TaskLaunchArgTests
{
    [Fact]
    public void Absent_WhenFlagNotPresent()
    {
        var result = TaskLaunchArg.Parse(["--driver", "ansi"]);

        Assert.False(result.Present);
        Assert.False(result.HasId);
        Assert.False(result.MissingValue);
        Assert.Null(result.TaskId);
    }

    [Fact]
    public void NoArgs_IsAbsent()
    {
        Assert.False(TaskLaunchArg.Parse([]).Present);
    }

    [Fact]
    public void SpaceSeparated_ReadsTheId()
    {
        var result = TaskLaunchArg.Parse(["--task", "abc123"]);

        Assert.True(result.Present);
        Assert.True(result.HasId);
        Assert.False(result.MissingValue);
        Assert.Equal("abc123", result.TaskId);
    }

    [Fact]
    public void EqualsForm_ReadsTheId()
    {
        var result = TaskLaunchArg.Parse(["--task=abc123"]);

        Assert.True(result.HasId);
        Assert.Equal("abc123", result.TaskId);
    }

    [Fact]
    public void BareFlag_AtEnd_IsMissingValue()
    {
        var result = TaskLaunchArg.Parse(["--task"]);

        Assert.True(result.Present);
        Assert.True(result.MissingValue);
        Assert.False(result.HasId);
        Assert.Null(result.TaskId);
    }

    [Theory]
    [InlineData("--task=")]
    [InlineData("--task= ")]
    public void EqualsForm_WithBlankValue_IsMissingValue(string arg)
    {
        var result = TaskLaunchArg.Parse([arg]);

        Assert.True(result.Present);
        Assert.True(result.MissingValue);
        Assert.False(result.HasId);
    }

    [Fact]
    public void SpaceSeparated_WithWhitespaceValue_IsMissingValue()
    {
        var result = TaskLaunchArg.Parse(["--task", "   "]);

        Assert.True(result.MissingValue);
        Assert.False(result.HasId);
    }

    [Theory]
    [InlineData("  abc123  ")]
    [InlineData("\tabc123")]
    public void TrimsSurroundingWhitespace(string raw)
    {
        Assert.Equal("abc123", TaskLaunchArg.Parse(["--task", raw]).TaskId);
    }

    [Fact]
    public void EqualsForm_TrimsSurroundingWhitespace()
    {
        Assert.Equal("abc123", TaskLaunchArg.Parse(["--task=  abc123 "]).TaskId);
    }

    [Theory]
    [InlineData("--reset")]
    [InlineData("--driver")]
    [InlineData("--task")]
    public void FlagShapedNextToken_IsMissingValue_NotConsumedAsId(string next)
    {
        // `--task --reset` means the flag was given without an id (→ a clear error), not that the id is
        // literally "--reset". A real ClickUp task/custom id never starts with "--".
        var result = TaskLaunchArg.Parse(["--task", next]);

        Assert.True(result.Present);
        Assert.True(result.MissingValue);
        Assert.False(result.HasId);
        Assert.Null(result.TaskId);
    }

    [Fact]
    public void FindsFlag_AmongOtherArgs()
    {
        var result = TaskLaunchArg.Parse(["--driver", "ansi", "--task", "xyz", "--reset"]);

        Assert.Equal("xyz", result.TaskId);
    }

    [Fact]
    public void FirstOccurrenceWins()
    {
        var result = TaskLaunchArg.Parse(["--task", "first", "--task", "second"]);

        Assert.Equal("first", result.TaskId);
    }

    [Fact]
    public void EqualsForm_DoesNotMatchDifferentFlagWithSamePrefix()
    {
        // A hypothetical future "--taskbar" must not be read as the launch flag.
        var result = TaskLaunchArg.Parse(["--taskbar=on"]);

        Assert.False(result.Present);
    }
}
