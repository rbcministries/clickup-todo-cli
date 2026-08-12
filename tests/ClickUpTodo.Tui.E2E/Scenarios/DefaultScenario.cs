using System.Net.Http;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// The always-on backend: the 200-task generator, the workspace shape (statuses / members / list names),
/// and the base detail / comment / list builders, registered as tier-0 routes. It is a real
/// <see cref="IE2EScenario"/> so the backend builds one uniform route list (default + active scenarios),
/// but it is <b>not</b> reflection-discovered — the backend constructs it directly and it is always active —
/// so it never appears in the fail-fast scenario listing and cannot be selected or deactivated. Opt-in
/// scenarios override its routes at tier 1, reusing the builders on <see cref="FakeClickUp"/> so their
/// responses stay byte-for-byte the default plus their patch.
/// </summary>
internal sealed class DefaultScenario : IE2EScenario
{
    public string Name => "default";
    public bool IsActive => true;

    public IEnumerable<Route<Handler>> Routes(FakeClickUp b) =>
    [
        new(HttpMethod.Get, "user", (_, _, _, _) =>
            FakeClickUp.OkAsync("""{"user":{"id":1,"username":"bench","email":"bench@example.com"}}""")),

        // POST/DELETE /v2/list/{listId}/task/{taskId} (#237): task↔list membership writes, consumed by the
        // Quick Updates List pane (#242). Suffix-anchored matching keeps this distinct from create-task
        // (POST .../list/{id}/task, no trailing id) with no ordering dependence.
        new(HttpMethod.Post, "list/{listId}/task/{taskId}", (req, path, _, _) => Membership(b, req, path)),
        new(HttpMethod.Delete, "list/{listId}/task/{taskId}", (req, path, _, _) => Membership(b, req, path)),

        // POST /task/{id}/comment (#216): create-comment echo returning the minimal created-comment shape.
        // The id comes back as a JSON *number* on create (the GET read path returns it as a string) (#144).
        // The #325 comment-log scenario overrides this to also record the posted body.
        new(HttpMethod.Post, "task/{id}/comment", (_, _, _, _) =>
            FakeClickUp.OkAsync(FakeClickUp.CreateCommentEchoBody)),
        new(HttpMethod.Get, "task/{id}/comment", (_, p, _, _) =>
            FakeClickUp.OkAsync(FakeClickUp.CommentsJson(FakeClickUp.TaskIdOfComment(p)))),

        // POST /comment/{comment_id}/reply (#330): create-reply echo (same minimal shape). The #330
        // reply-log scenario overrides this to record the target comment id + body. GET .../reply is
        // contributed only by the #329 threads scenario (no default: it is never fetched with threads off).
        new(HttpMethod.Post, "comment/{id}/reply", (_, _, _, _) =>
            FakeClickUp.OkAsync(FakeClickUp.CreateReplyEchoBody)),

        new(HttpMethod.Put, "task/{id}", (req, path, _, ct) => TaskPut(b, req, path, ct)),
        new(HttpMethod.Get, "task/{id}", (_, path, query, _) => TaskGet(b, path, query)),

        // GET /team/{id}/task: the default list snapshot. The #232 foreign, #376 nudge and #333 warm-closed
        // scenarios override this to substitute / patch the rows.
        new(HttpMethod.Get, "team/{id}/task", (_, _, query, _) =>
            FakeClickUp.OkAsync(b.TasksJson(FakeClickUp.PageOf(query), b.TaskCount, FakeClickUp.IncludeClosed(query)))),

        // POST /list/{id}/task (#209/#213): create-task echo. The #395 capture scenario overrides this to
        // also write the outgoing body to E2E_CAPTURE_FILE.
        new(HttpMethod.Post, "list/{id}/task", (_, _, _, _) =>
            FakeClickUp.OkAsync(FakeClickUp.CreateTaskEchoBody)),
        new(HttpMethod.Get, "list/{id}/task", (_, _, _, _) =>
            FakeClickUp.OkAsync("""{"tasks":[],"last_page":true}""")),
        // GET /list/{id}/field: no Custom Field definitions by default (so the New Task screen creates
        // directly). The #249/#395/#446 custom-field and #365 QU List-pane scenarios override this.
        new(HttpMethod.Get, "list/{id}/field", (_, _, _, _) =>
            FakeClickUp.OkAsync("""{"fields":[]}""")),
        new(HttpMethod.Get, "list/{id}", (_, p, _, _) => FakeClickUp.OkAsync(FakeClickUp.ListJson(p))),

        new(HttpMethod.Get, "team", (_, _, _, _) =>
            FakeClickUp.OkAsync($$"""{"teams":[{"id":"ws1","name":"Bench","members":[{{FakeClickUp.MembersJson()}}]}]}""")),
    ];

    /// <summary>Task↔list membership write (#242): POST adds, DELETE removes the additional location so a
    /// later detail GET reflects it; ClickUp echoes an empty body.</summary>
    private static Task<HttpResponseMessage> Membership(FakeClickUp b, HttpRequestMessage request, string path)
    {
        var listId = FakeClickUp.ListIdOfMembership(path);
        lock (b.Gate)
        {
            if (request.Method == HttpMethod.Post) b.Locations.Add(listId);
            else b.Locations.Remove(listId);
        }
        return FakeClickUp.OkAsync("{}");
    }

    /// <summary>The default /task/{id} PUT (#217/#545): an assignee add/remove, a description edit or a name
    /// rename mutates the shared state; either way echo the task reflecting the current state so the write
    /// response reconciles. Status/priority PUTs carry none of these, so they leave the state untouched and
    /// echo the current detail.</summary>
    private static async Task<HttpResponseMessage> TaskPut(FakeClickUp b, HttpRequestMessage request, string path, CancellationToken ct)
    {
        var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
        string body;
        lock (b.Gate)
        {
            b.ApplyAssigneeMutation(reqBody);
            b.ApplyDescriptionMutation(reqBody);
            b.ApplyNameMutation(reqBody);
            body = b.DetailJson(FakeClickUp.LastSegment(path));
        }
        return FakeClickUp.Ok(body);
    }

    /// <summary>The default /task/{id} GET: the detail read, with the two sentinel 404 paths — quick-open
    /// not-found (<c>tmissing</c>, #303) and the hyphenless custom-id fallback (<c>PROJ123</c> without
    /// <c>custom_task_ids=true</c>, #353).</summary>
    private static Task<HttpResponseMessage> TaskGet(FakeClickUp b, string path, string query)
    {
        var idSeg = FakeClickUp.LastSegment(path);
        if (FakeClickUp.TaskGetSentinel(idSeg, query) is { } notFound)
            return Task.FromResult(notFound);
        return Task.FromResult(FakeClickUp.Ok(b.DetailJson(idSeg)));
    }
}
