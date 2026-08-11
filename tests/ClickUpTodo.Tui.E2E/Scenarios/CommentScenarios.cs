using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>Threaded comments (#329, E2E_THREADS=1): marks the middle comment (<c>c2</c>) with a two-reply
/// thread and serves <c>GET /comment/c2/reply</c>, so the real <c>CommentThreadLoader</c> fetches its replies
/// and the detail view renders them nested. Off by default (reply_count "0"), so every existing scenario sees
/// the same flat three comments. Overrides the comments GET (to bump the count) and adds the reply route
/// (contributed only here — it is never fetched with threads off).</summary>
internal sealed class ThreadsScenario : IE2EScenario
{
    public string Name => "threads";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_THREADS") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "task/{id}/comment", (_, p, _, _) =>
        {
            var taskId = FakeClickUp.TaskIdOfComment(p);
            var node = JsonNode.Parse(FakeClickUp.CommentsJson(taskId))!;
            foreach (var c in node["comments"]!.AsArray())
                if (c!["id"]?.GetValue<string>() == "c2")
                    c["reply_count"] = "2";
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),
        new(HttpMethod.Get, "comment/{id}/reply", (_, p, _, _) =>
            FakeClickUp.OkAsync(RepliesJson(FakeClickUp.CommentIdOfReply(p))), 1),
    ];

    /// <summary>The reply thread for a comment: two replies for the seeded thread parent (c2), empty for any
    /// other comment. A <c>CommentsResponse</c>-shaped payload, exactly like the flat comment list.</summary>
    private static string RepliesJson(string commentId)
    {
        if (commentId != "c2")
            return JsonSerializer.Serialize(new { comments = Array.Empty<object>() });
        var replies = new[]
        {
            new { id = "c2r1", comment_text = "Reply one: thanks — taking a look now.", user = new { username = "Alex Kim" }, date = "1751481000000", resolved = false },
            new { id = "c2r2", comment_text = "Reply two: confirmed fixed ✅", user = new { username = "Ben Seymour" }, date = "1751482000000", resolved = false },
        };
        return JsonSerializer.Serialize(new { comments = replies });
    }
}

/// <summary>A deterministic tall comment tail (#468, E2E_LONG_STREAM=1) so the Stream overflows by well over
/// a page — the geometry the page-scroll composition check needs. Fixed count + fixed text + fixed dates, so
/// it is stable across refreshes. Overrides the comments GET, appending the filler to the default set.</summary>
internal sealed class LongStreamScenario : IE2EScenario
{
    public string Name => "long-stream";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_LONG_STREAM") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "task/{id}/comment", (_, p, _, _) =>
        {
            var taskId = FakeClickUp.TaskIdOfComment(p);
            if (taskId == "tclosed")
                return FakeClickUp.OkAsync(FakeClickUp.CommentsJson(taskId));
            var node = JsonNode.Parse(FakeClickUp.CommentsJson(taskId))!;
            var comments = node["comments"]!.AsArray();
            for (var i = 1; i <= 40; i++)
                comments.Add(new JsonObject
                {
                    ["id"] = $"ls{i}",
                    ["comment_text"] = $"Filler stream line {i:D2} — deterministic content for the #468 page-scroll composition check.",
                    ["user"] = new JsonObject { ["username"] = "Filler Bot" },
                    ["date"] = $"{1751490100000L + i}",
                    ["resolved"] = false,
                });
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),
    ];
}

/// <summary>A growing comment tail (E2E_VARY_COMMENTS=1): each successive fetch returns one more comment — the
/// only way to exercise the detail view's <em>content-changed</em> refresh path (scroll preservation).
/// Overrides the comments GET, appending a per-fetch-count tail to the default set.</summary>
internal sealed class VaryCommentsScenario : IE2EScenario
{
    private int _fetches;

    public string Name => "vary-comments";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_VARY_COMMENTS") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "task/{id}/comment", (_, p, _, _) =>
        {
            var taskId = FakeClickUp.TaskIdOfComment(p);
            if (taskId == "tclosed")
                return FakeClickUp.OkAsync(FakeClickUp.CommentsJson(taskId));
            var node = JsonNode.Parse(FakeClickUp.CommentsJson(taskId))!;
            var comments = node["comments"]!.AsArray();
            var seq = System.Threading.Interlocked.Increment(ref _fetches);
            for (var i = 1; i <= seq; i++)
                comments.Add(new JsonObject
                {
                    ["id"] = $"e{i}",
                    ["comment_text"] = $"Auto-refresh probe comment {i}",
                    ["user"] = new JsonObject { ["username"] = "Probe Bot" },
                    ["date"] = $"{1751490000000L + i}",
                    ["resolved"] = false,
                });
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),
    ];
}
