using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;
using ClickUpTodo.Tui;
using ClickUpTodo.Tui.E2E;

// Boots the REAL TodoApp against a canned in-process ClickUp backend so the TUI can be driven under a PTY
// and its keypress latency measured end-to-end. No network.
//
// This is the harness orchestrator (E, #489): it discovers scenarios by reflection — never a registry — and
// wires the active ones in. A scenario self-activates from its own legacy env var(s) (so the 39 checks'
// existing invocations keep working) or via E2E_SCENARIO=<Name>; each active scenario tweaks the config,
// seeds the backend, contributes routes, supplies launchers/markers, and optionally hosts a different app.
// Adding a scenario is one new file under Scenarios/ — this file, FakeClickUp, and the config block below
// stop being shared append points.

var taskCount = int.TryParse(Environment.GetEnvironmentVariable("E2E_TASKS"), out var n) ? n : 200;

var config = new AppConfig
{
    WorkspaceId = "ws1",
    WorkspaceName = "Bench",
    PersonalTasksListId = "plist",
    PersonalTasksListName = "Personal Tasks",
    RefreshSeconds = int.TryParse(Environment.GetEnvironmentVariable("E2E_REFRESH"), out var r) ? r : 600,
    // Default no rewrite; the #304 SubdomainScenario sets this from E2E_SUBDOMAIN when active.
    WorkspaceSubdomain = "",
};

// Discovery by reflection (never a registry): every IE2EScenario in this assembly except the always-on
// DefaultScenario, which the backend constructs directly.
var scenarios = ScenarioHost.Discover();

// Fail-fast on a mistyped selector: print the discovered names and exit non-zero rather than silently
// booting the plain default backend (the trade-off #489 calls out for reflection discovery).
var selector = Environment.GetEnvironmentVariable("E2E_SCENARIO");
if (!string.IsNullOrEmpty(selector) && !ScenarioHost.IsKnownSelector(scenarios, selector))
{
    Console.Error.WriteLine(
        $"Unknown E2E_SCENARIO '{selector}'. Discovered scenarios: "
        + string.Join(", ", scenarios.Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal)) + ".");
    Environment.Exit(2);
    return;
}

// A scenario is active when its own legacy var(s) say so, or the selector names it (additive).
var active = ScenarioHost.Active(scenarios, selector);

foreach (var s in active)
    s.Configure(config);

// Launcher / marker inputs: the first active scenario that supplies one wins (each is supplied by at most one
// scenario). Absent ⇒ a no-op browser launcher, the app's real tab launcher, and the facade's Null markers.
IBrowserLauncher browser = active.Select(s => s.BrowserLauncher).FirstOrDefault(b => b is not null)
                           ?? new NullBrowserLauncher();
ITerminalLauncher? tabLauncher = active.Select(s => s.TabLauncher).FirstOrDefault(t => t is not null);
var markers = active.Select(s => s.Markers).FirstOrDefault(m => m is not null);
var changeMarkers = markers?.Store;

var backend = new FakeClickUp(new HarnessContext { TaskCount = taskCount }, active);
var client = new ClickUpClient("fake-token", new HttpClient(backend), changeMarkers: changeMarkers);

IStateStore stateStore = new JsonFileStateStore();
var configStore = new ConfigStore(stateStore);
var tasks = new TaskService(client, config, 1, userName: "Ben Seymour");
var feed = new FeedService(client, tasks, config);
var focus = new LocalFocusStore(config, configStore);
// Isolated per-process state dir for the persistent task/feed caches (#122/#123), so the harness never
// touches the developer's real data dir and every run starts with a cold cache — a deterministic no-op first
// paint, which keeps the A/B renders byte-identical to the stock renderer.
var cacheStore = new JsonFileStateStore(
    Path.Combine(Path.GetTempPath(), "clickup-todo-e2e", Guid.NewGuid().ToString("N")));
var taskCache = new TaskCache(cacheStore);
var feedCache = new FeedCache(cacheStore);
var assignees = new AssigneeFrequencyCache(
    stateStore, config.WorkspaceId, ct => client.GetWorkspaceMembersAsync(config.WorkspaceId, ct));
var lists = new ListFrequencyCache(stateStore, config.WorkspaceId);

var services = new HarnessServices
{
    Backend = backend,
    Tasks = tasks,
    Feed = feed,
    Config = config,
    ConfigStore = configStore,
    Focus = focus,
    TaskCache = taskCache,
    FeedCache = feedCache,
    Assignees = assignees,
    Lists = lists,
    Browser = browser,
    ChangeMarkers = changeMarkers,
    TabLauncher = tabLauncher,
};

// Pre-boot work that needs the constructed services (e.g. #333 warm-closed's real PrefetchClosedTasksAsync,
// which also arms its refresh stall only after the warm prefetch has run).
foreach (var s in active)
    await s.BeforeBootAsync(services);

// A scenario may take over the app (single-task / feed); otherwise the default dashboard boots.
var host = active.Select(s => s.Host).FirstOrDefault(h => h is not null);
if (host is not null)
    await host.RunAsync(services);
else
    new TodoApp(tasks, feed, config, configStore, focus, taskCache, feedCache, assignees, lists, browser,
        changeMarkers, tabLauncher).Run("ansi");

markers?.Disposable?.Dispose();
