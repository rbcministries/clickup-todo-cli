using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

public sealed class GroupHeaderPaletteTests
{
    private static TaskItem Task(string id, string? status = null, string? statusColor = null,
        string? listId = null, string? listName = null, int? priorityLevel = null, string? priorityColor = null)
        => new()
        {
            Id = id,
            Name = id,
            StatusName = status,
            StatusColor = statusColor,
            ListId = listId,
            ListName = listName,
            PriorityLevel = priorityLevel,
            PriorityColor = priorityColor,
        };

    private static TaskGroup Group(string? label, params TaskItem[] tasks) => new(label, tasks);

    [Fact]
    public void NullField_YieldsAllNull()
    {
        var colors = GroupHeaderPalette.Resolve(null, [Group("x", Task("1"))]);
        Assert.Equal(new string?[] { null }, colors);
    }

    [Fact]
    public void Status_UsesGroupsStatusColor()
    {
        var groups = new[]
        {
            Group("In progress", Task("1", status: "In progress", statusColor: "#5f55ee")),
            Group("Done", Task("2", status: "Done", statusColor: "#008844")),
        };
        var colors = GroupHeaderPalette.Resolve(TaskField.Status, groups);
        Assert.Equal(new[] { "#5f55ee", "#008844" }, colors);
    }

    [Fact]
    public void List_PrefersFetchedColor_OverGeneratedHue()
    {
        var groups = new[] { Group("CoreOps", Task("1", listId: "L1", listName: "CoreOps")) };
        var listColors = new Dictionary<string, string?> { ["L1"] = "#e16b16" };
        Assert.Equal(new[] { "#e16b16" }, GroupHeaderPalette.Resolve(TaskField.List, groups, listColors));
    }

    [Fact]
    public void List_FallsBackToStableHue_WhenColorUnset()
    {
        var groups = new[] { Group("CoreOps", Task("1", listId: "L1", listName: "CoreOps")) };
        var unset = new Dictionary<string, string?> { ["L1"] = null };

        var first = GroupHeaderPalette.Resolve(TaskField.List, groups, unset)[0];
        var second = GroupHeaderPalette.Resolve(TaskField.List, groups, unset)[0];

        Assert.NotNull(first);
        Assert.Matches("^#[0-9a-f]{6}$", first!);
        Assert.Equal(first, second); // deterministic: same list name → same hue across renders
    }

    [Fact]
    public void Priority_UsesFetchedColor_ThenCanonicalFallback()
    {
        var groups = new[]
        {
            Group("Urgent", Task("1", priorityLevel: 1, priorityColor: "#ff1122")), // explicit color wins
            Group("Low", Task("2", priorityLevel: 4)),                              // canonical fallback (gray)
        };
        var colors = GroupHeaderPalette.Resolve(TaskField.Priority, groups);
        Assert.Equal("#ff1122", colors[0]);
        Assert.Equal("#d8d8d8", colors[1]);
    }

    [Fact]
    public void DateGradient_SpreadsColorsAndLeavesNoDateBucketNeutral()
    {
        var groups = new[]
        {
            Group("2026-01-01", Task("1")),
            Group("2026-02-01", Task("2")),
            Group("2026-03-01", Task("3")),
            Group("(none)", Task("4")), // TaskView labels the missing-date bucket; not a yyyy-MM-dd date
        };
        var colors = GroupHeaderPalette.Resolve(TaskField.Due, groups);
        var dated = colors.Take(3).ToList();

        Assert.All(dated, c => Assert.Matches("^#[0-9a-f]{6}$", c!));
        Assert.Equal(3, dated.Distinct().Count()); // each dated group gets a distinct hue
        Assert.Null(colors[3]);                    // the no-date bucket stays neutral
    }

    [Fact]
    public void HslToRgb_MapsPrimariesCorrectly()
    {
        Assert.Equal((255, 0, 0), GroupHeaderPalette.HslToRgb(0, 1.0, 0.5));     // red
        Assert.Equal((0, 255, 0), GroupHeaderPalette.HslToRgb(120, 1.0, 0.5));   // green
        Assert.Equal((0, 0, 255), GroupHeaderPalette.HslToRgb(240, 1.0, 0.5));   // blue
        Assert.Equal((255, 255, 255), GroupHeaderPalette.HslToRgb(0, 0.0, 1.0)); // white
    }
}
