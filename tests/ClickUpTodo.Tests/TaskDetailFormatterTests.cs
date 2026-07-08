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
        string? customId = null,
        string? listId = "L1",
        IReadOnlyList<NamedEntity>? lists = null,
        string? statusName = "in progress",
        string? priority = "high",
        string? statusColor = null,
        string? priorityColor = null) => new()
        {
            Id = "abc",
            CustomId = customId,
            Name = "Ship the report",
            StatusName = statusName,
            StatusColor = statusColor,
            ListId = listId,
            ListName = "Personal Tasks",
            Lists = lists ?? [],
            Priority = priority,
            PriorityColor = priorityColor,
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
    public void Comments_SeparatorAppearsBetweenAdjacentComments()
    {
        CommentItem[] comments =
        [
            new("1", "ben", DateMs: null, Text: "First!", Resolved: false),
            new("2", "sam", DateMs: null, Text: "Second.", Resolved: false),
        ];

        var text = TaskDetailFormatter.Comments(comments);

        Assert.Contains(TaskDetailFormatter.CommentSeparator, text);
        // The rule sits between the two bodies, on its own line flanked by blank lines.
        Assert.Contains("First!\n\n" + TaskDetailFormatter.CommentSeparator + "\n\nsam", text);
    }

    [Fact]
    public void Comments_SingleCommentHasNoSeparator()
    {
        var text = TaskDetailFormatter.Comments([new("1", "ben", null, "Only one.", false)]);
        Assert.DoesNotContain(TaskDetailFormatter.CommentSeparator, text);
    }

    [Fact]
    public void Comments_SeparatorIsNeverLeadingOrTrailing()
    {
        CommentItem[] comments =
        [
            new("1", "ben", DateMs: null, Text: "First!", Resolved: false),
            new("2", "sam", DateMs: null, Text: "Second.", Resolved: false),
        ];

        var text = TaskDetailFormatter.Comments(comments);

        Assert.False(text.StartsWith(TaskDetailFormatter.CommentSeparator, StringComparison.Ordinal));
        Assert.False(text.TrimEnd('\n').EndsWith(TaskDetailFormatter.CommentSeparator, StringComparison.Ordinal));
    }

    [Fact]
    public void Comments_ThreeCommentsHaveExactlyTwoSeparators()
    {
        CommentItem[] comments =
        [
            new("1", "ben", DateMs: null, Text: "A", Resolved: false),
            new("2", "sam", DateMs: null, Text: "B", Resolved: false),
            new("3", "kim", DateMs: null, Text: "C", Resolved: false),
        ];

        var text = TaskDetailFormatter.Comments(comments);

        var count = text.Split(TaskDetailFormatter.CommentSeparator).Length - 1;
        Assert.Equal(2, count);
    }

    // ---- Stream tab (#106) --------------------------------------------------

    // Comments deliberately supplied out of date order to prove the formatter sorts them.
    private static CommentItem[] ScrambledDatedComments() =>
    [
        new("2", "sam", DateMs: 2000, Text: "BBB", Resolved: false),
        new("3", "kim", DateMs: 3000, Text: "CCC", Resolved: false),
        new("1", "ben", DateMs: 1000, Text: "AAA", Resolved: false),
    ];

    [Fact]
    public void Stream_Ascending_DescriptionFirstThenCommentsOldestToNewest()
    {
        var text = TaskDetailFormatter.Stream(Sample(description: "DESCBODY"), ScrambledDatedComments(), StreamSort.Ascending);

        Assert.StartsWith("Description", text);
        // Description, then AAA (oldest) < BBB < CCC (newest).
        Assert.True(text.IndexOf("DESCBODY", StringComparison.Ordinal) < text.IndexOf("AAA", StringComparison.Ordinal));
        Assert.True(text.IndexOf("AAA", StringComparison.Ordinal) < text.IndexOf("BBB", StringComparison.Ordinal));
        Assert.True(text.IndexOf("BBB", StringComparison.Ordinal) < text.IndexOf("CCC", StringComparison.Ordinal));
    }

    [Fact]
    public void Stream_Descending_CommentsNewestToOldestThenDescriptionLast()
    {
        var text = TaskDetailFormatter.Stream(Sample(description: "DESCBODY"), ScrambledDatedComments(), StreamSort.Descending);

        // CCC (newest) < BBB < AAA (oldest) < Description body (last).
        Assert.True(text.IndexOf("CCC", StringComparison.Ordinal) < text.IndexOf("BBB", StringComparison.Ordinal));
        Assert.True(text.IndexOf("BBB", StringComparison.Ordinal) < text.IndexOf("AAA", StringComparison.Ordinal));
        Assert.True(text.IndexOf("AAA", StringComparison.Ordinal) < text.IndexOf("DESCBODY", StringComparison.Ordinal));
    }

    [Fact]
    public void Stream_UndatedComments_SortAsOldest()
    {
        CommentItem[] comments =
        [
            new("d", "sam", DateMs: 2000, Text: "DAT", Resolved: false),
            new("u", "ben", DateMs: null, Text: "UND", Resolved: false),
        ];

        var asc = TaskDetailFormatter.Stream(Sample(description: "DESCBODY"), comments, StreamSort.Ascending);
        // Ascending: Description, then the undated comment (oldest), then the dated one.
        Assert.True(asc.IndexOf("DESCBODY", StringComparison.Ordinal) < asc.IndexOf("UND", StringComparison.Ordinal));
        Assert.True(asc.IndexOf("UND", StringComparison.Ordinal) < asc.IndexOf("DAT", StringComparison.Ordinal));

        var desc = TaskDetailFormatter.Stream(Sample(description: "DESCBODY"), comments, StreamSort.Descending);
        // Descending is the exact reverse: dated, then undated, then Description last.
        Assert.True(desc.IndexOf("DAT", StringComparison.Ordinal) < desc.IndexOf("UND", StringComparison.Ordinal));
        Assert.True(desc.IndexOf("UND", StringComparison.Ordinal) < desc.IndexOf("DESCBODY", StringComparison.Ordinal));
    }

    [Fact]
    public void Stream_SeparatesEveryBlockWithTheSharedRule()
    {
        // Description + 3 comments = 4 blocks → exactly 3 separators, never leading/trailing.
        var text = TaskDetailFormatter.Stream(Sample(), ScrambledDatedComments(), StreamSort.Ascending);

        var count = text.Split(TaskDetailFormatter.CommentSeparator).Length - 1;
        Assert.Equal(3, count);
        Assert.False(text.StartsWith(TaskDetailFormatter.CommentSeparator, StringComparison.Ordinal));
        Assert.False(text.TrimEnd('\n').EndsWith(TaskDetailFormatter.CommentSeparator, StringComparison.Ordinal));
    }

    [Fact]
    public void Stream_NoComments_ShowsOnlyDescriptionWithNoSeparator()
    {
        var text = TaskDetailFormatter.Stream(Sample(description: "DESCBODY"), [], StreamSort.Ascending);

        Assert.StartsWith("Description", text);
        Assert.Contains("DESCBODY", text);
        Assert.DoesNotContain(TaskDetailFormatter.CommentSeparator, text);
    }

    [Fact]
    public void Stream_NoDescription_ShowsPlaceholderBlock()
    {
        var text = TaskDetailFormatter.Stream(Sample(description: null), [], StreamSort.Ascending);
        Assert.Contains("(no description)", text);
    }

    [Fact]
    public void Stream_CommentBlocks_MatchTheCommentsTabShape()
    {
        // The Stream reuses the same block shape as the Comments tab, so a single comment renders
        // byte-for-byte the same body there as in Comments (author/date/resolved header + body).
        CommentItem[] one = [new("1", "ben", DateMs: 1000, Text: "hello", Resolved: true)];
        var commentsBody = TaskDetailFormatter.Comments(one);
        var stream = TaskDetailFormatter.Stream(Sample(description: "DESCBODY"), one, StreamSort.Ascending);
        Assert.Contains(commentsBody, stream);
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

    // ── Header attribute lines / colouring (issue #66) ───────────────────────

    [Fact]
    public void HeaderAttributeLines_ColorsPriorityAndStatusValues()
    {
        var lines = TaskDetailFormatter.HeaderAttributeLines(
            Sample(statusColor: "#00ff00", priorityColor: "#ff0000"));

        var priority = lines.Single(l => l.Text.StartsWith("Priority:"));
        // The label run is uncoloured; only the trailing value run carries the priority colour.
        Assert.Equal("high", priority.Runs[^1].Text);
        Assert.Equal("#ff0000", priority.Runs[^1].Color);
        Assert.Null(priority.Runs[0].Color);

        var status = lines.Single(l => l.Text.StartsWith("Status:"));
        Assert.Equal("in progress", status.Runs[^1].Text);
        Assert.Equal("#00ff00", status.Runs[^1].Color);
        Assert.Null(status.Runs[0].Color);
    }

    [Fact]
    public void HeaderAttributeLines_OnlyStatusAndPriorityValuesAreColoured()
    {
        var lines = TaskDetailFormatter.HeaderAttributeLines(
            Sample(statusColor: "#00ff00", priorityColor: "#ff0000"));

        foreach (var line in lines)
            foreach (var run in line.Runs)
                if (run.Color is not null)
                    Assert.Contains(run.Text, new[] { "high", "in progress" });
    }

    [Fact]
    public void HeaderAttributeLines_AbsentValues_AreNotColoured()
    {
        // Blank status/priority render as the em-dash placeholder, which is never badged even if a
        // colour is somehow present.
        var lines = TaskDetailFormatter.HeaderAttributeLines(
            Sample(statusName: null, priority: null, statusColor: "#00ff00", priorityColor: "#ff0000"));

        var priority = lines.Single(l => l.Text.StartsWith("Priority:"));
        Assert.Equal("—", priority.Runs[^1].Text);
        Assert.Null(priority.Runs[^1].Color);

        var status = lines.Single(l => l.Text.StartsWith("Status:"));
        Assert.Equal("—", status.Runs[^1].Text);
        Assert.Null(status.Runs[^1].Color);
    }

    [Fact]
    public void HeaderAttributeLines_MultipleLists_RendersUncolouredListsLine()
    {
        var lines = TaskDetailFormatter.HeaderAttributeLines(
            Sample(listId: "L1", lists: [new NamedEntity("L2", "Engineering"), new NamedEntity("L3", "Q3 Launch")]));

        var listsLine = lines.Single(l => l.Text.StartsWith("Lists:"));
        Assert.Contains("Personal Tasks, Engineering, Q3 Launch", listsLine.Text);
        // The multi-list membership line is never badged.
        Assert.All(listsLine.Runs, r => Assert.Null(r.Color));
    }

    [Fact]
    public void OtherAttributes_EqualsHeaderLinesPlusCustomFieldsBody()
    {
        // Guards the refactor: the plain string and the coloured view are built from the same pieces.
        var task = Sample(customFields: [new("Sprint", "drop_down")]);
        var expected = string.Join("\n", TaskDetailFormatter.HeaderAttributeLines(task).Select(l => l.Text))
            + "\n\n" + TaskDetailFormatter.CustomFieldsBody(task);

        Assert.Equal(expected, TaskDetailFormatter.OtherAttributes(task));
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
    public void CustomFieldValue_Labels_EmptyArray_RendersNoValue()
    {
        // No labels selected → empty string, which OtherAttributes omits (not a literal "[]").
        Assert.Equal("", TaskDetailFormatter.CustomFieldValue(Field("labels", "[]")));
    }

    [Fact]
    public void CustomFieldValue_Users_EmptyArray_RendersNoValue()
    {
        Assert.Equal("", TaskDetailFormatter.CustomFieldValue(Field("users", "[]")));
    }

    [Fact]
    public void CustomFieldValue_Location_UsesCompactFallback()
    {
        // A location value is an object; it renders as compact single-line JSON, not raw "text".
        var f = Field("location", "{\"formatted_address\":\"123 Main St\"}");
        var rendered = TaskDetailFormatter.CustomFieldValue(f)!;
        Assert.Contains("123 Main St", rendered);
        Assert.DoesNotContain("\n", rendered);
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
    public void OtherAttributes_MultipleLists_DedupesHomeEchoedByNameWithoutId()
    {
        // ClickUp reliably returns a list's name but not always its id; MapDetail maps a missing id
        // to "". A location echoing the home list by name only must still collapse to one entry.
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(listId: "L1", lists: [new NamedEntity("", "Personal Tasks"), new NamedEntity("L2", "Engineering")]));
        Assert.Contains("Lists:         Personal Tasks, Engineering", text);
        Assert.DoesNotContain("Personal Tasks, Personal Tasks", text);
    }

    [Fact]
    public void OtherAttributes_MultipleLists_IgnoresBlankNamedLocations()
    {
        var text = TaskDetailFormatter.OtherAttributes(
            Sample(listId: "L1", lists: [new NamedEntity("L2", "   "), new NamedEntity("L3", "Engineering")]));
        Assert.Contains("Lists:         Personal Tasks, Engineering", text);
    }
}
