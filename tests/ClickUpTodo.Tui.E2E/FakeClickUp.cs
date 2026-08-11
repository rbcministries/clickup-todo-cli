using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

/// <summary>Boot-time state for the harness's default backend (#487). Task count is the one knob the base
/// generator needs; scenario-specific state no longer lives here — E (#489) moved it into the scenario
/// files, discovered by reflection, so this type stopped being a shared append point.</summary>
internal sealed class HarnessContext
{
    /// <summary>Task count for the generated backend (E2E_TASKS; paging is exercised above 100).</summary>
    public int TaskCount { get; init; } = 200;
}

/// <summary>
/// The in-process fake ClickUp backend: a <see cref="HttpMessageHandler"/> that answers the app's REST
/// calls from canned data, with no sockets. It owns the <b>default</b> generated backend (the 200-task
/// list, statuses/members/list names, the base detail/comment builders) via <see cref="DefaultScenario"/>,
/// and the shared mutable state the default routes round-trip (the assignee set, additional list
/// memberships, the description). Opt-in scenarios layer over it: each active scenario contributes tier-1
/// routes (which override the default's tier-0 route for the same pattern) and may seed the shared state —
/// so the shared builders here stay scenario-free and a new scenario is one file (E, #489).
/// </summary>
internal sealed class FakeClickUp : HttpMessageHandler
{
    private readonly int _taskCount;

    /// <summary>Task count for the default generated backend (E2E_TASKS), read by the team-tasks route.</summary>
    internal int TaskCount => _taskCount;

    // The request dispatch table (#488): routes resolve by tier then specificity, not by source order, so a
    // scenario override (tier 1) cleanly shadows the default route (tier 0) and appending an endpoint can't
    // silently reorder another. Built once at construction — which is where a duplicate/ambiguous
    // registration fails loudly (see Routing.cs).
    private readonly RouteTable<RouteHandler> _routes;

    /// <summary>Builds the backend and its route table from the always-on <see cref="DefaultScenario"/> plus
    /// the given active scenarios: default routes at tier 0, each scenario's routes at tier 1, and each
    /// active scenario gets to seed the shared state first. An empty scenario list yields the pure default
    /// backend — the shape the <c>dotnet test</c> ambiguity assertion constructs.</summary>
    public FakeClickUp(HarnessContext ctx, IReadOnlyList<IE2EScenario>? scenarios = null)
    {
        _taskCount = ctx.TaskCount;
        scenarios ??= [];
        foreach (var s in scenarios)
            s.SeedBackend(this);

        var routes = new List<Route<RouteHandler>>();
        routes.AddRange(new DefaultScenario().Routes(this));
        foreach (var s in scenarios)
            routes.AddRange(s.Routes(this));
        _routes = new RouteTable<RouteHandler>(routes);
    }

    // ── Seeded workspace shape (shared across the default backend and reused by scenarios) ──────────────
    internal static readonly string[] Statuses = ["to do", "in progress", "blocked", "in review"];
    internal static readonly string[] StatusColors = ["#d3d3d3", "#4194f6", "#e50000", "#a875ff"];
    internal static readonly string[] Lists = ["plist", "list2", "list3"];
    internal static readonly string[] ListNames = ["Personal Tasks", "Q3 Website Refresh", "Ministry Ops"];

    // Workspace members feed the assignee-frequency pool (#155): the Quick Updates Assignees pane
    // (#158) shows these in its empty-state top-up and matches them on type-ahead search.
    internal static readonly (long Id, string Name)[] Members =
    [
        (101, "Ada Lovelace"), (102, "Grace Hopper"), (103, "Alan Turing"),
        (104, "Margaret Hamilton"), (105, "Katherine Johnson"), (106, "Linus Torvalds"),
    ];

    // ── Shared mutable state the default routes round-trip (scenarios may seed it via SeedBackend) ───────

    /// <summary>Guards the mutable state below (a detail GET can race an assignee/description PUT).</summary>
    internal object Gate { get; } = new();

    /// <summary>The current assignee set of the task the Assignees pane writes to, mutated by the PUT so the
    /// add/remove round-trip is truthful. Starts empty; the #234 seed scenario pre-seeds member 101 so a
    /// remove round-trips. Read/written under <see cref="Gate"/>.</summary>
    internal HashSet<long> Assignees { get; } = [];

    /// <summary>The task's current <em>additional</em> list memberships ("Tasks in Multiple Lists", #237),
    /// mutated by the membership POST/DELETE (#242). Starts empty (the common single-list case); the #365 QU
    /// List-pane scenario pre-seeds one. Read/written under <see cref="Gate"/>.</summary>
    internal HashSet<string> Locations { get; } = new(StringComparer.Ordinal);

    /// <summary>The task's current plain-text description, mutated by a description PUT (#217) so the write
    /// response — and later detail GETs — echo the edited text. Seeded with wide/multi-byte prose ending in a
    /// ClickUp task link (a Task-kind link for the #317 rendering check). Read/written under
    /// <see cref="Gate"/>; the md-link/wrap scenarios (#430/#443) reseed it.</summary>
    internal string Description { get; set; } =
        "Call Center training Thursday, June 25th\n\nOn My Account - we need to display the Primary and Active addresses while suppressing the others.  During the demo, it was noticed that a large amount of addresses on that test account were displaying.\n\nFeel free to consult with Phil as needed\n\nParent ticket: https://app.clickup.com/t/86a1b2c3d for the full thread";

    // ── Dispatch ────────────────────────────────────────────────────────────────────────────────────────

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var handler = _routes.Resolve(request.Method, path);
        // No route ⇒ the old trailing `else`: an empty JSON object.
        return handler is null ? Ok("{}") : await handler(request, path, request.RequestUri.Query, ct);
    }

    /// <summary>A route handler: everything a former <c>SendAsync</c> branch needs — the request (for its
    /// body/method), the <paramref name="path"/> and <paramref name="query"/>, and the token — returning the
    /// full response so 404 branches keep their status code, not just a body. Public so scenario files (in
    /// this assembly) can register handlers of this shape.</summary>
    internal delegate Task<HttpResponseMessage> RouteHandler(
        HttpRequestMessage request, string path, string query, CancellationToken ct);

    // ── Response helpers (shared) ───────────────────────────────────────────────────────────────────────

    internal static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static Task<HttpResponseMessage> OkAsync(string body) => Task.FromResult(Ok(body));

    /// <summary>ClickUp's task-not-found shape (#303/#353): a 404 with the ITEM_100 error body.</summary>
    internal static HttpResponseMessage TaskNotFound() =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"err":"Task not found","ECODE":"ITEM_100"}""", Encoding.UTF8, "application/json"),
        };

    /// <summary>Reads a canned response payload from the embedded <c>Fixtures/{name}.json</c> (#486).
    /// Adding a fixture is one new file — the <c>.csproj</c> globs <c>Fixtures\*.json</c> in and this
    /// resolves the manifest name by suffix, so there is no shared append point. On a miss it throws with
    /// the available resource names, so a rename fails loudly at first use instead of serving a null body.</summary>
    internal static string Fixture(string name)
    {
        var asm = typeof(FakeClickUp).Assembly;
        var suffix = $".Fixtures.{name}.json";
        var resource = Array.Find(asm.GetManifestResourceNames(),
                           n => n.EndsWith(suffix, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException(
                           $"Embedded fixture '{name}' not found (looked for a resource ending '{suffix}'). "
                           + "Available: " + string.Join(", ", asm.GetManifestResourceNames()));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── Query helpers ───────────────────────────────────────────────────────────────────────────────────

    internal static int PageOf(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
            if (part.StartsWith("page=") && int.TryParse(part[5..], out var p))
                return p;
        return 0;
    }

    /// <summary>Whether the request opted into closed tasks (the feed's F12 / the list's #178 toggle
    /// flips <c>include_closed=true</c>).</summary>
    internal static bool IncludeClosed(string query)
        => query.Contains("include_closed=true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last path segment (a task/list/comment id for the <c>{id}</c> routes).</summary>
    internal static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>The two <c>GET task/{id}</c> 404 sentinels — quick-open not-found (<c>tmissing</c>, #303) and
    /// the hyphenless custom-id fallback (<c>PROJ123</c> without <c>custom_task_ids=true</c>, #353) — or
    /// <c>null</c> for any real id. In the monolith these fired ahead of the tree/foreign/nudge/default branch,
    /// so every scenario's task GET honoured them; scenario overrides call this first to keep that intact
    /// (none of them serves a task named <c>tmissing</c>/<c>PROJ123</c>, so the guard only ever 404s those).</summary>
    internal static HttpResponseMessage? TaskGetSentinel(string idSeg, string query)
    {
        if (idSeg == "tmissing")
            return TaskNotFound();
        if (idSeg == "PROJ123" && !query.Contains("custom_task_ids=true", StringComparison.OrdinalIgnoreCase))
            return TaskNotFound();
        return null;
    }

    /// <summary>The task id from a <c>/v2/task/{id}/comment</c> path.</summary>
    internal static string TaskIdOfComment(string path)
    {
        var trimmed = path.EndsWith("/comment") ? path[..^"/comment".Length] : path;
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>The comment id from a <c>/v2/comment/{id}/reply</c> path.</summary>
    internal static string CommentIdOfReply(string path)
    {
        var trimmed = path.EndsWith("/reply") ? path[..^"/reply".Length] : path;
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>The list id from a <c>/v2/list/{listId}/field</c> definitions path (#365).</summary>
    internal static string ListIdOfField(string path)
    {
        const string listSeg = "/list/";
        const string fieldSeg = "/field";
        var start = path.IndexOf(listSeg, StringComparison.Ordinal) + listSeg.Length;
        var end = path.IndexOf(fieldSeg, start, StringComparison.Ordinal);
        return end > start ? path[start..end] : "";
    }

    /// <summary>The list id from a <c>/v2/list/{listId}/task/{taskId}</c> membership path.</summary>
    internal static string ListIdOfMembership(string path)
    {
        const string listSeg = "/list/";
        const string taskSeg = "/task/";
        var start = path.IndexOf(listSeg, StringComparison.Ordinal) + listSeg.Length;
        var end = path.IndexOf(taskSeg, StringComparison.Ordinal);
        return end > start ? path[start..end] : "";
    }

    // ── Shared JSON builders (default output — scenario-free; scenarios reuse and patch these) ───────────

    /// <summary>The workspace <c>members</c> array (each wrapped as <c>{ user }</c>) from <see cref="Members"/>.</summary>
    internal static string MembersJson()
        => string.Join(",", Members.Select(m => $"{{\"user\":{{\"id\":{m.Id},\"username\":\"{m.Name}\"}}}}"));

    /// <summary>The <c>assignees</c> array for the given id set, mapped to the seeded member names.</summary>
    internal static string AssigneesJson(HashSet<long> assignees)
        => string.Join(",", Members.Where(m => assignees.Contains(m.Id))
            .Select(m => $"{{\"id\":{m.Id},\"username\":\"{m.Name}\"}}"));

    /// <summary>The chip colour for a status name, matching the list's workflow (see <see cref="ListJson"/>):
    /// the seeded status colours by index, then <c>complete</c>'s green, else the neutral grey. Shared by the
    /// #232 foreign and #376 nudge scenarios, whose modelled writes echo a status back.</summary>
    internal static string StatusColor(string status)
    {
        var i = Array.IndexOf(Statuses, status);
        if (i >= 0)
            return StatusColors[i];
        return status == "complete" ? "#6bc950" : "#d3d3d3";
    }

    /// <summary>The default generated team-task page: <paramref name="count"/> tasks starting at
    /// <paramref name="page"/>, plus the completed <c>tclosed</c> task on the last page when the caller opts
    /// into closed tasks. Scenario-free: the #234 seeded-assignee, #376 nudge overlay and #333 warm-closed
    /// date all moved into their scenarios, which override <c>GET team/{id}/task</c> and patch this output.</summary>
    internal string TasksJson(int page, int total, bool includeClosed)
        => TasksJson(page, total, includeClosed, ClosedTaskDefaultDate);

    /// <summary>As <see cref="TasksJson(int,int,bool)"/>, but with the completed task's <c>date_updated</c>
    /// supplied — the #333 warm-closed scenario passes a recent date so its row survives the cache age
    /// window, every other caller the fixed default.</summary>
    internal string TasksJson(int page, int total, bool includeClosed, string closedDate)
    {
        const int pageSize = 100;
        var start = page * pageSize;
        var count = Math.Clamp(total - start, 0, pageSize);
        var lastPage = start + count >= total;
        var sb = new StringBuilder();
        sb.Append("{\"tasks\":[");
        for (var i = 0; i < count; i++)
        {
            var k = start + i;
            var li = k % 3;
            if (i > 0) sb.Append(',');
            // Every 4th task is a subtask of the task 3 before it (same list), so the F4
            // nested view has real parents to nest under.
            var parent = k % 4 == 3 ? $",\"parent\":\"t{k - 3}\"" : "";
            var status = Statuses[k % 4];
            var statusColor = StatusColors[k % 4];
            sb.Append($$"""
            {"id":"t{{k}}","name":"Task {{k}} — follow up on the {{ListNames[li]}} item with a realistic title 📌","status":{"status":"{{status}}","color":"{{statusColor}}"},"list":{"id":"{{Lists[li]}}","name":"{{ListNames[li]}}"},"due_date":"{{DateTimeOffset.UtcNow.AddDays(k % 14).ToUnixTimeMilliseconds()}}","date_updated":"1700000000000","url":"https://app.clickup.com/t/t{{k}}"{{parent}}{{(k % 3 == 0 ? ",\"priority\":{\"priority\":\"high\",\"color\":\"#f50000\"}" : "")}}}
            """);
        }
        // A completed (closed-type) task, returned only when the caller opts into closed tasks. The feed
        // fans a comment fetch out over it, so its distinctive comment (see CommentsJson) surfaces only
        // once F12 flips include_closed on. Appended on the last page so paging stays correct.
        if (includeClosed && lastPage)
        {
            if (count > 0) sb.Append(',');
            sb.Append($$"""
            {"id":"tclosed","name":"Closed ticket — shipped and done ✅","status":{"status":"complete","type":"closed","color":"#6bc950"},"list":{"id":"plist","name":"Personal Tasks"},"date_updated":"{{closedDate}}","url":"https://app.clickup.com/t/tclosed"}
            """);
        }
        sb.Append($"],\"last_page\":{(lastPage ? "true" : "false")}}}");
        return sb.ToString();
    }

    /// <summary>The completed task's fixed <c>date_updated</c> (the feed checks depend on it for comment-sort
    /// order). The #333 warm-closed scenario substitutes a recent date via the overload above.</summary>
    internal const string ClosedTaskDefaultDate = "1751500000000";

    /// <summary>The default task-detail read (and the echo for a task PUT): fixed name, the current mutable
    /// assignee/description/locations, an empty <c>checklists</c> and no <c>custom_fields</c>. Scenario-free:
    /// the #425 title refresh, #365 custom-field values and #456 checklists all moved into their scenarios,
    /// which override <c>GET task/{id}</c> and patch this JSON.</summary>
    internal string DetailJson(string id)
    {
        lock (Gate)
        {
            var description = JsonSerializer.Serialize(Description);
            return $$"""
            {"id":"{{id}}","name":"My Account - Address display  (EA-7221)","status":{"status":"in review","color":"#a875ff"},"list":{"id":"plist","name":"Personal Tasks"},"url":"https://app.clickup.com/t/{{id}}","date_updated":"1700000000000","assignees":[{{AssigneesJson(Assignees)}}],"locations":[{{LocationsJson()}}],"checklists":[],"description":{{description}}}
            """;
        }
    }

    /// <summary>The <c>locations</c> array (additional list memberships, #242) for the current
    /// <see cref="Locations"/> set, mapped to the seeded list names. Called under <see cref="Gate"/>.</summary>
    internal string LocationsJson()
        => string.Join(",", Locations.Select(lid =>
        {
            var idx = Array.IndexOf(Lists, lid);
            var name = idx >= 0 ? ListNames[idx] : lid;
            return $"{{\"id\":\"{lid}\",\"name\":\"{name}\"}}";
        }));

    /// <summary>The default flat comment list for a task (#317 grapheme mix), or the completed task's single
    /// closing note. Scenario-free: the #329 threads reply-count, #468 long stream and #452 vary-comments
    /// tail moved into their scenarios, which override <c>GET task/{id}/comment</c> and patch this.</summary>
    internal static string CommentsJson(string taskId)
    {
        // The completed task's activity (#178-style feed F12): a single distinctive comment, dated newest so
        // it sorts to the top of the feed when include_closed surfaces its task.
        if (taskId == "tclosed")
            return JsonSerializer.Serialize(new
            {
                comments = new[]
                {
                    new { id = "cclosed", comment_text = "Closing note: deployed to prod, ticket resolved.", user = new { username = "Dana Closed" }, date = "1751495000000", resolved = false },
                },
            });

        // 🛠️ is U+1F6E0 + U+FE0F (variation selector): ambiguous-width emoji presentation —
        // the worst case for column-model vs terminal disagreement (field-reported trigger).
        var text = "🛠️ Session summary — implementation (“ship now” approach)\n\n" +
                   "PR: https://github.com/rbcministries/ODBM.Secure/pull/64 — Ready for Review\n" +
                   "Branch: claude/ea-7221-address-display (off latest main)\n\n" +
                   "What was built\n\n" +
                   "Frontend-only filter in getAddressBookPageData (apps/account/src/api/account.ts): the Addresses page now displays only the primary address + addresses in use by an active (active/in_renewal) subscription; historical/unused addresses are suppressed.";
        var comments = new object[]
        {
            new { id = "c1", comment_text = text, user = new { username = "Ben Seymour" }, date = "1751476320000", resolved = false },
            new { id = "c2", comment_text = "Follow-up: verified against the staging account — looks good ✅", user = new { username = "Ben Seymour" }, date = "1751480000000", resolved = false, reply_count = "0" },
            // Mentions the signed-in user (username "bench", see the /user response), so the feed (#114) can
            // be validated end-to-end: this row gets the mention chip and is the only one the F3
            // mentions-only filter keeps. Newest date so it sorts to the top of the feed.
            new { id = "c3", comment_text = "@bench can you take a look when you get a chance?", user = new { username = "Alex Kim" }, date = "1751490000000", resolved = false },
        };
        return JsonSerializer.Serialize(new { comments });
    }

    // Create/echo bodies shared by the default routes and the scenarios that override them only to also
    // record the request (comment-log #325, reply-log #330, capture #395) — so the echo stays one source.

    /// <summary>The create-task echo (#209/#213): a created task so the New Task screen's Save round-trips
    /// through the facade and closes back to the list. (Not persisted into the team-tasks list.)</summary>
    internal const string CreateTaskEchoBody =
        """{"id":"tnew","name":"New task from Ctrl+N","status":{"status":"to do","color":"#d3d3d3"},"list":{"id":"plist","name":"Personal Tasks"},"url":"https://app.clickup.com/t/tnew"}""";

    /// <summary>The create-comment echo (#216/#144): the minimal created-comment shape the deserializer
    /// reads, with the id as a JSON <em>number</em> (the GET read path returns it as a string).</summary>
    internal const string CreateCommentEchoBody = """{"id":9014000000001,"hist_id":"h1","date":1751500000000}""";

    /// <summary>The create-reply echo (#330): the same minimal created-comment shape.</summary>
    internal const string CreateReplyEchoBody = """{"id":9014000000002,"hist_id":"h2","date":1751500500000}""";

    internal static string ListJson(string path)
    {
        var id = LastSegment(path);
        var idx = Array.IndexOf(Lists, id);
        var name = idx >= 0 ? ListNames[idx] : id;
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"color":"#e16b16"},"statuses":[{"status":"to do","color":"#d3d3d3","orderindex":0},{"status":"in progress","color":"#4194f6","orderindex":1},{"status":"blocked","color":"#e50000","orderindex":2},{"status":"in review","color":"#a875ff","orderindex":3},{"status":"complete","color":"#6bc950","orderindex":4}]}
        """;
    }

    // ── Shared mutation appliers (default task PUT reuses these; scenarios may too) ──────────────────────

    /// <summary>Applies an assignee PUT body (<c>{"assignees":{"add":[id],"rem":[id]}}</c>) to
    /// <see cref="Assignees"/> so the write response echoes the new set. A body without <c>assignees</c>
    /// leaves it untouched. Call under <see cref="Gate"/>.</summary>
    internal void ApplyAssigneeMutation(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (!doc.RootElement.TryGetProperty("assignees", out var a))
                return;
            if (a.TryGetProperty("add", out var add) && add.ValueKind == JsonValueKind.Array)
                foreach (var e in add.EnumerateArray())
                    Assignees.Add(e.GetInt64());
            if (a.TryGetProperty("rem", out var rem) && rem.ValueKind == JsonValueKind.Array)
                foreach (var e in rem.EnumerateArray())
                    Assignees.Remove(e.GetInt64());
        }
        catch (JsonException)
        {
            // A non-JSON / unexpected body is not this fake's concern — leave the set untouched.
        }
    }

    /// <summary>Applies a description PUT body (<c>{"description":"..."}</c>) to <see cref="Description"/> so
    /// the write response — and later detail GETs — echo the edited text (#217). A body without a string
    /// <c>description</c> leaves it untouched. Call under <see cref="Gate"/>.</summary>
    internal void ApplyDescriptionMutation(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                Description = d.GetString() ?? "";
        }
        catch (JsonException)
        {
            // A non-JSON / unexpected body is not this fake's concern — leave the description untouched.
        }
    }

    /// <summary>Reads the string <c>name</c> from a JSON body (checklist create/rename, #458), or null when
    /// the body carries no string <c>name</c>. Shared by the checklist scenario's handlers.</summary>
    internal static string? ParseName(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
