using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

public sealed class StatusPickerModelTests
{
    private static IReadOnlyList<StatusOption> Statuses(params string[] names)
        => names.Select(n => new StatusOption(n, null)).ToList();

    [Fact]
    public void FormatStatus_IndentsTheName()
        => Assert.Equal("  in progress", StatusPickerModel.FormatStatus(new StatusOption("in progress", "#fff")));

    [Fact]
    public void PreselectedIndex_FindsTheCurrentStatus()
    {
        var statuses = Statuses("to do", "in progress", "done");

        Assert.Equal(1, StatusPickerModel.PreselectedIndex(statuses, "in progress"));
    }

    [Fact]
    public void PreselectedIndex_IsCaseInsensitive()
    {
        var statuses = Statuses("To Do", "In Progress", "Done");

        Assert.Equal(2, StatusPickerModel.PreselectedIndex(statuses, "done"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a real status")]
    public void PreselectedIndex_ReturnsMinusOne_WhenNoMatch(string? current)
    {
        var statuses = Statuses("to do", "done");

        Assert.Equal(-1, StatusPickerModel.PreselectedIndex(statuses, current));
    }
}
