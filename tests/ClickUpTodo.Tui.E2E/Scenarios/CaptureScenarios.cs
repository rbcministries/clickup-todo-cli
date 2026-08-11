namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>#395: when E2E_CAPTURE_FILE is set, write the outgoing create-task request body to that file so a
/// check can assert the <c>custom_fields</c> array actually reached the POST (a regression that dropped it
/// would leave the file without the values). Overrides <c>POST /list/{id}/task</c> to capture, then returns
/// the same create echo.</summary>
internal sealed class CaptureFileScenario : IE2EScenario
{
    public string Name => "capture-file";
    public bool IsActive => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_CAPTURE_FILE"));

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Post, "list/{id}/task", async (req, _, _, ct) =>
        {
            if (req.Content is not null
                && Environment.GetEnvironmentVariable("E2E_CAPTURE_FILE") is { Length: > 0 } capturePath)
            {
                var requestBody = await req.Content.ReadAsStringAsync(ct);
                try { File.WriteAllText(capturePath, requestBody); } catch { /* best-effort capture */ }
            }
            return FakeClickUp.Ok(FakeClickUp.CreateTaskEchoBody);
        }, 1),
    ];
}

/// <summary>#325: when E2E_COMMENT_LOG is set, record the raw create-comment request body (one per line) so a
/// check can assert the structured <c>comment</c> blocks array — an @-mention tag,
/// <c>{"type":"tag","user":{"id":…}}</c> — was actually sent. Overrides <c>POST /task/{id}/comment</c> to
/// record, then returns the same create echo.</summary>
internal sealed class CommentLogScenario : IE2EScenario
{
    public string Name => "comment-log";
    public bool IsActive => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_COMMENT_LOG"));

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Post, "task/{id}/comment", async (req, _, _, ct) =>
        {
            if (Environment.GetEnvironmentVariable("E2E_COMMENT_LOG") is { Length: > 0 } log
                && req.Content is { } content)
                File.AppendAllText(log, await content.ReadAsStringAsync(ct) + "\n");
            return FakeClickUp.Ok(FakeClickUp.CreateCommentEchoBody);
        }, 1),
    ];
}

/// <summary>#330: when E2E_REPLY_LOG is set, record a posted reply — its target comment id and the raw body,
/// one <c>{commentId}\t{body}</c> line per POST — so a reply-post check can assert the write reached the
/// backend keyed to the picked parent. Overrides <c>POST /comment/{id}/reply</c> to record, then returns the
/// same create echo.</summary>
internal sealed class ReplyLogScenario : IE2EScenario
{
    public string Name => "reply-log";
    public bool IsActive => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_REPLY_LOG"));

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Post, "comment/{id}/reply", async (req, path, _, ct) =>
        {
            var reqBody = req.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
            if (Environment.GetEnvironmentVariable("E2E_REPLY_LOG") is { Length: > 0 } log)
            {
                var commentId = FakeClickUp.CommentIdOfReply(path);
                try
                {
                    File.AppendAllText(log,
                        commentId + "\t" + reqBody.Replace('\n', ' ').Replace('\r', ' ') + "\n");
                }
                catch { /* best-effort capture */ }
            }
            return FakeClickUp.Ok(FakeClickUp.CreateReplyEchoBody);
        }, 1),
    ];
}
