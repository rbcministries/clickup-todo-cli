using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// #232 opt-in foreign / context-parent scenario (E2E_FOREIGN=1). A tiny deterministic snapshot that
/// exercises the #160 not-mine Quick Updates path: <c>pt1</c> — an assigned top-level task carrying a
/// teammate-owned foreign subtask <c>fs1</c> (#70); and <c>ct1</c> — an assigned task whose parent
/// <c>cp1</c> is ABSENT from the snapshot, so <c>cp1</c> is pulled in as a context-parent header (#46) with
/// <c>ct1</c> nested under it. The Status/Priority PUT is modelled (parsed, persisted, echoed) so a
/// committed write reads back — the gap #232 closes. Overrides the default task GET/PUT and team-tasks
/// routes; owns its own mutable status/priority maps (a background refresh can race a write).
/// </summary>
internal sealed class ForeignScenario : IE2EScenario
{
    private readonly object _gate = new();

    private readonly Dictionary<string, string> _status = new(StringComparer.Ordinal)
    {
        ["pt1"] = "to do",
        ["ct1"] = "to do",
        ["fs1"] = "to do",
        ["cp1"] = "in review",
    };
    private readonly Dictionary<string, int?> _priority = new(StringComparer.Ordinal);

    public string Name => "foreign";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_FOREIGN") == "1";

    // Both row kinds (context parent, foreign subtask) only exist while the subtasks view is on — TodoApp
    // gates the context-parent fetch on ShowSubtasks and the foreign-subtask fetch on Subtasks != Hidden —
    // so force the "all" state.
    public void Configure(AppConfig config) => config.View.Subtasks = SubtaskView.All;

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "team/{id}/task", (_, _, _, _) => FakeClickUp.OkAsync(TeamTasks()), 1),
        new(HttpMethod.Get, "task/{id}", (_, path, query, _) => FakeClickUp.OkAsync(TaskGet(path, query)), 1),
        new(HttpMethod.Put, "task/{id}", async (req, path, _, ct) =>
        {
            var reqBody = req.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
            return FakeClickUp.Ok(Put(path, reqBody));
        }, 1),
    ];

    /// <summary>The two-task assigned snapshot (page-agnostic — it fits one page).</summary>
    private string TeamTasks()
    {
        lock (_gate)
            return $"{{\"tasks\":[{TaskJson("pt1", includeSubtasks: false)},{TaskJson("ct1", includeSubtasks: false)}],\"last_page\":true}}";
    }

    /// <summary>A single task fetch: <c>?include_subtasks=true</c> (the per-parent foreign fetch) appends the
    /// owned subtask; a plain GET (the context-parent detail fetch) omits it.</summary>
    private string TaskGet(string path, string query)
    {
        var id = FakeClickUp.LastSegment(path);
        var includeSubtasks = query.Contains("include_subtasks=true", StringComparison.OrdinalIgnoreCase);
        lock (_gate)
            return TaskJson(id, includeSubtasks);
    }

    /// <summary>Applies a modelled Status/Priority write and echoes the task reflecting it.</summary>
    private string Put(string path, string requestBody)
    {
        var id = FakeClickUp.LastSegment(path);
        lock (_gate)
        {
            ApplyMutation(id, requestBody);
            return TaskJson(id, includeSubtasks: false);
        }
    }

    /// <summary>Parses a <c>{"status":"…"}</c> / <c>{"priority":n|null}</c> PUT body into the per-task
    /// override maps so the next read/echo reflects the committed value. Assumes <c>_gate</c> is held.</summary>
    private void ApplyMutation(string id, string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                _status[id] = st.GetString()!;
            // priority: an integer level sets it; an explicit null (ClickUp's "clear") resets it. Guard the
            // int read so a non-integer number can't throw past the JsonException catch below.
            if (root.TryGetProperty("priority", out var pr))
                _priority[id] = pr.ValueKind == JsonValueKind.Number && pr.TryGetInt32(out var level) ? level : null;
        }
        catch (JsonException)
        {
            // A non-JSON body isn't this fake's concern — leave the overrides untouched.
        }
    }

    /// <summary>Builds the task object for a known id, reflecting the current mutable status/priority.
    /// Assumes <c>_gate</c> is held. Only pt1 owns a subtask (fs1), appended when the caller opted in.</summary>
    private string TaskJson(string id, bool includeSubtasks)
    {
        var (name, parent, assignee) = id switch
        {
            "pt1" => ("Assigned parent — my task AA", (string?)null, ""),
            "ct1" => ("My nested subtask BB", "cp1", ""),
            "fs1" => ("Foreign teammate subtask ZZ", "pt1", "{\"id\":101,\"username\":\"Ada Lovelace\"}"),
            "cp1" => ("Context parent PP", (string?)null, "{\"id\":102,\"username\":\"Grace Hopper\"}"),
            _ => (id, (string?)null, ""),
        };
        var status = _status.TryGetValue(id, out var s) ? s : "to do";
        var parentField = parent is null ? "" : $",\"parent\":\"{parent}\"";
        // Echo the priority level as ClickUp does: id "1".."4" (which SetTaskPriorityAsync reads back via
        // ClickUpPriority.Level) plus the lowercase name, matching the real API and the default TasksJson.
        var priorityField = _priority.TryGetValue(id, out var lvl) && lvl is { } l
            ? $",\"priority\":{{\"id\":\"{l}\",\"priority\":\"{ClickUpPriority.NameFromLevel(l)?.ToLowerInvariant()}\",\"color\":\"#f50000\"}}"
            : "";
        var subtasksField = includeSubtasks && id == "pt1"
            ? $",\"subtasks\":[{TaskJson("fs1", includeSubtasks: false)}]"
            : "";
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"status":"{{status}}","color":"{{FakeClickUp.StatusColor(status)}}"},"list":{"id":"plist","name":"Personal Tasks"},"assignees":[{{assignee}}],"date_updated":"1700000000000","url":"https://app.clickup.com/t/{{id}}"{{parentField}}{{priorityField}}{{subtasksField}}}
        """;
    }
}
