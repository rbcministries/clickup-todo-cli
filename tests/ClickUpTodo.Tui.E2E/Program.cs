using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Tui;

// Boots the REAL TodoApp against a canned in-process ClickUp backend so the TUI can be
// driven under a PTY and its keypress latency measured end-to-end. No network.

var taskCount = int.TryParse(Environment.GetEnvironmentVariable("E2E_TASKS"), out var n) ? n : 200;

var config = new AppConfig
{
    WorkspaceId = "ws1",
    WorkspaceName = "Bench",
    PersonalTasksListId = "plist",
    PersonalTasksListName = "Personal Tasks",
    RefreshSeconds = int.TryParse(Environment.GetEnvironmentVariable("E2E_REFRESH"), out var r) ? r : 600,
};

if (Environment.GetEnvironmentVariable("E2E_VIEW") == "rich")
{
    // A realistic power view: grouped by list, subtasks nested (all assignees, #179), a few pins.
    config.View.GroupField = TaskField.List;
    config.View.Subtasks = SubtaskView.All;
    config.PinnedTaskIds = ["t1", "t5", "t9"];
}

var client = new ClickUpClient("fake-token", new HttpClient(new FakeClickUp(taskCount)));
IStateStore stateStore = new JsonFileStateStore();
var configStore = new ConfigStore(stateStore);
var tasks = new TaskService(client, config, 1, userName: "Ben Seymour");
var feed = new FeedService(client, tasks, config);
var focus = new LocalFocusStore(config, configStore);
// Isolated per-process state dir for the persistent task cache (#122), so the harness never touches
// the developer's real data dir and every run starts with a cold cache — a deterministic no-op first
// paint, which keeps the A/B renders byte-identical to the stock renderer.
var cacheStore = new JsonFileStateStore(
    Path.Combine(Path.GetTempPath(), "clickup-todo-e2e", Guid.NewGuid().ToString("N")));
var taskCache = new TaskCache(cacheStore);
// Same isolated, cold-on-each-run store for the persistent feed cache (#123) — a deterministic
// cold first open keeps the A/B renders byte-identical to the stock renderer.
var feedCache = new FeedCache(cacheStore);
var assignees = new AssigneeFrequencyCache(
    stateStore, config.WorkspaceId, ct => client.GetWorkspaceMembersAsync(config.WorkspaceId, ct));
// List-frequency pool (#238), on the same store — a cold pool each run keeps A/B renders identical.
var lists = new ListFrequencyCache(stateStore, config.WorkspaceId);
new TodoApp(tasks, feed, config, configStore, focus, taskCache, feedCache, assignees, lists).Run("ansi");
return;

sealed class FakeClickUp(int taskCount) : HttpMessageHandler
{
    private static readonly string[] Statuses = ["to do", "in progress", "blocked", "in review"];
    private static readonly string[] StatusColors = ["#d3d3d3", "#4194f6", "#e50000", "#a875ff"];
    private static readonly string[] Lists = ["plist", "list2", "list3"];
    private static readonly string[] ListNames = ["Personal Tasks", "Q3 Website Refresh", "Ministry Ops"];

    // Workspace members feed the assignee-frequency pool (#155): the Quick Updates Assignees pane
    // (#158) shows these in its empty-state top-up and matches them on type-ahead search.
    private static readonly (long Id, string Name)[] Members =
    [
        (101, "Ada Lovelace"), (102, "Grace Hopper"), (103, "Alan Turing"),
        (104, "Margaret Hamilton"), (105, "Katherine Johnson"), (106, "Linus Torvalds"),
    ];

    // The current assignee set of any task the Assignees pane writes to, mutated by the PUT so the
    // add/remove round-trip is truthful (the write response echoes the new set, which the pane and the
    // list row reconcile to). Starts empty so the empty-state list shows the top-frequent members.
    // Guarded by _gate since SendAsync is async and a detail GET can race an assignee PUT.
    private readonly HashSet<long> _assignees = [];
    private readonly object _gate = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        string body;

        if (path.EndsWith("/user"))
            body = """{"user":{"id":1,"username":"bench","email":"bench@example.com"}}""";
        // POST /task/{id}/comment (#216): the create-comment write returns the minimal created-comment
        // shape (id + date + hist_id) the CreateCommentResponse deserializer reads, so a comment posted
        // from the detail composer round-trips truthfully. Must precede the GET /comment branch below.
        else if (request.Method == HttpMethod.Post && path.Contains("/task/") && path.EndsWith("/comment"))
            body = """{"id":"newc1","hist_id":"h1","date":1751500000000}""";
        else if (path.Contains("/task/") && path.EndsWith("/comment"))
            body = CommentsJson(TaskIdOfComment(path));
        else if (path.Contains("/task/") && request.Method == HttpMethod.Put)
        {
            // Status/priority PUTs carry no assignees (the set is untouched); an assignee add/remove
            // mutates the shared set. Either way echo the task with the current assignees so the write
            // response reconciles correctly. Read the body before taking the lock (can't await under it).
            var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
            lock (_gate)
            {
                ApplyAssigneeMutation(reqBody);
                body = DetailJson(path, _assignees);
            }
        }
        else if (path.Contains("/task/"))
            lock (_gate) body = DetailJson(path, _assignees);
        else if (path.Contains("/team/") && path.EndsWith("/task"))
            body = TasksJson(page: PageOf(query), taskCount, IncludeClosed(query));
        else if (request.Method == HttpMethod.Post && path.Contains("/list/") && path.EndsWith("/task"))
            // Create-task (#209/#213): echo a created task so the New Task screen's Save round-trips
            // through the facade and closes back to the list. (Not persisted into the team-tasks list.)
            body = """{"id":"tnew","name":"New task from Ctrl+N","status":{"status":"to do","color":"#d3d3d3"},"list":{"id":"plist","name":"Personal Tasks"},"url":"https://app.clickup.com/t/tnew"}""";
        else if (path.Contains("/list/") && path.EndsWith("/task"))
            body = """{"tasks":[],"last_page":true}""";
        else if (path.Contains("/list/"))
            body = ListJson(path);
        else if (path.EndsWith("/team"))
            body = $$"""{"teams":[{"id":"ws1","name":"Bench","members":[{{MembersJson()}}]}]}""";
        else
            body = "{}";

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static int PageOf(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
            if (part.StartsWith("page=") && int.TryParse(part[5..], out var p))
                return p;
        return 0;
    }

    /// <summary>Whether the request opted into closed tasks (the feed's F12 / the list's #178 toggle
    /// flips <c>include_closed=true</c>).</summary>
    private static bool IncludeClosed(string query)
        => query.Contains("include_closed=true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The task id from a <c>/v2/task/{id}/comment</c> path.</summary>
    private static string TaskIdOfComment(string path)
    {
        var trimmed = path.EndsWith("/comment") ? path[..^"/comment".Length] : path;
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    private static string TasksJson(int page, int total, bool includeClosed)
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
            sb.Append($$"""
            {"id":"t{{k}}","name":"Task {{k}} — follow up on the {{ListNames[li]}} item with a realistic title 📌","status":{"status":"{{Statuses[k % 4]}}","color":"{{StatusColors[k % 4]}}"},"list":{"id":"{{Lists[li]}}","name":"{{ListNames[li]}}"},"due_date":"{{DateTimeOffset.UtcNow.AddDays(k % 14).ToUnixTimeMilliseconds()}}","date_updated":"1700000000000","url":"https://app.clickup.com/t/t{{k}}"{{parent}}{{(k % 3 == 0 ? ",\"priority\":{\"priority\":\"high\",\"color\":\"#f50000\"}" : "")}}}
            """);
        }
        // A completed (closed-type) task, returned only when the caller opts into closed tasks. The feed
        // fans a comment fetch out over it, so its distinctive comment (see CommentsJson) surfaces only
        // once F12 flips include_closed on — and drops back out when F12 is toggled off. Appended on the
        // last page so paging stays correct.
        if (includeClosed && lastPage)
        {
            if (count > 0) sb.Append(',');
            sb.Append("""
            {"id":"tclosed","name":"Closed ticket — shipped and done ✅","status":{"status":"complete","type":"closed","color":"#6bc950"},"list":{"id":"plist","name":"Personal Tasks"},"date_updated":"1751500000000","url":"https://app.clickup.com/t/tclosed"}
            """);
        }
        sb.Append($"],\"last_page\":{(lastPage ? "true" : "false")}}}");
        return sb.ToString();
    }

    /// <summary>Detail for the Enter → detail screen (and the echo for a task PUT). The description
    /// deliberately mixes plain prose with wide/multi-byte graphemes so per-cell rendering issues have
    /// something to bite; the assignees reflect the current mutable set so an assignee write round-trips.</summary>
    private static string DetailJson(string path, HashSet<long> assignees)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        return $$"""
        {"id":"{{id}}","name":"My Account - Address display  (EA-7221)","status":{"status":"in review","color":"#a875ff"},"list":{"id":"plist","name":"Personal Tasks"},"url":"https://app.clickup.com/t/{{id}}","date_updated":"1700000000000","assignees":[{{AssigneesJson(assignees)}}],"description":"Call Center training Thursday, June 25th\n\nOn My Account - we need to display the Primary and Active addresses while suppressing the others.  During the demo, it was noticed that a large amount of addresses on that test account were displaying.\n\nFeel free to consult with Phil as needed"}
        """;
    }

    /// <summary>The workspace <c>members</c> array (each wrapped as <c>{ user }</c>) from <see cref="Members"/>.</summary>
    private static string MembersJson()
        => string.Join(",", Members.Select(m => $"{{\"user\":{{\"id\":{m.Id},\"username\":\"{m.Name}\"}}}}"));

    /// <summary>The <c>assignees</c> array for the given id set, mapped to the seeded member names.</summary>
    private static string AssigneesJson(HashSet<long> assignees)
        => string.Join(",", Members.Where(m => assignees.Contains(m.Id))
            .Select(m => $"{{\"id\":{m.Id},\"username\":\"{m.Name}\"}}"));

    /// <summary>Applies an assignee PUT body (<c>{"assignees":{"add":[id],"rem":[id]}}</c>) to the shared
    /// set so the write response echoes the new set; a body without <c>assignees</c> (a status/priority
    /// PUT) leaves it untouched.</summary>
    private void ApplyAssigneeMutation(string requestBody)
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
                    _assignees.Add(e.GetInt64());
            if (a.TryGetProperty("rem", out var rem) && rem.ValueKind == JsonValueKind.Array)
                foreach (var e in rem.EnumerateArray())
                    _assignees.Remove(e.GetInt64());
        }
        catch (JsonException)
        {
            // A non-JSON / unexpected body is not this fake's concern — leave the set untouched.
        }
    }

    /// <summary>Comments matching the field report that exposed sparse-flush artifacts: an emoji
    /// lead-in, em-dashes, curly quotes, and a URL (auto-hyperlinked cells) on the same lines.</summary>
    // Counts comment fetches so E2E_VARY_COMMENTS can make each successive fetch return more comments —
    // the only way to exercise the detail view's *content-changed* refresh path (scroll preservation).
    private static int _commentFetches;

    private static string CommentsJson(string taskId)
    {
        // The completed task's activity (#178-style feed F12): a single distinctive comment, dated
        // newest so it sorts to the top of the feed when include_closed surfaces its task. Its author
        // ("Dana Closed") appears in the feed only while F12 is on.
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
        var comments = new List<object>
        {
            new { id = "c1", comment_text = text, user = new { username = "Ben Seymour" }, date = "1751476320000", resolved = false },
            new { id = "c2", comment_text = "Follow-up: verified against the staging account — looks good ✅", user = new { username = "Ben Seymour" }, date = "1751480000000", resolved = false },
            // Mentions the signed-in user (username "bench", see the /user response), so the feed
            // (#114) can be validated end-to-end: this row gets the mention chip and is the only one
            // the F3 mentions-only filter keeps. Newest date so it sorts to the top of the feed.
            new { id = "c3", comment_text = "@bench can you take a look when you get a chance?", user = new { username = "Alex Kim" }, date = "1751490000000", resolved = false },
        };
        // Optional: append a growing tail of comments so each refresh changes content (scroll-preservation
        // check). Off by default, so every existing scenario sees the exact same three comments as before.
        if (Environment.GetEnvironmentVariable("E2E_VARY_COMMENTS") == "1")
        {
            var seq = System.Threading.Interlocked.Increment(ref _commentFetches);
            for (var i = 1; i <= seq; i++)
                comments.Add(new { id = $"e{i}", comment_text = $"Auto-refresh probe comment {i}", user = new { username = "Probe Bot" }, date = $"{1751490000000L + i}", resolved = false });
        }
        return JsonSerializer.Serialize(new { comments });
    }

    private static string ListJson(string path)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        var idx = Array.IndexOf(Lists, id);
        var name = idx >= 0 ? ListNames[idx] : id;
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"color":"#e16b16"},"statuses":[{"status":"to do","color":"#d3d3d3","orderindex":0},{"status":"in progress","color":"#4194f6","orderindex":1},{"status":"blocked","color":"#e50000","orderindex":2},{"status":"in review","color":"#a875ff","orderindex":3},{"status":"complete","color":"#6bc950","orderindex":4}]}
        """;
    }
}
