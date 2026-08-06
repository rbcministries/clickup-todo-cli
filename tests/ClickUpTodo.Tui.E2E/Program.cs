using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;
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
    // #304: seed a workspace subdomain so a Ctrl+B launch rewrites the fake backend's
    // app.clickup.com task URLs onto {subdomain}.clickup.com. Absent ⇒ blank ⇒ no rewrite.
    WorkspaceSubdomain = Environment.GetEnvironmentVariable("E2E_SUBDOMAIN") ?? "",
};

if (Environment.GetEnvironmentVariable("E2E_VIEW") == "rich")
{
    // A realistic power view: grouped by list, subtasks nested (all assignees, #179), a few pins.
    config.View.GroupField = TaskField.List;
    config.View.Subtasks = SubtaskView.All;
    config.PinnedTaskIds = ["t1", "t5", "t9"];
}

// Opt-in not-mine-rows scenario (#232): a context parent (#46) and a foreign subtask (#70/#179) so
// the #160 "Quick Updates edits a task that isn't my own work, and the row stays" path is assertable.
// Both row kinds only exist while the subtasks view is on — TodoApp.FetchAsync gates the context-parent
// fetch on ShowSubtasks and the foreign-subtask fetch on Subtasks != Hidden — so force the "all" state.
var foreign = Environment.GetEnvironmentVariable("E2E_FOREIGN") == "1";
if (foreign)
    config.View.Subtasks = SubtaskView.All;

// Opt-in Task Tree tab scenario (#291): serve a small fixed ancestry/child tree for the opened task so
// the detail view's Task Tree tab has real parents/children to render and navigate. Gated so no other
// check's GET /task/{id} response changes.
var tree = Environment.GetEnvironmentVariable("E2E_TREE") == "1";

// #320: E2E_LINK_CTRL_DEST=tab sets the persisted task-link Ctrl+Click destination to a new terminal
// tab, so the link-destination check can drive Ctrl+/Ctrl+Shift+click and observe the new-tab launch.
// Default (unset) keeps the Browser destination, matching every other check's #318 behaviour.
if (Environment.GetEnvironmentVariable("E2E_LINK_CTRL_DEST") == "tab")
    config.DetailView.TaskLinkCtrlClick = TaskLinkCtrlClickDestination.NewTerminalTab;

// Opt-in Checklists tab scenario (C, #456): serve the opened task with a seeded `checklists` array
// (two groups, a nested item, mixed resolved state, one assigned item) so the Checklists tab has real
// content to render. Gated so no other check's GET /task/{id} response changes.
var checklists = Environment.GetEnvironmentVariable("E2E_CHECKLISTS") == "1";

// Two-instance nudge channel (#376 item 1): when E2E_MARKER_DB is set, wire a real shared-file
// LiteDbChangeMarkerStore into BOTH the producer (ClickUpClient) and the consumer (TodoApp), so a Quick
// Update committed in one app process nudges the other (nudge-then-fetch, #294/#295). LiteDB's shared
// connection is the cross-process mutex, so two processes pointed at the same file coordinate safely.
// Absent ⇒ both null ⇒ the facade's Null store and a disarmed marker poll — every single-instance check
// is unchanged. E2E_INSTANCE_ID gives each process a distinct marker id (the consumer skips its own
// writes by id); it defaults to a random id.
LiteDbStateStore? markerStateStore = null;
IChangeMarkerStore? changeMarkers = null;
var markerDbPath = Environment.GetEnvironmentVariable("E2E_MARKER_DB");
if (!string.IsNullOrWhiteSpace(markerDbPath))
{
    var instanceId = Environment.GetEnvironmentVariable("E2E_INSTANCE_ID");
    if (string.IsNullOrWhiteSpace(instanceId))
        instanceId = Guid.NewGuid().ToString("N");
    markerStateStore = new LiteDbStateStore(markerDbPath);
    changeMarkers = markerStateStore.CreateChangeMarkerStore(instanceId);
}

var client = new ClickUpClient(
    "fake-token", new HttpClient(new FakeClickUp(taskCount, foreign, tree, checklists)), changeMarkers: changeMarkers);
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
var lists = new ListFrequencyCache(stateStore, config.WorkspaceId);

// #333 bridge-paint scenario: warm the closed-task cache *before* the TUI boots, so the F12→All
// bridge (TaskService.SupplementWithClosed) has a set to splice and paints closed rows on the
// pre-refresh frame. Exercises the real fetch→map→ClosedTaskCache.Update path (no synthetic inject);
// no state store is needed since Update sets the in-memory snapshot regardless of persistence. Off by
// default so every other scenario keeps its cold, empty warm set (byte-identical A/B first paint).
if (Environment.GetEnvironmentVariable("E2E_WARM_CLOSED") == "1")
    await tasks.PrefetchClosedTasksAsync();

// Arm the closed-refresh stall (#333) only now — after any warm prefetch has already run unstalled —
// so the delay hits the *authoritative* F12→All include_closed=true refresh (opening a deterministic
// window to observe the pre-refresh bridge frame) but never the pre-boot warm prefetch above. A no-op
// unless E2E_STALL_CLOSED_MS > 0.
FakeClickUp.ArmClosedStall();

// #304: when E2E_BROWSER_LOG is set, capture Ctrl+B launches to that file (one URL per line) so a
// pyte check can assert the app.clickup.com → subdomain host rewrite. Otherwise the app can't launch a
// real browser under the PTY, so fall back to a no-op launcher rather than SystemBrowserLauncher.
var browserLog = Environment.GetEnvironmentVariable("E2E_BROWSER_LOG");
IBrowserLauncher browser = string.IsNullOrEmpty(browserLog)
    ? new NullBrowserLauncher()
    : new RecordingBrowserLauncher(browserLog);

// #320: when E2E_TAB_LOG is set, record each new-terminal-tab launch (the Ctrl+Click new-tab
// destination) to that file — one `clickup-todo --task <id>` display command per line — so a pyte
// check asserts the launch from the recorder rather than the (failing, terminal-less) real launcher's
// clipboard fallback. Null otherwise, so TodoApp uses the real TerminalLauncher as before.
var tabLog = Environment.GetEnvironmentVariable("E2E_TAB_LOG");
ITerminalLauncher? tabLauncher = string.IsNullOrEmpty(tabLog) ? null : new RecordingTerminalLauncher(tabLog);

// Single-task launch mode (#296): E2E_SINGLE_TASK=<id> boots SingleTaskApp straight into that task's
// detail view — the harness equivalent of `clickup-todo --task <id>` — instead of the dashboard. It
// shares the same #304 browser launcher so a Ctrl+B host rewrite is observable in single-task mode too.
var singleTaskId = Environment.GetEnvironmentVariable("E2E_SINGLE_TASK");
if (!string.IsNullOrWhiteSpace(singleTaskId))
{
    var launchTask = await tasks.GetTaskDetailAsync(singleTaskId);
    var launchComments = await tasks.GetTaskCommentsAsync(singleTaskId);
    new SingleTaskApp(tasks, config, configStore, launchTask, launchComments, browser).Run("ansi");
    markerStateStore?.Dispose();
    return;
}

new TodoApp(tasks, feed, config, configStore, focus, taskCache, feedCache, assignees, lists, browser,
    changeMarkers, tabLauncher).Run("ansi");
markerStateStore?.Dispose();
return;

/// <summary>A browser launcher that succeeds without opening anything — the default under the PTY, where
/// there is no browser to launch and a real <see cref="SystemBrowserLauncher"/> would just fail.</summary>
sealed class NullBrowserLauncher : IBrowserLauncher
{
    public bool TryOpen(Uri url) => true;
}

/// <summary>Records each launched URL (one per line) to a file so a pyte check can assert the #304 host
/// rewrite. Appends so repeated Ctrl+B presses are all observable.</summary>
sealed class RecordingBrowserLauncher(string path) : IBrowserLauncher
{
    public bool TryOpen(Uri url)
    {
        File.AppendAllText(path, url.ToString() + "\n");
        return true;
    }
}

/// <summary>Records each new-terminal-tab app launch (#320's Ctrl+Click new-tab destination) to a file
/// — one <c>clickup-todo --task &lt;id&gt;</c> display command per line — and reports success, so under
/// the PTY the launch is observable from the recorder instead of failing to a clipboard fallback. Only
/// <see cref="LaunchAppAsync"/> is exercised here; the prompt-file <c>LaunchAsync</c> (agent dispatch)
/// runs through TodoApp's own real launcher, never this one.</summary>
sealed class RecordingTerminalLauncher(string path) : ITerminalLauncher
{
    public Task<LaunchResult> LaunchAsync(
        string promptFilePath, string? workingDir, TerminalLauncherOptions options,
        bool oneOff = false, CancellationToken ct = default)
        => throw new NotSupportedException("RecordingTerminalLauncher records only app-tab launches.");

    public Task<LaunchResult> LaunchAppAsync(
        AppLaunchCommand command, TerminalLauncherOptions options, CancellationToken ct = default)
    {
        File.AppendAllText(path, command.ToDisplayCommand() + "\n");
        return Task.FromResult(new LaunchResult(true, "e2e-recorder", null));
    }
}

sealed class FakeClickUp(int taskCount, bool foreign = false, bool tree = false, bool checklists = false) : HttpMessageHandler
{
    // #232 opt-in scenario flag: serve the small not-mine-rows snapshot + a modelled Status/Priority
    // write instead of the default generated list. Off by default so every existing check is untouched.
    private readonly bool _foreign = foreign;
    private readonly bool _tree = tree;
    // C (#456): when set, DetailJson embeds a seeded `checklists` array so the Checklists tab renders
    // real groups/items. Off by default, so every other check's task-detail response is untouched.
    private readonly bool _checklists = checklists;

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

    // #234 repro seam: when E2E_QU_SEED_ASSIGNEE=1, tasks open Quick Updates already assigned to this
    // member, so the Assignees pane's empty-state row 0 is a removable ✓ row — the state where a stray
    // Enter in the *empty* search box used to silently remove them. Off by default, so the other checks
    // (which don't set it) see the original empty assignee set.
    private const long SeededAssigneeId = 101; // Ada Lovelace (Members[0])
    private static bool SeedQuAssignee => Environment.GetEnvironmentVariable("E2E_QU_SEED_ASSIGNEE") == "1";

    // #333 closed-task bridge-paint scenario knobs (default off, so no other check is affected):
    //  • E2E_WARM_CLOSED=1 — the completed task (tclosed) is served with a *recent* date_updated so it
    //    survives ClosedTaskCache's 30-day age window when the warm-now hook (Program.cs) prefetches it.
    //    Off ⇒ the original fixed date (which the feed checks rely on for comment-sort order) is kept.
    //  • E2E_STALL_CLOSED_MS=<ms> — delay the *authoritative* include_closed=true team-task refresh by
    //    this many ms once armed (see ArmClosedStall), so the F12→All pre-refresh bridge frame is
    //    deterministically observable before the superset lands.
    // #395 opt-in: serve a small set of fillable Custom Field definitions from GET /list/{id}/field so the
    // New Task screen's custom-field page renders and the required-block + drop-down paths are assertable.
    // Off by default, so every existing check sees the empty field set (Save creates directly, as before).
    private static bool CustomFields => Environment.GetEnvironmentVariable("E2E_CUSTOM_FIELDS") == "1";

    // #325 opt-in: when set, the POST /task/{id}/comment handler appends each request body (one per line)
    // to this file so mention_check.py can assert the structured @-mention tag block was actually sent.
    // Off by default, so every existing check's comment post is unaffected.
    private static readonly string? CommentLog = Environment.GetEnvironmentVariable("E2E_COMMENT_LOG");

    private const string CustomFieldsJson =
        """{"fields":[{"id":"cf_notes","name":"Notes","type":"text","required":false},{"id":"cf_estimate","name":"Estimate","type":"number","required":true},{"id":"cf_stage","name":"Stage","type":"drop_down","required":false,"type_config":{"options":[{"id":"opt_alpha","name":"Alpha","orderindex":0},{"id":"opt_beta","name":"Beta","orderindex":1}]}}]}""";

    // #425: when E2E_TITLE_REFRESH=1, the launch task is renamed after its first (boot) detail fetch, so a
    // refresh (Ctrl+R / F5) must move the terminal tab title. The boot fetch keeps the original long name;
    // every fetch after it returns a short renamed title the check can assert the window title changed to.
    // Off by default, so every other scenario sees the fixed launch-task name.
    private static bool TitleRefresh => Environment.GetEnvironmentVariable("E2E_TITLE_REFRESH") == "1";
    private static int _detailFetches;

    // #446 opt-in: serve a *tall* fillable set — nine single-line text fields plus a required tenth — so
    // the New Task Custom fields page's widget stack (2 + 10×3 = 32 content rows) is taller than a short
    // emulated terminal. This exercises the page's content-scroll: "Last Field" seeded below the fold is
    // only reachable/visible once Tab (scroll-on-focus) or PgUp/PgDn scrolls it in. Off by default; takes
    // precedence over E2E_CUSTOM_FIELDS when both are set. "Last Field" being required also drives the
    // required-block path for a below-the-fold field. NATO-word names avoid substring collisions on the
    // pyte screen.
    private static bool CustomFieldsMany => Environment.GetEnvironmentVariable("E2E_CUSTOM_FIELDS_MANY") == "1";

    private const string CustomFieldsManyJson =
        """{"fields":[{"id":"cf_1","name":"Alpha","type":"text","required":false},{"id":"cf_2","name":"Bravo","type":"text","required":false},{"id":"cf_3","name":"Charlie","type":"text","required":false},{"id":"cf_4","name":"Delta","type":"text","required":false},{"id":"cf_5","name":"Echo","type":"text","required":false},{"id":"cf_6","name":"Foxtrot","type":"text","required":false},{"id":"cf_7","name":"Golf","type":"text","required":false},{"id":"cf_8","name":"Hotel","type":"text","required":false},{"id":"cf_9","name":"India","type":"text","required":false},{"id":"cf_last","name":"Last Field","type":"text","required":true}]}""";

    private static bool WarmClosed => Environment.GetEnvironmentVariable("E2E_WARM_CLOSED") == "1";
    private static readonly int ClosedStallMs =
        int.TryParse(Environment.GetEnvironmentVariable("E2E_STALL_CLOSED_MS"), out var ms) ? ms : 0;
    private static volatile bool _closedStallArmed;

    // #376 (item 1) two-instance nudge scenario (E2E_NUDGE=1): a tiny cross-process task-status overlay so
    // a Quick Update committed in one app process is visible to the OTHER process's per-task GET — the
    // nudge re-fetch (#295). Off by default, so every single-instance check keeps the canned PUT/GET. The
    // overlay lives in a shared JSON file (E2E_SHARED_STATE, the SAME path for both processes), keyed by
    // task id; only the writer process mutates it (on a status PUT), the reader only GETs.
    private static bool NudgeScenario => Environment.GetEnvironmentVariable("E2E_NUDGE") == "1";
    private static string? SharedStatePath => Environment.GetEnvironmentVariable("E2E_SHARED_STATE");

    // A date_updated newer than the seeded rows' "1700000000000", so a committed status is strictly newer
    // than the version the other instance already holds. Without the bump the consumer's redundant-fetch
    // guard (held >= server) would suppress the nudge fetch and nothing would propagate.
    private const long NudgeUpdatedMs = 1800000000000L;

    /// <summary>One task's overlaid status in the #376 two-instance scenario.</summary>
    private sealed class StatusOverlay
    {
        public string Status { get; set; } = "";
        public string Color { get; set; } = "";
        public long Updated { get; set; }
    }

    /// <summary>Reads the shared status overlay (#376). A missing/empty/torn file yields an empty map, so
    /// the reader falls back to each task's seeded default. Retries a transient IO race (a concurrent
    /// writer's atomic replace) a few times.</summary>
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

    /// <summary>Upserts one task's overlaid status (#376) via read-modify-write with an atomic replace
    /// (unique temp file + <see cref="File.Move(string, string, bool)"/>, atomic on POSIX), so a concurrent
    /// reader never sees a torn file. The RMW itself assumes a <b>single writer</b> — the scenario only ever
    /// commits in one instance, so this is a status mirror, not the multi-writer channel (that's the marker
    /// <i>store</i>). A unique temp name keeps overlapping writes within a process from clobbering each
    /// other even though the scenario issues just one PUT.</summary>
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

    /// <summary>Arms the include_closed refresh stall (#333). Called by <c>Program.cs</c> right before
    /// <c>Run()</c>, i.e. after any pre-boot warm prefetch — so the stall hits the authoritative F12→All
    /// refresh but never the warm prefetch that seeds the bridge. A no-op unless E2E_STALL_CLOSED_MS &gt; 0.</summary>
    public static void ArmClosedStall() => _closedStallArmed = true;

    /// <summary>The completed task's <c>date_updated</c>: recent (so the warm cache's age window keeps it)
    /// only under E2E_WARM_CLOSED, otherwise the original fixed timestamp the feed checks depend on.</summary>
    private static string ClosedTaskDateUpdated => WarmClosed
        ? DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds().ToString()
        : "1751500000000";

    // The current assignee set of any task the Assignees pane writes to, mutated by the PUT so the
    // add/remove round-trip is truthful (the write response echoes the new set, which the pane and the
    // list row reconcile to). Starts empty so the empty-state list shows the top-frequent members
    // (or pre-seeded with the #234 member so a remove would round-trip truthfully).
    // Guarded by _gate since SendAsync is async and a detail GET can race an assignee PUT.
    private readonly HashSet<long> _assignees = SeedQuAssignee ? [SeededAssigneeId] : [];

    // The task's current *additional* list memberships ("Tasks in Multiple Lists", #237), mutated by the
    // membership POST/DELETE (#242) so an add/remove from the Quick Updates List pane round-trips: a later
    // detail GET reflects the change via the detail's `locations` array. Ids index into Lists/ListNames.
    // Starts empty (the common single-list case — the pane shows only the home "plist"). Guarded by _gate.
    private readonly HashSet<string> _locations = [];
    private readonly object _gate = new();

    // The task's current plain-text description, mutated by a description PUT (#217) so the write
    // response — and later detail GETs — echo the edited text (open → edit → save → reflected round-trip).
    // Guarded by _gate like _assignees. Seeded with the default that DetailJson used to hard-code (wide/
    // multi-byte prose so per-cell rendering has something to bite).
    // Ends with a ClickUp task link so the link-rendering check (#317) has a Task-kind link in the
    // Description pane to assert (the Comments pane already carries a Web-kind github URL). description_edit
    // only asserts the "Call Center training" substring, so the trailing URL is safe for it.
    // #430 opt-in: append a markdown [text](url) link whose visible text is prose ("the runbook") and whose
    // resolved target differs from it, so markdown_osc8_check.py can assert the OSC-8 hyperlink points at the
    // RESOLVED url — not the visible text. Off by default, so every existing check sees the original body
    // byte-for-byte. The visible text isn't a URL, so an OSC-8 open for MdLinkTarget can only come from the
    // markdown resolution this exercises.
    private static bool MdLink => Environment.GetEnvironmentVariable("E2E_MD_LINK") == "1";
    public const string MdLinkTarget = "https://example.com/runbook-42";

    private string _description =
        "Call Center training Thursday, June 25th\n\nOn My Account - we need to display the Primary and Active addresses while suppressing the others.  During the demo, it was noticed that a large amount of addresses on that test account were displaying.\n\nFeel free to consult with Phil as needed\n\nParent ticket: https://app.clickup.com/t/86a1b2c3d for the full thread"
        + (MdLink ? "\n\nSee [the runbook](" + MdLinkTarget + ") for steps" : "");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        string body;

        if (path.EndsWith("/user"))
            body = """{"user":{"id":1,"username":"bench","email":"bench@example.com"}}""";
        // POST/DELETE /v2/list/{listId}/task/{taskId} (#237): task↔list membership writes, consumed by
        // the Quick Updates List pane (#242). Mutate the shared additional-locations set so a later detail
        // GET reflects the change; ClickUp echoes an empty body. Must precede the /task/ branches below,
        // since this path also contains "/task/". Create-task is POST .../task (no trailing id) and stays
        // on its own branch further down.
        else if (path.Contains("/list/") && path.Contains("/task/")
                 && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Delete))
        {
            var listId = ListIdOfMembership(path);
            lock (_gate)
            {
                if (request.Method == HttpMethod.Post) _locations.Add(listId);
                else _locations.Remove(listId);
            }
            body = "{}";
        }
        // POST /task/{id}/comment (#216): the create-comment write returns the minimal created-comment
        // shape (id + date + hist_id) the CreateCommentResponse deserializer reads, so a comment posted
        // from the detail composer round-trips truthfully. The id comes back as a JSON *number* on create
        // (the GET read path returns it as a string) — mirror that so the fake matches the real API (#144).
        // Must precede the GET /comment branch below.
        else if (request.Method == HttpMethod.Post && path.Contains("/task/") && path.EndsWith("/comment"))
        {
            // #325: when E2E_COMMENT_LOG is set, record the raw request body (one per line) so a check can
            // assert the structured `comment` blocks array — an @-mention tag, {"type":"tag","user":{"id":…}}
            // — was actually sent. Otherwise the body is ignored (the canned response covers the round-trip).
            if (!string.IsNullOrEmpty(CommentLog) && request.Content is { } commentContent)
                File.AppendAllText(CommentLog, await commentContent.ReadAsStringAsync(ct) + "\n");
            body = """{"id":9014000000001,"hist_id":"h1","date":1751500000000}""";
        }
        else if (path.Contains("/task/") && path.EndsWith("/comment"))
            body = CommentsJson(TaskIdOfComment(path));
        // POST /comment/{comment_id}/reply (#330, threaded comments D): the create-reply write returns the
        // same minimal created-comment shape as the top-level comment POST (id as a JSON number, hist_id,
        // date), so a reply posted from the composer's reply mode round-trips truthfully. Records the target
        // comment id + request body to E2E_REPLY_LOG so a reply-post check can assert the write reached the
        // backend (keyed to the picked parent) rather than guess from the screen. Must precede the GET reply
        // branch below (both match .EndsWith("/reply")).
        else if (request.Method == HttpMethod.Post && path.Contains("/comment/") && path.EndsWith("/reply"))
        {
            var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
            RecordReply(CommentIdOfReply(path), reqBody);
            body = """{"id":9014000000002,"hist_id":"h2","date":1751500500000}""";
        }
        // GET /comment/{comment_id}/reply (#329, threaded comments C): a comment's reply thread. Only the
        // seeded thread parent (c2) returns replies, and only under E2E_THREADS — so every other scenario,
        // which sees reply_count=0 on all comments, never reaches this branch. Same CommentsResponse wire
        // shape as the flat comment list, which GetThreadedCommentsAsync reads.
        else if (request.Method == HttpMethod.Get && path.Contains("/comment/") && path.EndsWith("/reply"))
            body = RepliesJson(CommentIdOfReply(path));
        else if (path.Contains("/task/") && request.Method == HttpMethod.Put)
        {
            // Status/priority PUTs carry no assignees (the set is untouched); an assignee add/remove
            // mutates the shared set. Either way echo the task with the current assignees so the write
            // response reconciles correctly. Read the body before taking the lock (can't await under it).
            var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
            // #232: in the foreign scenario a Status/Priority PUT is modelled distinctly — parsed,
            // persisted, and echoed — so a committed value round-trips (the default path only reconciles
            // assignees/description and echoes a canned detail).
            if (_foreign)
                body = ForeignPut(path, reqBody);
            else if (NudgeScenario)
                body = NudgePut(path, reqBody);
            else
                lock (_gate)
                {
                    ApplyAssigneeMutation(reqBody);
                    ApplyDescriptionMutation(reqBody);
                    body = DetailJson(path, _assignees);
                }
        }
        else if (path.Contains("/task/"))
        {
            // #303 quick-open not-found path: a GET for the sentinel id "tmissing" returns a 404 so the
            // Ctrl+O resolve can flash an error and leave the list unchanged. Off every other scenario's
            // path (no check opens "tmissing").
            var idSeg = path[(path.LastIndexOf('/') + 1)..];
            if (idSeg == "tmissing")
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"err":"Task not found","ECODE":"ITEM_100"}""", Encoding.UTF8, "application/json"),
                };
            // #353 hyphenless-custom-id fallback: the sentinel id "PROJ123" (a hyphenless custom id that
            // parses as a plain id) 404s on the plain GET but resolves once retried with
            // custom_task_ids=true — proving the plain-id→custom-id 404 fallback end-to-end.
            if (idSeg == "PROJ123" && !query.Contains("custom_task_ids=true", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"err":"Task not found","ECODE":"ITEM_100"}""", Encoding.UTF8, "application/json"),
                };
            if (_tree)
                body = TreeTaskGet(path, query);
            else if (_foreign)
                body = ForeignTaskGet(path, query);
            else if (NudgeScenario)
                body = NudgeTaskGet(path);
            else
                lock (_gate) body = DetailJson(path, _assignees, countTitleFetch: true);
        }
        else if (path.Contains("/team/") && path.EndsWith("/task"))
        {
            // #333: stall the authoritative F12→All include_closed=true refresh (once armed) so the
            // pre-refresh bridge frame is observable before this superset replaces it. The pre-boot warm
            // prefetch's include_closed fetch runs unarmed, so it is never delayed; the default
            // include_closed=false boot/poll fetches never match this gate.
            if (!_foreign && ClosedStallMs > 0 && _closedStallArmed && IncludeClosed(query))
                await Task.Delay(ClosedStallMs, ct);
            body = _foreign ? ForeignTeamTasks() : TasksJson(page: PageOf(query), taskCount, IncludeClosed(query));
        }
        else if (request.Method == HttpMethod.Post && path.Contains("/list/") && path.EndsWith("/task"))
        {
            // Create-task (#209/#213): echo a created task so the New Task screen's Save round-trips
            // through the facade and closes back to the list. (Not persisted into the team-tasks list.)
            // #395: when E2E_CAPTURE_FILE is set, write the outgoing request body to that file so a check
            // can assert the custom_fields array actually reached the POST (a regression that dropped it
            // would leave the file without the values). Off by default, so no other check is affected.
            if (request.Content is not null
                && Environment.GetEnvironmentVariable("E2E_CAPTURE_FILE") is { Length: > 0 } capturePath)
            {
                var requestBody = await request.Content.ReadAsStringAsync(ct);
                try { File.WriteAllText(capturePath, requestBody); } catch { /* best-effort capture */ }
            }
            body = """{"id":"tnew","name":"New task from Ctrl+N","status":{"status":"to do","color":"#d3d3d3"},"list":{"id":"plist","name":"Personal Tasks"},"url":"https://app.clickup.com/t/tnew"}""";
        }
        else if (path.Contains("/list/") && path.EndsWith("/task"))
            body = """{"tasks":[],"last_page":true}""";
        else if (path.Contains("/list/") && path.EndsWith("/field"))
            // Custom Field definitions (#249/#395): the tall set (#446) under E2E_CUSTOM_FIELDS_MANY, else
            // the small seeded set under E2E_CUSTOM_FIELDS, else an empty set (so the New Task screen
            // creates directly, as every other check expects).
            body = CustomFieldsMany ? CustomFieldsManyJson
                : CustomFields ? CustomFieldsJson
                : """{"fields":[]}""";
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

    /// <summary>Records a posted reply (#330) — its target comment id and the raw request body — to the
    /// file named by <c>E2E_REPLY_LOG</c>, one <c>{commentId}\t{body}</c> line per POST, so a reply-post
    /// check can assert the write reached the backend keyed to the picked parent. No-op when unset.</summary>
    private static void RecordReply(string commentId, string requestBody)
    {
        var logPath = Environment.GetEnvironmentVariable("E2E_REPLY_LOG");
        if (string.IsNullOrEmpty(logPath))
            return;
        try { File.AppendAllText(logPath, commentId + "\t" + requestBody.Replace('\n', ' ').Replace('\r', ' ') + "\n"); }
        catch { /* best-effort capture */ }
    }

    /// <summary>The comment id from a <c>/v2/comment/{id}/reply</c> path.</summary>
    private static string CommentIdOfReply(string path)
    {
        var trimmed = path.EndsWith("/reply") ? path[..^"/reply".Length] : path;
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>The reply thread for a comment (#329): two replies for the seeded thread parent (c2),
    /// empty for any other comment. A <c>CommentsResponse</c>-shaped payload, exactly like the flat comment
    /// list, so <c>GetThreadedCommentsAsync</c> maps it the same way.</summary>
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

    private static string TasksJson(int page, int total, bool includeClosed)
    {
        const int pageSize = 100;
        var start = page * pageSize;
        var count = Math.Clamp(total - start, 0, pageSize);
        var lastPage = start + count >= total;
        // #376: apply the two-instance status overlay to the list rows too, so a full resync (not just the
        // per-task nudge re-fetch) reflects a committed cross-process change. Null/empty in every other
        // scenario, so each task keeps its seeded status.
        var overlay = NudgeScenario ? ReadOverlay() : null;
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
            // #234: when opted in, every task carries the seeded current assignee, so whichever task the
            // cursor opens Quick Updates on has a removable ✓ row 0 in its Assignees empty state.
            var seeded = SeedQuAssignee
                ? $",\"assignees\":[{{\"id\":{SeededAssigneeId},\"username\":\"{Members[0].Name}\"}}]"
                : "";
            // Seeded default status/date, overridden by a committed cross-process change (#376) when present.
            var status = Statuses[k % 4];
            var statusColor = StatusColors[k % 4];
            var dateUpdated = "1700000000000";
            if (overlay is not null && overlay.TryGetValue($"t{k}", out var ov))
            {
                status = ov.Status;
                statusColor = ov.Color;
                dateUpdated = ov.Updated.ToString();
            }
            sb.Append($$"""
            {"id":"t{{k}}","name":"Task {{k}} — follow up on the {{ListNames[li]}} item with a realistic title 📌","status":{"status":"{{status}}","color":"{{statusColor}}"},"list":{"id":"{{Lists[li]}}","name":"{{ListNames[li]}}"},"due_date":"{{DateTimeOffset.UtcNow.AddDays(k % 14).ToUnixTimeMilliseconds()}}","date_updated":"{{dateUpdated}}","url":"https://app.clickup.com/t/t{{k}}"{{seeded}}{{parent}}{{(k % 3 == 0 ? ",\"priority\":{\"priority\":\"high\",\"color\":\"#f50000\"}" : "")}}}
            """);
        }
        // A completed (closed-type) task, returned only when the caller opts into closed tasks. The feed
        // fans a comment fetch out over it, so its distinctive comment (see CommentsJson) surfaces only
        // once F12 flips include_closed on — and drops back out when F12 is toggled off. Appended on the
        // last page so paging stays correct.
        if (includeClosed && lastPage)
        {
            if (count > 0) sb.Append(',');
            sb.Append($$"""
            {"id":"tclosed","name":"Closed ticket — shipped and done ✅","status":{"status":"complete","type":"closed","color":"#6bc950"},"list":{"id":"plist","name":"Personal Tasks"},"date_updated":"{{ClosedTaskDateUpdated}}","url":"https://app.clickup.com/t/tclosed"}
            """);
        }
        sb.Append($"],\"last_page\":{(lastPage ? "true" : "false")}}}");
        return sb.ToString();
    }

    /// <summary>Detail for the Enter → detail screen (and the echo for a task PUT). The description
    /// deliberately mixes plain prose with wide/multi-byte graphemes so per-cell rendering issues have
    /// something to bite; the assignees reflect the current mutable set so an assignee write round-trips.</summary>
    // C (#456): a seeded checklists payload — two groups, mixed resolved state, one nested item, one
    // assigned item — mirroring the ClickUp GET /task shape the read model (#454) maps. Aggregate: 2 of 5
    // items resolved ⇒ the tab title reads "Checklists (2/5)".
    private const string ChecklistsJson = """
    [
      {"id":"c1","name":"Release steps","orderindex":0,"resolved":1,"unresolved":2,
       "items":[
         {"id":"i1","name":"Cut the tag","resolved":true,"orderindex":0,"assignee":null},
         {"id":"i2","name":"Draft release notes","resolved":false,"orderindex":1,
          "assignee":{"id":101,"username":"Ada Lovelace"},
          "children":[{"id":"i2a","name":"Verify the changelog","resolved":false,"orderindex":0}]}]},
      {"id":"c2","name":"QA signoff","orderindex":1,"resolved":1,"unresolved":1,
       "items":[
         {"id":"i3","name":"Smoke test on staging","resolved":true,"orderindex":0,"assignee":null},
         {"id":"i4","name":"Cross-browser check","resolved":false,"orderindex":1,"assignee":null}]}
    ]
    """;

    private string DetailJson(string path, HashSet<long> assignees, bool countTitleFetch = false)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        // JSON-encode the (mutable, possibly user-edited) description so quotes/newlines/emoji round-trip.
        var description = JsonSerializer.Serialize(_description);
        // #425: under E2E_TITLE_REFRESH the launch task is renamed after the boot read, so a refresh moves
        // the terminal title. The boot (first) read keeps the original long name; every read after it
        // returns the short renamed name. Only detail READS (GET) advance the counter — a PUT echo passes
        // countTitleFetch:false so a write can't inflate it. Off ⇒ the fixed name every other scenario expects.
        var name = "My Account - Address display  (EA-7221)";
        if (TitleRefresh && countTitleFetch && System.Threading.Interlocked.Increment(ref _detailFetches) > 1)
            name = "Renamed on refresh";
        // C (#456): the seeded checklists array, or empty (the common case — no other check sees it).
        var checklists = _checklists ? ChecklistsJson : "[]";
        // `locations` are the task's additional list memberships (#242), mutated by the membership
        // POST/DELETE so an add/remove from the List pane round-trips; empty in the common single-list case.
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"status":"in review","color":"#a875ff"},"list":{"id":"plist","name":"Personal Tasks"},"url":"https://app.clickup.com/t/{{id}}","date_updated":"1700000000000","assignees":[{{AssigneesJson(assignees)}}],"locations":[{{LocationsJson()}}],"checklists":{{checklists}},"description":{{description}}}
        """;
    }

    /// <summary>The <c>locations</c> array (additional list memberships, #242) for the current
    /// <see cref="_locations"/> set, mapped to the seeded list names. Called under <c>_gate</c>.</summary>
    private string LocationsJson()
        => string.Join(",", _locations.Select(lid =>
        {
            var idx = Array.IndexOf(Lists, lid);
            var name = idx >= 0 ? ListNames[idx] : lid;
            return $"{{\"id\":\"{lid}\",\"name\":\"{name}\"}}";
        }));

    /// <summary>The list id from a <c>/v2/list/{listId}/task/{taskId}</c> membership path.</summary>
    private static string ListIdOfMembership(string path)
    {
        const string listSeg = "/list/";
        const string taskSeg = "/task/";
        var start = path.IndexOf(listSeg, StringComparison.Ordinal) + listSeg.Length;
        var end = path.IndexOf(taskSeg, StringComparison.Ordinal);
        return end > start ? path[start..end] : "";
    }

    /// <summary>Applies a description PUT body (<c>{"description":"..."}</c>) to the shared field so the
    /// write response — and later detail GETs — echo the edited text (#217). A body without a string
    /// <c>description</c> (a status/priority/assignee PUT) leaves it untouched.</summary>
    private void ApplyDescriptionMutation(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                _description = d.GetString() ?? "";
        }
        catch (JsonException)
        {
            // A non-JSON / unexpected body is not this fake's concern — leave the description untouched.
        }
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

    // ── #232 opt-in foreign / context-parent scenario (E2E_FOREIGN=1) ─────────
    // A tiny deterministic snapshot that exercises the #160 not-mine Quick Updates path:
    //   • pt1 — an assigned top-level task carrying a teammate-owned foreign subtask fs1 (#70), and
    //   • ct1 — an assigned task whose parent cp1 is ABSENT from the snapshot, so cp1 is pulled in as a
    //     context-parent header (#46) with ct1 nested under it.
    // The PUT is modelled (parsed, persisted, echoed) so a committed Status/Priority reads back — the
    // gap #232 closes (the default fake echoes a canned "in review" for any /task write). The per-task
    // current status/priority are mutable and _gate-guarded (a background refresh can race a write).
    private readonly Dictionary<string, string> _foreignStatus = new(StringComparer.Ordinal)
    {
        ["pt1"] = "to do",
        ["ct1"] = "to do",
        ["fs1"] = "to do",
        ["cp1"] = "in review",
    };
    private readonly Dictionary<string, int?> _foreignPriority = new(StringComparer.Ordinal);

    /// <summary>The two-task assigned snapshot for the foreign scenario (page-agnostic — it fits one page).</summary>
    private string ForeignTeamTasks()
    {
        lock (_gate)
            return $"{{\"tasks\":[{ForeignTaskJson("pt1", includeSubtasks: false)},{ForeignTaskJson("ct1", includeSubtasks: false)}],\"last_page\":true}}";
    }

    /// <summary>A single task fetch: <c>?include_subtasks=true</c> (the per-parent foreign fetch) appends
    /// the owned subtask; a plain GET (the context-parent detail fetch) omits it.</summary>
    private string ForeignTaskGet(string path, string query)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        var includeSubtasks = query.Contains("include_subtasks=true", StringComparison.OrdinalIgnoreCase);
        lock (_gate)
            return ForeignTaskJson(id, includeSubtasks);
    }

    /// <summary>The Task Tree scenario (#291): a fixed, bounded ancestry/child tree hung off the opened
    /// task <c>t0</c>. A plain GET returns each node's own <c>parent</c> (the tab's ancestry walk climbs
    /// it one node at a time); an <c>?include_subtasks=true</c> GET appends that node's direct children
    /// (the descendant BFS). The chain terminates — <c>tanc</c> has no parent and the leaves have no
    /// children — so the walk and BFS both stop naturally.</summary>
    private string TreeTaskGet(string path, string query)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        var includeSubtasks = query.Contains("include_subtasks=true", StringComparison.OrdinalIgnoreCase);
        return TreeTaskJson(id, includeSubtasks);
    }

    private static string TreeTaskJson(string id, bool includeSubtasks)
    {
        // id -> (display name, parent id, direct child ids). Distinctive UPPER tokens so the check can
        // assert each row is present and correctly indented.
        var (name, parent, children) = id switch
        {
            "tanc" => ("Ancestor epic ANCESTOR", (string?)null, Array.Empty<string>()),
            "t0" => ("Release task ROOT", "tanc", new[] { "t0c1", "t0c2" }),
            "t0c1" => ("Subtask one CHILDONE", "t0", new[] { "t0c1a" }),
            "t0c1a" => ("Nested subtask GRANDKID", "t0c1", Array.Empty<string>()),
            "t0c2" => ("Subtask two CHILDTWO", "t0", Array.Empty<string>()),
            _ => ($"Task {id}", (string?)null, Array.Empty<string>()),
        };
        var parentField = parent is null ? "" : $",\"parent\":\"{parent}\"";
        var subtasksField = includeSubtasks && children.Length > 0
            ? $",\"subtasks\":[{string.Join(",", children.Select(c => TreeTaskJson(c, includeSubtasks: false)))}]"
            : "";
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"status":"in progress","color":"#4194f6"},"list":{"id":"plist","name":"Personal Tasks"},"assignees":[],"date_updated":"1700000000000","url":"https://app.clickup.com/t/{{id}}"{{parentField}}{{subtasksField}}}
        """;
    }

    /// <summary>Applies a modelled Status/Priority write and echoes the task reflecting it.</summary>
    private string ForeignPut(string path, string requestBody)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        lock (_gate)
        {
            ApplyForeignMutation(id, requestBody);
            return ForeignTaskJson(id, includeSubtasks: false);
        }
    }

    /// <summary>Parses a <c>{"status":"…"}</c> / <c>{"priority":n|null}</c> PUT body into the per-task
    /// override maps so the next read/echo reflects the committed value. Assumes <c>_gate</c> is held.</summary>
    private void ApplyForeignMutation(string id, string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                _foreignStatus[id] = st.GetString()!;
            // priority: an integer level sets it; an explicit null (ClickUp's "clear") resets it. Guard the
            // int read so a non-integer number can't throw past the JsonException catch below.
            if (root.TryGetProperty("priority", out var pr))
                _foreignPriority[id] = pr.ValueKind == JsonValueKind.Number && pr.TryGetInt32(out var level) ? level : null;
        }
        catch (JsonException)
        {
            // A non-JSON body isn't this fake's concern — leave the overrides untouched.
        }
    }

    /// <summary>Builds the task object for a known foreign-scenario id, reflecting the current mutable
    /// status/priority. Assumes <c>_gate</c> is held. Only pt1 owns a subtask (fs1), appended when the
    /// caller opted into subtasks.</summary>
    private string ForeignTaskJson(string id, bool includeSubtasks)
    {
        var (name, parent, assignee) = id switch
        {
            "pt1" => ("Assigned parent — my task AA", (string?)null, ""),
            "ct1" => ("My nested subtask BB", "cp1", ""),
            "fs1" => ("Foreign teammate subtask ZZ", "pt1", "{\"id\":101,\"username\":\"Ada Lovelace\"}"),
            "cp1" => ("Context parent PP", (string?)null, "{\"id\":102,\"username\":\"Grace Hopper\"}"),
            _ => (id, (string?)null, ""),
        };
        var status = _foreignStatus.TryGetValue(id, out var s) ? s : "to do";
        var parentField = parent is null ? "" : $",\"parent\":\"{parent}\"";
        // Echo the priority level as ClickUp does: id "1".."4" (which SetTaskPriorityAsync reads back via
        // ClickUpPriority.Level) plus the lowercase name, matching the real API and the default TasksJson.
        var priorityField = _foreignPriority.TryGetValue(id, out var lvl) && lvl is { } l
            ? $",\"priority\":{{\"id\":\"{l}\",\"priority\":\"{ClickUpPriority.NameFromLevel(l)?.ToLowerInvariant()}\",\"color\":\"#f50000\"}}"
            : "";
        var subtasksField = includeSubtasks && id == "pt1"
            ? $",\"subtasks\":[{ForeignTaskJson("fs1", includeSubtasks: false)}]"
            : "";
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"status":"{{status}}","color":"{{ForeignStatusColor(status)}}"},"list":{"id":"plist","name":"Personal Tasks"},"assignees":[{{assignee}}],"date_updated":"1700000000000","url":"https://app.clickup.com/t/{{id}}"{{parentField}}{{priorityField}}{{subtasksField}}}
        """;
    }

    /// <summary>The chip colour for a status name, matching the list's workflow (see <see cref="ListJson"/>).</summary>
    private static string ForeignStatusColor(string status)
    {
        var i = Array.IndexOf(Statuses, status);
        if (i >= 0)
            return StatusColors[i];
        return status == "complete" ? "#6bc950" : "#d3d3d3";
    }

    // ── #376 (item 1) two-instance nudge scenario (E2E_NUDGE=1) ───────────────
    // A Quick Update committed in one app process must be observable in the OTHER process's per-task
    // GET (its nudge re-fetch, #295). Unlike the foreign scenario's in-memory maps, the state is shared
    // via a file (E2E_SHARED_STATE) so it crosses the process boundary. Only the status is modelled — the
    // one field this scenario drives — keyed by task id; the writer persists it on a status PUT and every
    // GET reflects it.

    /// <summary>#376 two-instance PUT: persist a committed status into the shared overlay (bumping
    /// <c>date_updated</c> so it's strictly newer than the seed the other instance holds) and echo the task
    /// reflecting it — so the writer's own optimistic reconcile settles on the committed value and the
    /// change-marker it records carries the newer server <c>date_updated</c>.</summary>
    private static string NudgePut(string path, string requestBody)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        var overlay = ReadOverlay();
        string status, color;
        if (overlay.TryGetValue(id, out var cur))
            (status, color) = (cur.Status, cur.Color);
        else
            (status, color) = (DefaultStatus(id), ForeignStatusColor(DefaultStatus(id)));
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
            {
                status = st.GetString()!;
                color = ForeignStatusColor(status);
            }
        }
        catch (JsonException)
        {
            // A non-JSON / unexpected body isn't this fake's concern — keep the prior/default status.
        }
        WriteOverlay(id, new StatusOverlay { Status = status, Color = color, Updated = NudgeUpdatedMs });
        return NudgeTaskJson(id, status, color, NudgeUpdatedMs);
    }

    /// <summary>#376 two-instance per-task GET (the consumer's nudge re-fetch, #295): serve the overlaid
    /// status when the writer has committed one, else the task's seeded default (with the original date).</summary>
    private static string NudgeTaskGet(string path)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        var overlay = ReadOverlay();
        return overlay.TryGetValue(id, out var o)
            ? NudgeTaskJson(id, o.Status, o.Color, o.Updated)
            : NudgeTaskJson(id, DefaultStatus(id), ForeignStatusColor(DefaultStatus(id)), 1700000000000L);
    }

    /// <summary>The seeded default status for a task id, matching <see cref="TasksJson"/> (<c>Statuses[k % 4]</c>).</summary>
    private static string DefaultStatus(string id) => Statuses[TaskIndex(id) % 4];

    /// <summary>The numeric index behind a <c>t{k}</c> id (0 when it doesn't parse).</summary>
    private static int TaskIndex(string id) => int.TryParse(id.TrimStart('t'), out var k) ? k : 0;

    /// <summary>A full task object (the shape <see cref="ClickUpClient.Map"/> reads) for the #376 scenario.
    /// Name / list / due date / priority mirror <see cref="TasksJson"/> for the same id, so the wholesale
    /// full-fidelity reconcile (#376 item 2) lands a row that differs from the seeded one only in the status
    /// chip — otherwise the replace would strip the priority flag and due date the list row carried, which
    /// would misrepresent the reconcile as lossy.</summary>
    private static string NudgeTaskJson(string id, string status, string color, long updated)
    {
        var k = TaskIndex(id);
        var li = k % 3;
        var dueMs = DateTimeOffset.UtcNow.AddDays(k % 14).ToUnixTimeMilliseconds();
        var priority = k % 3 == 0 ? ",\"priority\":{\"priority\":\"high\",\"color\":\"#f50000\"}" : "";
        return $$"""
        {"id":"{{id}}","name":"Task {{k}} — follow up on the {{ListNames[li]}} item with a realistic title 📌","status":{"status":"{{status}}","color":"{{color}}"},"list":{"id":"{{Lists[li]}}","name":"{{ListNames[li]}}"},"due_date":"{{dueMs}}","date_updated":"{{updated}}","assignees":[]{{priority}},"url":"https://app.clickup.com/t/{{id}}"}
        """;
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

        // Threaded comments (#329): under E2E_THREADS, the middle comment (c2) reports a two-reply thread,
        // so the real CommentThreadLoader fetches its replies from the /comment/c2/reply route (RepliesJson)
        // and the detail view renders them nested. Off by default (reply_count "0"), so every existing
        // scenario sees the same flat three comments as before.
        var threads = Environment.GetEnvironmentVariable("E2E_THREADS") == "1";

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
            new { id = "c2", comment_text = "Follow-up: verified against the staging account — looks good ✅", user = new { username = "Ben Seymour" }, date = "1751480000000", resolved = false, reply_count = threads ? "2" : "0" },
            // Mentions the signed-in user (username "bench", see the /user response), so the feed
            // (#114) can be validated end-to-end: this row gets the mention chip and is the only one
            // the F3 mentions-only filter keeps. Newest date so it sorts to the top of the feed.
            new { id = "c3", comment_text = "@bench can you take a look when you get a chance?", user = new { username = "Alex Kim" }, date = "1751490000000", resolved = false },
        };
        // Optional: a deterministic tall tail so the Stream overflows by well over a page — the geometry
        // the #468 page-scroll composition check needs (detail_arrow_check.py). Fixed count + fixed text +
        // fixed dates, so it's stable across refreshes; off by default, so every other scenario is
        // unaffected. Distinct from E2E_VARY_COMMENTS, whose tail grows on each fetch.
        if (Environment.GetEnvironmentVariable("E2E_LONG_STREAM") == "1")
        {
            for (var i = 1; i <= 40; i++)
                comments.Add(new { id = $"ls{i}", comment_text = $"Filler stream line {i:D2} — deterministic content for the #468 page-scroll composition check.", user = new { username = "Filler Bot" }, date = $"{1751490100000L + i}", resolved = false });
        }

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
