using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="TaskItemProjection.FromDetail"/> — the <see cref="TaskDetail"/> →
/// <see cref="TaskItem"/> projection used when launching Quick Updates from the Task Detail view for a
/// task that isn't in the list snapshot (#159).
/// </summary>
public sealed class TaskItemProjectionTests
{
    private static TaskDetail Sample(string? priority = "High", IReadOnlyList<string>? assignees = null) => new()
    {
        Id = "abc123",
        CustomId = "ENG-7",
        Name = "Fix the thing",
        Url = "https://app.clickup.com/t/abc123",
        StatusName = "in progress",
        StatusColor = "#4194f6",
        ListId = "list-9",
        ListName = "Personal",
        Description = "body text",
        Priority = priority,
        PriorityColor = "#f50000",
        DueDateMs = 1_700_000_000_000,
        CreatedMs = 1_600_000_000_000,
        UpdatedMs = 1_650_000_000_000,
        Assignees = assignees ?? ["Ada Lovelace", "Alan Turing"],
    };

    [Fact]
    public void FromDetail_CarriesIdentityStatusListAndDates()
    {
        var item = TaskItemProjection.FromDetail(Sample());

        Assert.Equal("abc123", item.Id);
        Assert.Equal("ENG-7", item.CustomId);
        Assert.Equal("Fix the thing", item.Name);
        Assert.Equal("https://app.clickup.com/t/abc123", item.Url);
        Assert.Equal("in progress", item.StatusName);
        Assert.Equal("#4194f6", item.StatusColor);
        Assert.Equal("list-9", item.ListId);
        Assert.Equal("Personal", item.ListName);
        Assert.Equal("#f50000", item.PriorityColor); // High resolves, so its colour is carried
        Assert.Equal(1_700_000_000_000, item.DueDateMs);
        Assert.Equal(1_600_000_000_000, item.CreatedMs);
        Assert.Equal(1_650_000_000_000, item.UpdatedMs);
    }

    [Theory]
    [InlineData("Urgent", 1, "Urgent")]
    [InlineData("high", 2, "High")]
    [InlineData("Normal", 3, "Normal")]
    [InlineData("LOW", 4, "Low")]
    public void FromDetail_RecoversPriorityLevelAndCanonicalNameFromName(string priority, int level, string canonical)
    {
        var item = TaskItemProjection.FromDetail(Sample(priority: priority));

        Assert.Equal(level, item.PriorityLevel);
        Assert.Equal(canonical, item.PriorityName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Someday-Maybe")]
    public void FromDetail_NoOrUnrecognisedPriority_LeavesLevelAndNameNull(string? priority)
    {
        var item = TaskItemProjection.FromDetail(Sample(priority: priority));

        Assert.Null(item.PriorityLevel);
        Assert.Null(item.PriorityName);
        Assert.Null(item.PriorityColor); // no coloured-but-nameless priority
    }

    [Fact]
    public void FromDetail_MapsAssigneeNamesToPlaceholderIdAssignees()
    {
        var item = TaskItemProjection.FromDetail(Sample(assignees: ["Ada Lovelace", "Alan Turing"]));

        Assert.Collection(item.Assignees,
            a => { Assert.Equal(0, a.Id); Assert.Equal("Ada Lovelace", a.Name); },
            a => { Assert.Equal(0, a.Id); Assert.Equal("Alan Turing", a.Name); });
    }

    [Fact]
    public void FromDetail_NoAssignees_YieldsEmptyList()
    {
        var item = TaskItemProjection.FromDetail(Sample(assignees: []));

        Assert.Empty(item.Assignees);
    }
}
