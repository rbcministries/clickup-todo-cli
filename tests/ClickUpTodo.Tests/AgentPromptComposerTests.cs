using System.Text.Json;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the agent-dispatch prompt composer (issue #24). Pure context-shaping and a thin
/// temp-file writer — fully exercised in CI with no ClickUp API.
/// </summary>
public sealed class AgentPromptComposerTests
{
    private static TaskDetail Task(
        string id = "abc123",
        string? customId = null,
        string name = "Ship the Q3 report",
        string? status = "in progress",
        string? listId = "L1",
        string? listName = "Personal Tasks",
        string? url = "https://app.clickup.com/t/abc123",
        long? dueMs = 1_700_000_000_000,
        string? priority = "high",
        string? description = "Write it up.",
        IReadOnlyList<string>? assignees = null,
        IReadOnlyList<string>? tags = null)
        => new()
        {
            Id = id,
            CustomId = customId,
            Name = name,
            StatusName = status,
            ListId = listId,
            ListName = listName,
            Url = url,
            DueDateMs = dueMs,
            Priority = priority,
            Description = description,
            Assignees = assignees ?? ["Ben", "Sam"],
            Tags = tags ?? ["report", "q3"],
        };

    private static CommentItem Comment(
        string id = "c1", string author = "Ben", long? dateMs = 1_699_000_000_000,
        string text = "Looks good.", bool resolved = false)
        => new(id, author, dateMs, text, resolved);

    /// <summary>Parses the JSON object that follows the preamble in a composed prompt.</summary>
    private static JsonElement PayloadOf(string composed)
    {
        var brace = composed.IndexOf('{');
        using var doc = JsonDocument.Parse(composed[brace..]);
        return doc.RootElement.Clone();
    }

    private static JsonElement TaskOf(TaskDetail t, IReadOnlyList<CommentItem>? comments = null)
    {
        using var doc = JsonDocument.Parse(AgentPromptComposer.BuildJson(t, comments ?? []));
        return doc.RootElement.Clone().GetProperty("task");
    }

    // ── Layout ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Compose_LaysOutPromptThenPreambleThenJson()
    {
        var composed = AgentPromptComposer.Compose(Task(), [Comment()], "Please triage this.");

        Assert.StartsWith($"Please triage this.\n\n{AgentPromptComposer.Preamble}\n\n{{", composed);
        // The tail parses as a single JSON object with task + comments.
        var payload = PayloadOf(composed);
        Assert.Equal(JsonValueKind.Object, payload.GetProperty("task").ValueKind);
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("comments").ValueKind);
    }

    [Fact]
    public void Compose_TrimsUserPrompt()
    {
        var composed = AgentPromptComposer.Compose(Task(), [], "   hello   ");
        Assert.StartsWith($"hello\n\n{AgentPromptComposer.Preamble}", composed);
    }

    [Fact]
    public void Compose_EmptyPrompt_StillEmitsPreambleAndJson()
    {
        var composed = AgentPromptComposer.Compose(Task(), [], "");
        Assert.StartsWith($"\n\n{AgentPromptComposer.Preamble}\n\n{{", composed);
    }

    // ── template model (#100) ─────────────────────────────────────────────────────

    [Fact]
    public void DefaultTemplate_RendersIdenticalToLegacyLayout()
    {
        // Regression guard: the default template must reproduce the pre-#100 output byte-for-byte
        // (trimmed prompt · blank · fixed preamble · blank · combined JSON).
        var task = Task();
        var comments = new[] { Comment() };

        var composed = AgentPromptComposer.Compose(task, comments, "  triage  ");

        var expected = $"triage\n\n{AgentPromptComposer.Preamble}\n\n{AgentPromptComposer.BuildJson(task, comments)}";
        Assert.Equal(expected, composed);
    }

    [Fact]
    public void Compose_CustomTemplate_OverridesTheDefault()
    {
        var composed = AgentPromptComposer.Compose(Task(), [], "triage", template: "LEAD: {userPrompt}!");

        Assert.Equal("LEAD: triage!", composed);
        Assert.DoesNotContain(AgentPromptComposer.Preamble, composed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_BlankTemplate_FallsBackToTheDefault(string? template)
    {
        var composed = AgentPromptComposer.Compose(Task(), [], "triage", template);
        Assert.StartsWith($"triage\n\n{AgentPromptComposer.Preamble}\n\n{{", composed);
    }

    [Fact]
    public void Compose_SubstitutesScalarPlaceholders()
    {
        var task = Task(id: "abc123", customId: "TEAM-42");
        var composed = AgentPromptComposer.Compose(
            task, [], "  go  ", template: "p={userPrompt} id={taskId} cid={customId}");

        Assert.Equal("p=go id=abc123 cid=TEAM-42", composed);
    }

    [Fact]
    public void Compose_CustomId_FallsBackToTaskId_WhenAbsent()
    {
        var composed = AgentPromptComposer.Compose(Task(id: "abc123", customId: null), [], "go", template: "{customId}");
        Assert.Equal("abc123", composed);
    }

    [Fact]
    public void Compose_TaskAndCommentsJsonPlaceholders_MatchTheirBuilders()
    {
        var task = Task();
        var comments = new[] { Comment() };

        var composed = AgentPromptComposer.Compose(task, comments, "go", template: "T:{taskJson}\nC:{commentsJson}");

        Assert.Equal(
            $"T:{AgentPromptComposer.BuildTaskJson(task)}\nC:{AgentPromptComposer.BuildCommentsJson(comments)}",
            composed);
    }

    [Fact]
    public void Compose_ContextJsonPlaceholder_MatchesBuildJson()
    {
        var task = Task();
        var comments = new[] { Comment() };

        var composed = AgentPromptComposer.Compose(task, comments, "go", template: "{contextJson}");
        Assert.Equal(AgentPromptComposer.BuildJson(task, comments), composed);
    }

    [Fact]
    public void Compose_ToggleInstructionPlaceholders_RenderEmpty_UntilTheirTogglesLand()
    {
        // #97/#98 supply these; until then they expand to empty so a template referencing them is inert.
        var composed = AgentPromptComposer.Compose(
            Task(), [], "go", template: "[{postCommentInstruction}][{outputDirInstruction}]");
        Assert.Equal("[][]", composed);
    }

    [Fact]
    public void BuildTaskJson_MatchesTheTaskObjectInBuildJson()
    {
        // The standalone task JSON must be the same object as the "task" nested in BuildJson — compared
        // canonically (compact) since the nested copy carries deeper indentation from WriteIndented.
        var task = Task();
        using var whole = JsonDocument.Parse(AgentPromptComposer.BuildJson(task, []));
        using var standalone = JsonDocument.Parse(AgentPromptComposer.BuildTaskJson(task));

        Assert.Equal(
            JsonSerializer.Serialize(whole.RootElement.GetProperty("task")),
            JsonSerializer.Serialize(standalone.RootElement));
    }

    [Fact]
    public void BuildCommentsJson_MatchesTheCommentsArrayInBuildJson()
    {
        // Same drift guard for the comments element shape (compared canonically for indentation).
        var task = Task();
        var comments = new[] { Comment(id: "c1"), Comment(id: "c2", author: "Sam", resolved: true) };
        using var whole = JsonDocument.Parse(AgentPromptComposer.BuildJson(task, comments));
        using var standalone = JsonDocument.Parse(AgentPromptComposer.BuildCommentsJson(comments));

        Assert.Equal(
            JsonSerializer.Serialize(whole.RootElement.GetProperty("comments")),
            JsonSerializer.Serialize(standalone.RootElement));
    }

    [Fact]
    public void WritePromptFile_HonorsCustomTemplate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = AgentPromptComposer.WritePromptFile(Task(), [], "triage", dir, template: "LEAD: {userPrompt}");
            Assert.Equal("LEAD: triage", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ── template rendering (#100) ──────────────────────────────────────────────────

    [Fact]
    public void Render_KnownPlaceholder_IsSubstituted()
        => Assert.Equal("hi X", AgentPromptComposer.Render("hi {a}", new Dictionary<string, string> { ["a"] = "X" }));

    [Fact]
    public void Render_UnknownPlaceholder_IsLeftLiteral()
        => Assert.Equal("hi {b}", AgentPromptComposer.Render("hi {b}", new Dictionary<string, string> { ["a"] = "X" }));

    [Fact]
    public void Render_DoubledBraces_EscapeToLiteralBraces()
        => Assert.Equal("{a} {X}",
            AgentPromptComposer.Render("{{a}} {{{a}}}", new Dictionary<string, string> { ["a"] = "X" }));

    [Fact]
    public void Render_LoneBraces_AreLiteral()
        => Assert.Equal("a { b } c",
            AgentPromptComposer.Render("a { b } c", new Dictionary<string, string> { ["b"] = "NO" }));

    [Fact]
    public void Render_SubstitutedValue_IsNotRescanned()
    {
        // A value that itself looks like a placeholder must not be re-substituted.
        var values = new Dictionary<string, string> { ["a"] = "{b}", ["b"] = "SHOULD-NOT-APPEAR" };
        Assert.Equal("{b}", AgentPromptComposer.Render("{a}", values));
    }

    [Fact]
    public void Render_LoneBrace_DoesNotSwallowAFollowingPlaceholder()
    {
        // A lone '{' must not capture forward to a later placeholder's '}' — the real {a} still expands.
        var values = new Dictionary<string, string> { ["a"] = "X" };
        Assert.Equal("the set { a, b and X", AgentPromptComposer.Render("the set { a, b and {a}", values));
        Assert.Equal("x { y X z", AgentPromptComposer.Render("x { y {a} z", values));
    }

    [Fact]
    public void Render_TokenSpanningANewline_IsNotTreatedAsAPlaceholder()
    {
        // A '{' whose matching '}' is on another line isn't a token; it stays literal and later tokens work.
        var values = new Dictionary<string, string> { ["a"] = "X" };
        Assert.Equal("{ not\na token } X=X", AgentPromptComposer.Render("{ not\na token } {a}=X", values));
    }

    [Fact]
    public void Placeholders_CoverEveryTokenTheDefaultTemplateUses()
    {
        var known = AgentPromptComposer.Placeholders.Select(p => p.Name).ToHashSet();
        Assert.Contains("userPrompt", known);
        Assert.Contains("contextJson", known);
    }

    // ── #27 → #100 preamble migration helper ────────────────────────────────────────

    [Fact]
    public void DefaultTemplateWithPreamble_SwapsThePreambleLine()
    {
        var template = AgentPromptComposer.DefaultTemplateWithPreamble("  Only use the JSON.  ");

        Assert.Equal("{userPrompt}\n\nOnly use the JSON.\n\n{contextJson}", template);
        Assert.DoesNotContain(AgentPromptComposer.Preamble, template);
        // Renders like the old custom-preamble output.
        var composed = AgentPromptComposer.Compose(Task(), [], "go", template);
        Assert.StartsWith("go\n\nOnly use the JSON.\n\n{", composed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultTemplateWithPreamble_BlankValue_YieldsTheUnchangedDefault(string? preamble)
        => Assert.Equal(AgentPromptComposer.DefaultTemplate, AgentPromptComposer.DefaultTemplateWithPreamble(preamble));

    // ── task subset ──────────────────────────────────────────────────────────────

    [Fact]
    public void Task_MapsCoreFields()
    {
        var t = TaskOf(Task());

        Assert.Equal("abc123", t.GetProperty("id").GetString());
        Assert.Equal("Ship the Q3 report", t.GetProperty("name").GetString());
        Assert.Equal("in progress", t.GetProperty("status").GetString());
        Assert.Equal("https://app.clickup.com/t/abc123", t.GetProperty("url").GetString());
        Assert.Equal("high", t.GetProperty("priority").GetString());
        Assert.Equal(1_700_000_000_000, t.GetProperty("due_date").GetInt64());
        Assert.Equal("L1", t.GetProperty("list").GetProperty("id").GetString());
        Assert.Equal("Personal Tasks", t.GetProperty("list").GetProperty("name").GetString());
    }

    [Fact]
    public void Task_CustomId_OmittedWhenNull_PresentWhenSet()
    {
        Assert.False(TaskOf(Task(customId: null)).TryGetProperty("custom_id", out _));
        Assert.Equal("TEAM-42", TaskOf(Task(customId: "TEAM-42")).GetProperty("custom_id").GetString());
    }

    [Fact]
    public void Task_NullScalars_AreOmitted()
    {
        var t = TaskOf(Task(status: null, url: null, priority: null, dueMs: null));

        Assert.False(t.TryGetProperty("status", out _));
        Assert.False(t.TryGetProperty("url", out _));
        Assert.False(t.TryGetProperty("priority", out _));
        Assert.False(t.TryGetProperty("due_date", out _));
    }

    [Fact]
    public void Task_List_OmittedWhenBothIdAndNameNull()
    {
        var t = TaskOf(Task(listId: null, listName: null));
        Assert.False(t.TryGetProperty("list", out _));
    }

    [Fact]
    public void Task_AssigneesAndTags_AreArrays_IncludingEmpty()
    {
        var t = TaskOf(Task(assignees: ["Ben", "Sam"], tags: []));

        Assert.Equal(["Ben", "Sam"], t.GetProperty("assignees").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.Array, t.GetProperty("tags").ValueKind);
        Assert.Empty(t.GetProperty("tags").EnumerateArray());
    }

    // ── description truncation ─────────────────────────────────────────────────

    [Fact]
    public void Description_ShortValue_KeptVerbatim()
        => Assert.Equal("Write it up.", TaskOf(Task(description: "Write it up.")).GetProperty("description").GetString());

    [Fact]
    public void Description_OverLimit_IsTruncatedWithEllipsis()
    {
        var big = new string('x', AgentPromptComposer.MaxDescriptionLength + 50);

        var desc = TaskOf(Task(description: big)).GetProperty("description").GetString()!;

        Assert.Equal(AgentPromptComposer.MaxDescriptionLength + 1, desc.Length); // max chars + the ellipsis
        Assert.EndsWith("…", desc);
        Assert.StartsWith(new string('x', AgentPromptComposer.MaxDescriptionLength), desc);
    }

    [Fact]
    public void Description_EmptyOrNull_IsOmitted()
    {
        Assert.False(TaskOf(Task(description: null)).TryGetProperty("description", out _));
        Assert.False(TaskOf(Task(description: "")).TryGetProperty("description", out _));
    }

    // ── comments ───────────────────────────────────────────────────────────────

    [Fact]
    public void Comments_CarryFullObjects()
    {
        using var doc = JsonDocument.Parse(
            AgentPromptComposer.BuildJson(Task(), [Comment(id: "c9", author: "Sam", dateMs: 123, text: "Done", resolved: true)]));
        var c = doc.RootElement.GetProperty("comments")[0];

        Assert.Equal("c9", c.GetProperty("id").GetString());
        Assert.Equal("Sam", c.GetProperty("author").GetString());
        Assert.Equal(123, c.GetProperty("date").GetInt64());
        Assert.Equal("Done", c.GetProperty("text").GetString());
        Assert.True(c.GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public void Comments_EmptyList_YieldsEmptyArray()
    {
        using var doc = JsonDocument.Parse(AgentPromptComposer.BuildJson(Task(), []));
        Assert.Empty(doc.RootElement.GetProperty("comments").EnumerateArray());
    }

    // ── escaping / safety ──────────────────────────────────────────────────────

    [Fact]
    public void SpecialCharacters_RoundTripThroughValidJson()
    {
        var t = Task(name: "Fix \"quote\" & <tag>", description: "line1\nline2 \"q\"");
        var comments = new[] { Comment(text: "He said \"hi\"\nbye") };

        // Whole composed prompt's payload still parses, and values survive intact.
        var payload = PayloadOf(AgentPromptComposer.Compose(t, comments, "p"));

        Assert.Equal("Fix \"quote\" & <tag>", payload.GetProperty("task").GetProperty("name").GetString());
        Assert.Equal("line1\nline2 \"q\"", payload.GetProperty("task").GetProperty("description").GetString());
        Assert.Equal("He said \"hi\"\nbye", payload.GetProperty("comments")[0].GetProperty("text").GetString());
    }

    // ── file writer ──────────────────────────────────────────────────────────────

    [Fact]
    public void WritePromptFile_WritesComposedContent_AndCreatesDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = AgentPromptComposer.WritePromptFile(Task(), [Comment()], "triage", dir);

            Assert.True(File.Exists(path));
            Assert.StartsWith(dir, path);
            Assert.Equal(AgentPromptComposer.Compose(Task(), [Comment()], "triage"), File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WritePromptFile_ProducesUniquePaths_AcrossCalls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var a = AgentPromptComposer.WritePromptFile(Task(), [], "x", dir);
            var b = AgentPromptComposer.WritePromptFile(Task(), [], "x", dir);
            Assert.NotEqual(a, b);
            Assert.True(File.Exists(a) && File.Exists(b));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WritePromptFile_DefaultDirectory_IsUnderTempClickUpTodo()
    {
        // Exercise the production default (directory: null) — writes under <temp>/clickup-todo.
        var path = AgentPromptComposer.WritePromptFile(Task(), [], "x");
        try
        {
            Assert.Equal(Path.Combine(Path.GetTempPath(), "clickup-todo"), Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path); // leave the shared temp dir itself in place
        }
    }

    [Fact]
    public void WritePromptFile_SanitizesTaskId_NoPathTraversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            // A hostile id with separators + traversal must not escape the target directory.
            var path = AgentPromptComposer.WritePromptFile(Task(id: "../../etc/p w?d"), [], "x", dir);

            Assert.Equal(dir, Path.GetDirectoryName(path));
            var name = Path.GetFileName(path);
            Assert.DoesNotContain("..", name);
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ── guards / defensive ──────────────────────────────────────────────────────

    [Fact]
    public void NullTask_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AgentPromptComposer.Compose(null!, [], "p"));
        Assert.Throws<ArgumentNullException>(() => AgentPromptComposer.WritePromptFile(null!, [], "p"));
    }

    [Fact]
    public void NullComments_TreatedAsEmptyArray()
    {
        using var doc = JsonDocument.Parse(AgentPromptComposer.BuildJson(Task(), null!));
        Assert.Empty(doc.RootElement.GetProperty("comments").EnumerateArray());
    }

    [Fact]
    public void Description_TruncationDoesNotSplitSurrogatePair()
    {
        const int max = AgentPromptComposer.MaxDescriptionLength;
        // Place a 2-code-unit emoji so the naive cut at `max` would land mid-surrogate.
        var value = new string('x', max - 1) + "😀" + new string('y', 5);

        var desc = TaskOf(Task(description: value)).GetProperty("description").GetString()!;

        Assert.Equal(new string('x', max - 1) + "…", desc); // stepped back off the high surrogate
        Assert.DoesNotContain('�', desc);              // no replacement char artifact
    }
}
