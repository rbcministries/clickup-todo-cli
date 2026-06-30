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
}
