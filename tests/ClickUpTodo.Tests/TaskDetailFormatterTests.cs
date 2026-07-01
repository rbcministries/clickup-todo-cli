using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure detail-view text formatting (issue #17). The Terminal.Gui glue isn't
/// unit-testable in CI, so the layout logic lives in <see cref="TaskDetailFormatter"/> and is
/// covered here.
/// </summary>
public sealed class TaskDetailFormatterTests
{
    private static TaskDetail Sample(
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? assignees = null,
        IReadOnlyList<CustomFieldItem>? customFields = null,
        string? description = "A description.",
        string? customId = null,
        string? listId = "L1",
        IReadOnlyList<NamedEntity>? lists = null) => new()
        {
            Id = "abc",
            CustomId = customId,
            Name = "Ship the report",
            StatusName = "in progress",
            ListId = listId,
            ListName = "Personal Tasks",
            Lists = lists ?? [],
            Priority = "high",
            Description = description,
            Tags = tags ?? [],
            Assignees = assignees ?? [],
            CustomFields = customFields ?? [],
        };

    [Fact]
    public void Header_LeadsWithTitle()
    {
        var header = TaskDetailFormatter.Header(Sample());
        Assert.StartsWith("Ship the report", header);
    }

    [Fact]
    public void Header_IncludesCustomIdWhenPresent()
    {
        var header = TaskDetailFormatter.Header(Sample(customId: "DEV-42"));
        Assert.Contains("DEV-42", header);
    }

    [Fact]
    public void Header_ListsTagsAndAssignees()
    {
        var header = TaskDetailFormatter.Header(Sample(tags: ["urgent", "q3"], assignees: ["ben", "sam"]));
        Assert.Contains("Tags: urgent, q3", header);
        Assert.Contains("Assignees: ben, sam", header);
    }

    [Fact]
    public void Header_NoAssignees_ShowsNone()
    {
        var header = TaskDetailFormatter.Header(Sample(assignees: []));
        Assert.Contains("Assignees: (none)", header);
    }

    [Fact]
    public void Header_OmitsTagsLineWhenEmpty()
    {
        var header = TaskDetailFormatter.Header(Sample(tags: []));
        Assert.DoesNotContain("Tags:", header);
    }

    [Fact]
    public void Description_FallsBackWhenBlank()
    {
        Assert.Equal("(no description)", TaskDetailFormatter.Description(Sample(description: "  ")));
        Assert.Equal("(no description)", TaskDetailFormatter.Description(Sample(description: null)));
    }

    [Fact]
    public void Description_TrimsContent()
    {
        Assert.Equal("Hello", TaskDetailFormatter.Description(Sample(description: "\n Hello \n")));
    }

    [Fact]
    public void Comments_EmptyShowsPlaceholder()
    {
        Assert.Equal("(no comments)", TaskDetailFormatter.Comments([]));
    }

    [Fact]
    public void Comments_RenderAuthorTextAndResolvedMarker()
    {
        CommentItem[] comments =
        [
            new("1", "ben", DateMs: null, Text: "First!", Resolved: false),
            new("2", "sam", DateMs: null, Text: "Done.", Resolved: true),
        ];

        var text = TaskDetailFormatter.Comments(comments);

        Assert.Contains("ben", text);
        Assert.Contains("First!", text);
        Assert.Contains("sam", text);
        Assert.Contains("[resolved]", text);
    }

    [Fact]
    public void Comments_EmptyBodyShowsPlaceholder()
    {
        var text = TaskDetailFormatter.Comments([new("1", "ben", null, "   ", false)]);
        Assert.Contains("(empty comment)", text);
    }

    [Fact]
    public void OtherAttributes_IncludesListAndDateLabels()
    {
        var text = TaskDetailFormatter.OtherAttributes(Sample());
        Assert.Contains("List:", text);
        Assert.Contains("Personal Tasks", text);
        Assert.Contains("Created:", text);
        Assert.Contains("Last activity:", text);
    }

    [Fact]
    public void OtherAttributes_MissingDatesShowDash()
    {
        var text = TaskDetailFormatter.OtherAttributes(Sample());
        // No created/updated/due set on the sample → each renders as an em dash.
        Assert.Contains("Created:       —", text);
    }

    [Fact]
    public void OtherAttributes_ListsCustomFieldNamesAndTypes()
    {
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(customFields: [new("Sprint", "drop_down"), new("Estimate", null)]));
        Assert.Contains("Sprint", text);
        Assert.Contains("drop_down", text);
        Assert.Contains("Estimate", text);
    }

    [Fact]
    public void OtherAttributes_NoCustomFieldsShowsNone()
    {
        var text = TaskDetailFormatter.OtherAttributes(Sample(customFields: []));
        Assert.Contains("(none)", text);
    }

    [Fact]
    public void OtherAttributes_SingleList_OmitsListsLine()
    {
        // No locations → only the home list; the multi-list "Lists:" line must not appear.
        var text = TaskDetailFormatter.OtherAttributes(Sample(lists: []));
        Assert.DoesNotContain("Lists:", text);
    }

    [Fact]
    public void OtherAttributes_HomeListOnlyLocation_OmitsListsLine()
    {
        // ClickUp may echo the home list back in locations; that alone is still a single list.
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(listId: "L1", lists: [new NamedEntity("L1", "Personal Tasks")]));
        Assert.DoesNotContain("Lists:", text);
    }

    [Fact]
    public void OtherAttributes_MultipleLists_RendersFullMembershipHomeFirst()
    {
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(listId: "L1", lists: [new NamedEntity("L2", "Engineering"), new NamedEntity("L3", "Q3 Launch")]));
        Assert.Contains("Lists:         Personal Tasks, Engineering, Q3 Launch", text);
    }

    [Fact]
    public void OtherAttributes_MultipleLists_DedupesHomeWhenEchoedInLocations()
    {
        // locations includes the home list (by id) plus one more → home listed once, home-first.
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(listId: "L1", lists: [new NamedEntity("L1", "Personal Tasks"), new NamedEntity("L2", "Engineering")]));
        Assert.Contains("Lists:         Personal Tasks, Engineering", text);
    }

    [Fact]
    public void OtherAttributes_MultipleLists_IgnoresBlankNamedLocations()
    {
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(listId: "L1", lists: [new NamedEntity("L2", "   "), new NamedEntity("L3", "Engineering")]));
        Assert.Contains("Lists:         Personal Tasks, Engineering", text);
    }
}
