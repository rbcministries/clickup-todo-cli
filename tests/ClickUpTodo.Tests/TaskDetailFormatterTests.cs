using System.Text.Json;
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
        string? customId = null) => new()
        {
            Id = "abc",
            CustomId = customId,
            Name = "Ship the report",
            StatusName = "in progress",
            ListName = "Personal Tasks",
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

    // ── Custom-field value rendering (issue #35) ─────────────────────────────

    /// <summary>Parses a JSON literal into a detached <see cref="JsonElement"/> for a field value.</summary>
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CustomFieldItem Field(string type, string valueJson, params CustomFieldOption[] options)
        => new("F", type, Json(valueJson), options);

    [Fact]
    public void CustomFieldValue_AbsentValue_ReturnsNull()
    {
        Assert.Null(TaskDetailFormatter.CustomFieldValue(new CustomFieldItem("F", "text")));
        Assert.Null(TaskDetailFormatter.CustomFieldValue(Field("text", "null")));
    }

    [Fact]
    public void CustomFieldValue_DropDown_ResolvesByOrderIndex()
    {
        var f = Field("drop_down", "1",
            new CustomFieldOption("o0", "Backlog", 0),
            new CustomFieldOption("o1", "In progress", 1));
        Assert.Equal("In progress", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_DropDown_ResolvesById()
    {
        var f = Field("drop_down", "\"o1\"",
            new CustomFieldOption("o0", "Backlog", 0),
            new CustomFieldOption("o1", "In progress", 1));
        Assert.Equal("In progress", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_DropDown_NoMatchFallsBackToRaw()
    {
        var f = Field("drop_down", "9", new CustomFieldOption("o0", "Backlog", 0));
        Assert.Equal("9", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_Labels_MapsIdsToNames()
    {
        var f = Field("labels", "[\"a\", \"c\"]",
            new CustomFieldOption("a", "Alpha", null),
            new CustomFieldOption("b", "Beta", null),
            new CustomFieldOption("c", "Gamma", null));
        Assert.Equal("Alpha, Gamma", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_Labels_UsesLabelFallbackName()
    {
        // Options built from a labels field carry their text via `label` (mapped into Name by the reader).
        var f = Field("labels", "[\"x\"]", new CustomFieldOption("x", "Important", null));
        Assert.Equal("Important", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_Users_ShowsUsernames()
    {
        var f = Field("users", "[{\"id\":1,\"username\":\"ben\"},{\"id\":2,\"email\":\"sam@x.io\"}]");
        Assert.Equal("ben, sam@x.io", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_Date_FormatsEpochMs()
    {
        // Stored as an epoch-ms string (ClickUp's shape). Rendered as a date+time, not the raw number.
        var f = Field("date", "\"1700000000000\"");
        var rendered = TaskDetailFormatter.CustomFieldValue(f);
        Assert.NotNull(rendered);
        Assert.DoesNotContain("1700000000000", rendered);
        Assert.Contains(":", rendered);    // has the HH:mm portion of the date format
        Assert.Contains("2023", rendered); // 1700000000000 ms → Nov 2023 in every timezone
    }

    [Fact]
    public void CustomFieldValue_Checkbox()
    {
        Assert.Equal("Yes", TaskDetailFormatter.CustomFieldValue(Field("checkbox", "true")));
        Assert.Equal("No", TaskDetailFormatter.CustomFieldValue(Field("checkbox", "false")));
        Assert.Equal("Yes", TaskDetailFormatter.CustomFieldValue(Field("checkbox", "\"true\"")));
    }

    [Fact]
    public void CustomFieldValue_Number_TrimsAndAcceptsStrings()
    {
        Assert.Equal("3.5", TaskDetailFormatter.CustomFieldValue(Field("number", "3.5")));
        Assert.Equal("42", TaskDetailFormatter.CustomFieldValue(Field("number", "42.0")));
        Assert.Equal("42", TaskDetailFormatter.CustomFieldValue(Field("currency", "\"42\"")));
    }

    [Fact]
    public void CustomFieldValue_Progress_ShowsPercent()
    {
        var f = Field("automatic_progress", "{\"percent_complete\": 42, \"current\": 4}");
        Assert.Equal("42%", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_Text_RendersString()
    {
        Assert.Equal("hello world", TaskDetailFormatter.CustomFieldValue(Field("short_text", "\"hello world\"")));
        Assert.Equal("https://x.io", TaskDetailFormatter.CustomFieldValue(Field("url", "\"https://x.io\"")));
    }

    [Fact]
    public void CustomFieldValue_UnknownType_CompactFallback()
    {
        // Unknown field type with a structured value → a compact, single-line, stringified value
        // (interior whitespace/newlines collapsed to single spaces).
        var f = Field("mystery", "{\"a\": 1,\n \"b\": 2}");
        Assert.Equal("{\"a\": 1, \"b\": 2}", TaskDetailFormatter.CustomFieldValue(f));
    }

    [Fact]
    public void CustomFieldValue_Labels_EmptyArrayFallsBack()
    {
        Assert.Equal("[]", TaskDetailFormatter.CustomFieldValue(Field("labels", "[]")));
    }

    [Fact]
    public void CustomFieldValue_Users_NonArrayFallsBack()
    {
        // A users field whose value isn't the expected array → compact fallback, no throw.
        Assert.Equal("oops", TaskDetailFormatter.CustomFieldValue(Field("users", "\"oops\"")));
    }

    [Fact]
    public void CustomFieldValue_EmojiRatingType_FallsBackToRawValue()
    {
        // "emoji" is deliberately not treated as a plain number; a bare number still renders as-is
        // via the compact fallback, and an object shape would render compactly rather than crash.
        Assert.Equal("5", TaskDetailFormatter.CustomFieldValue(Field("emoji", "5")));
    }

    [Fact]
    public void CustomFieldValue_LongText_Truncated()
    {
        var f = Field("text", "\"" + new string('x', 500) + "\"");
        var rendered = TaskDetailFormatter.CustomFieldValue(f)!;
        Assert.True(rendered.Length < 500);
        Assert.EndsWith("…", rendered);
    }

    [Fact]
    public void OtherAttributes_RendersCustomFieldValue()
    {
        var field = Field("drop_down", "0", new CustomFieldOption("o0", "Backlog", 0));
        var text = TaskDetailFormatter.OtherAttributes(Sample(customFields: [field with { Name = "Sprint" }]));
        Assert.Contains("Sprint", text);
        Assert.Contains(": Backlog", text);
    }

    [Fact]
    public void OtherAttributes_OmitsValueWhenAbsent()
    {
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(customFields: [new("Estimate", "number")]));
        Assert.Contains("Estimate", text);
        Assert.DoesNotContain("Estimate  (number):", text);
    }
}
