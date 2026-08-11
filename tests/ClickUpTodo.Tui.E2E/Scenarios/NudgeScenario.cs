using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// #376 (item 1) two-instance nudge scenario (E2E_NUDGE=1). A Quick Update committed in one app process must
/// be observable in the OTHER process's per-task GET (its nudge re-fetch, #295). The state is shared via a
/// file (E2E_SHARED_STATE) so it crosses the process boundary; only the status is modelled — the one field
/// this scenario drives — keyed by task id. Overrides the default task GET/PUT and patches the team-tasks
/// rows so a full resync (not just the per-task nudge re-fetch) reflects a committed cross-process change.
/// </summary>
internal sealed class NudgeScenario : IE2EScenario
{
    // A date_updated newer than the seeded rows' "1700000000000", so a committed status is strictly newer
    // than the version the other instance already holds. Without the bump the consumer's redundant-fetch
    // guard (held >= server) would suppress the nudge fetch and nothing would propagate.
    private const long UpdatedMs = 1800000000000L;

    public string Name => "nudge";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_NUDGE") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        // Full resync: reuse the default rows and overlay the committed cross-process status onto each.
        new(HttpMethod.Get, "team/{id}/task", (_, _, query, _) =>
        {
            var json = backend.TasksJson(FakeClickUp.PageOf(query), backend.TaskCount, FakeClickUp.IncludeClosed(query));
            var overlay = ReadOverlay();
            if (overlay.Count == 0)
                return FakeClickUp.OkAsync(json);
            var node = JsonNode.Parse(json)!;
            foreach (var t in node["tasks"]!.AsArray())
            {
                var id = t!["id"]!.GetValue<string>();
                if (overlay.TryGetValue(id, out var ov))
                {
                    t["status"]!["status"] = ov.Status;
                    t["status"]!["color"] = ov.Color;
                    t["date_updated"] = ov.Updated.ToString();
                }
            }
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),

        // The consumer's nudge re-fetch (#295): serve the overlaid status when the writer has committed one,
        // else the task's seeded default (with the original date).
        new(HttpMethod.Get, "task/{id}", (_, path, query, _) =>
        {
            var id = FakeClickUp.LastSegment(path);
            if (FakeClickUp.TaskGetSentinel(id, query) is { } notFound)
                return Task.FromResult(notFound);
            var overlay = ReadOverlay();
            return FakeClickUp.OkAsync(overlay.TryGetValue(id, out var o)
                ? TaskJson(id, o.Status, o.Color, o.Updated)
                : TaskJson(id, DefaultStatus(id), FakeClickUp.StatusColor(DefaultStatus(id)), 1700000000000L));
        }, 1),

        // Persist a committed status into the shared overlay (bumping date_updated so it's strictly newer)
        // and echo the task reflecting it — so the writer's own optimistic reconcile settles on the committed
        // value and the change-marker it records carries the newer server date_updated.
        new(HttpMethod.Put, "task/{id}", async (req, path, _, ct) =>
        {
            var reqBody = req.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
            return FakeClickUp.Ok(Put(path, reqBody));
        }, 1),
    ];

    private static string Put(string path, string requestBody)
    {
        var id = FakeClickUp.LastSegment(path);
        var overlay = ReadOverlay();
        string status, color;
        if (overlay.TryGetValue(id, out var cur))
            (status, color) = (cur.Status, cur.Color);
        else
            (status, color) = (DefaultStatus(id), FakeClickUp.StatusColor(DefaultStatus(id)));
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
            {
                status = st.GetString()!;
                color = FakeClickUp.StatusColor(status);
            }
        }
        catch (JsonException)
        {
            // A non-JSON / unexpected body isn't this fake's concern — keep the prior/default status.
        }
        WriteOverlay(id, new StatusOverlay { Status = status, Color = color, Updated = UpdatedMs });
        return TaskJson(id, status, color, UpdatedMs);
    }

    /// <summary>A full task object (the shape ClickUpClient.Map reads). Name / list / due date / priority
    /// mirror the default TasksJson for the same id, so the wholesale full-fidelity reconcile (#376 item 2)
    /// lands a row that differs from the seeded one only in the status chip.</summary>
    private static string TaskJson(string id, string status, string color, long updated)
    {
        var k = TaskIndex(id);
        var li = k % 3;
        var dueMs = DateTimeOffset.UtcNow.AddDays(k % 14).ToUnixTimeMilliseconds();
        var priority = k % 3 == 0 ? ",\"priority\":{\"priority\":\"high\",\"color\":\"#f50000\"}" : "";
        return $$"""
        {"id":"{{id}}","name":"Task {{k}} — follow up on the {{FakeClickUp.ListNames[li]}} item with a realistic title 📌","status":{"status":"{{status}}","color":"{{color}}"},"list":{"id":"{{FakeClickUp.Lists[li]}}","name":"{{FakeClickUp.ListNames[li]}}"},"due_date":"{{dueMs}}","date_updated":"{{updated}}","assignees":[]{{priority}},"url":"https://app.clickup.com/t/{{id}}"}
        """;
    }

    /// <summary>The seeded default status for a task id, matching the default TasksJson (<c>Statuses[k % 4]</c>).</summary>
    private static string DefaultStatus(string id) => FakeClickUp.Statuses[TaskIndex(id) % 4];

    /// <summary>The numeric index behind a <c>t{k}</c> id (0 when it doesn't parse).</summary>
    private static int TaskIndex(string id) => int.TryParse(id.TrimStart('t'), out var k) ? k : 0;

    private static string? SharedStatePath => Environment.GetEnvironmentVariable("E2E_SHARED_STATE");

    /// <summary>One task's overlaid status in the two-instance scenario.</summary>
    private sealed class StatusOverlay
    {
        public string Status { get; set; } = "";
        public string Color { get; set; } = "";
        public long Updated { get; set; }
    }

    /// <summary>Reads the shared status overlay. A missing/empty/torn file yields an empty map, so the reader
    /// falls back to each task's seeded default. Retries a transient IO race (a concurrent writer's atomic
    /// replace) a few times.</summary>
    private static Dictionary<string, StatusOverlay> ReadOverlay()
    {
        var path = SharedStatePath;
        if (string.IsNullOrEmpty(path))
            return new(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return new(StringComparer.Ordinal);
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new(StringComparer.Ordinal);
                return JsonSerializer.Deserialize<Dictionary<string, StatusOverlay>>(json)
                       ?? new(StringComparer.Ordinal);
            }
            catch (IOException) { Thread.Sleep(10); }
            catch (JsonException) { return new(StringComparer.Ordinal); }
        }
        return new(StringComparer.Ordinal);
    }

    /// <summary>Upserts one task's overlaid status via read-modify-write with an atomic replace (unique temp
    /// file + <see cref="File.Move(string, string, bool)"/>, atomic on POSIX), so a concurrent reader never
    /// sees a torn file. The scenario only ever commits in one instance, so this is a status mirror, not the
    /// multi-writer channel (that's the marker store).</summary>
    private static void WriteOverlay(string taskId, StatusOverlay entry)
    {
        var path = SharedStatePath;
        if (string.IsNullOrEmpty(path))
            return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var map = ReadOverlay();
                map[taskId] = entry;
                var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map));
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) { Thread.Sleep(10); }
        }
    }
}
