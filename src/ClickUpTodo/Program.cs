using ClickUpTodo;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;
using ClickUpTodo.Tui;

// The persistence backend is chosen here, once — the single drop-in point the #120 seam left for the
// #119 verdict (LiteDB), adopted in #121. Every call site keeps flowing through IStateStore unchanged.
var dataDirectory = JsonFileStateStore.DefaultDirectory();
using var liteStore = new LiteDbStateStore(Path.Combine(dataDirectory, "state.db"));
var legacyStore = new JsonFileStateStore(dataDirectory);
IStateStore stateStore = liteStore;
var configStore = new ConfigStore(stateStore);
var taskCache = new TaskCache(stateStore);
var feedCache = new FeedCache(stateStore);
var tokenStore = new TokenStore();

// `clickup-todo --reset` / `--logout`: forget the saved token and settings, then exit. Runs before
// the legacy import below so a corrupt config.json can't block recovery, and clears the legacy file
// too (parity with the pre-LiteDB behaviour, where deleting the config removed config.json) so a
// later launch can't re-import the just-forgotten settings.
if (args.Any(a => a is "--reset" or "--logout"))
{
    tokenStore.Delete();
    configStore.Delete();
    legacyStore.Delete(StateKeys.Config);
    // Drop every cached payload too, so logout leaves no stale snapshot behind for a different account
    // or workspace (a fresh workspace would miss on the fingerprint anyway; this just doesn't orphan the
    // documents). #124 completes this so --reset clears *all* cache keys — the working set + feed
    // (#122/#123), the status/color metadata (#125), and the assignee-frequency pool (#155) — via the
    // centralised, key-listed CacheReset so the cleared set stays verifiable in one place.
    CacheReset.ClearAll(stateStore);
    Console.WriteLine("Cleared saved ClickUp token and settings. Run `clickup-todo` to sign in again.");
    return 0;
}

if (args.Any(a => a is "--help" or "-h" or "-?"))
{
    Console.WriteLine($"clickup-todo — {AppBranding.DisplayName}, a keyboard-driven ClickUp task list.");
    Console.WriteLine();
    Console.WriteLine("Usage: clickup-todo [--task <ref>] [--feed] [--reset] [--driver <name>]");
    Console.WriteLine("  (no args)        Launch the task UI (runs first-time setup if needed).");
    Console.WriteLine("  --task <ref>     Open straight into that task's detail view (a single-task tab;");
    Console.WriteLine("                   titles the terminal window/tab with the task's id + name).");
    Console.WriteLine("                   <ref> is a task id (86abc123), a custom id (ABC-123), or a");
    Console.WriteLine("                   ClickUp task URL — the same forms the in-app Ctrl+O accepts.");
    Console.WriteLine("  --feed           Open straight into the mentions & comments feed as its own");
    Console.WriteLine("                   host (the same view Ctrl+E opens in the dashboard), so you can");
    Console.WriteLine("                   keep it in its own window/tab beside your work.");
    Console.WriteLine("  --reset          Forget the saved token and settings.");
    Console.WriteLine("  --driver <name>  Force a Terminal.Gui console driver. One of:");
    Console.WriteLine("                     windows  native Win32 input (try this if input feels laggy)");
    Console.WriteLine("                     dotnet   System.Console cross-platform driver");
    Console.WriteLine("                     ansi     pure ANSI escape-sequence driver (default)");
    Console.WriteLine("                   Also settable via the CLICKUP_TODO_DRIVER env var.");
    Console.WriteLine("  --help           Show this help.");
    return 0;
}

// Optional console-driver override (--driver <name> or the CLICKUP_TODO_DRIVER env var). Lets the
// user pick a Terminal.Gui driver if one behaves better on their terminal. Null = platform default.
var validDrivers = new[] { "windows", "dotnet", "ansi" };
var driverName = (GetOption(args, "--driver") ?? Environment.GetEnvironmentVariable("CLICKUP_TODO_DRIVER"))
    ?.Trim().ToLowerInvariant();
if (!string.IsNullOrEmpty(driverName) && !validDrivers.Contains(driverName))
{
    Console.Error.WriteLine($"Unknown driver '{driverName}'. Valid drivers: {string.Join(", ", validDrivers)} (default: ansi).");
    return 1;
}

// Single-task launch mode (#296): `--task <id>` boots straight into one task's Task Detail view
// instead of the dashboard. Parse it up front so a bare `--task` (no id) fails clearly before any
// setup/auth runs; the actual fetch + launch happen below, once the client is built.
var launch = TaskLaunchArg.Parse(args);
if (launch.MissingValue)
{
    Console.Error.WriteLine("Provide a task reference, e.g. `clickup-todo --task 86abc123` (id), `--task ABC-123` (custom id), or a ClickUp task URL.");
    return 1;
}

// Classify the launch token through the same parser Ctrl+O uses (#464), so `--task` accepts a plain id,
// a custom id, or a task URL with one shared classifier. A token that isn't any of those fails here —
// before any setup/auth runs, and with a message distinct from "that reference didn't resolve".
var launchRef = launch.HasId ? QuickOpenParser.Parse(launch.TaskId!) : QuickOpenRef.Invalid;
if (launch.HasId && launchRef.Kind == QuickOpenKind.Invalid)
{
    Console.Error.WriteLine($"'{launch.TaskId}' isn't a task reference. Pass a task id (86abc123), a custom id (ABC-123), or a ClickUp task URL.");
    return 1;
}

// One-time import of any existing file-backed config.json into the LiteDB store (idempotent; the old
// file is left in place so a downgrade still finds its settings). Runs after --reset/--help so those
// paths never touch a possibly-corrupt legacy file, and before the first configStore.Load() below.
SettingsMigration.ImportLegacyConfig(liteStore, legacyStore);

// First run (or after --reset): collect a token and pick the workspace + Personal Tasks list.
var token = tokenStore.Load();
var config = configStore.Load();
if (string.IsNullOrWhiteSpace(token) || !config.IsConfigured)
{
    if (!await SetupWizard.RunAsync(configStore, tokenStore))
        return 1;
    token = tokenStore.Load();
    config = configStore.Load();
}

// The cross-process nudge channel (#294): a per-process id stamped on every change marker this
// instance writes, so a consumer (#295) can skip its own nudges. The marker store rides the same
// state.db connection the rest of the app's state uses.
var instanceId = Guid.NewGuid().ToString("N");
var changeMarkers = liteStore.CreateChangeMarkerStore(instanceId);

// Build the client with the provider that matches how the saved token was obtained (raw personal
// token vs OAuth Bearer), recorded in config.AuthMode. The client nudges the change channel after
// each confirmed write so other running instances can re-fetch the changed task (#294).
using var client = ClickUpClientFactory.Create(config, token!, changeMarkers: changeMarkers);

long userId;
string userName;
try
{
    var me = await client.GetMeAsync();
    userId = me.Id;
    userName = me.DisplayName;
}
catch (ClickUpApiException ex) when (ex.IsAuthFailure)
{
    Console.Error.WriteLine("Your saved ClickUp token was rejected. Run `clickup-todo --reset` to sign in again.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not reach ClickUp: {ex.Message}");
    return 1;
}

var taskService = new TaskService(client, config, userId, stateStore: stateStore, userName: userName);

// If launched with `--task <id>`, boot straight into that one task's detail view and skip the
// dashboard's working-set services entirely (#296) — the minimal service graph a single-task tab
// needs is just the TaskService above. Fetch the task + comments first so an unknown/unreachable id
// exits with a clear message before the terminal is switched into the alt-screen.
if (launch.HasId)
{
    // A URL-carried team id wins over the configured workspace (#464), matching the in-app Ctrl+O
    // precedence (TodoApp.ResolveAndOpen). A custom id can't be resolved without one, so fail with a
    // message naming *that* cause rather than a misleading "task not found".
    var teamId = string.IsNullOrWhiteSpace(launchRef.TeamId) ? config.WorkspaceId : launchRef.TeamId;
    if (launchRef.Kind == QuickOpenKind.CustomId && string.IsNullOrWhiteSpace(teamId))
    {
        Console.Error.WriteLine($"Can't resolve custom id '{launch.TaskId}' — no workspace is configured. Run `clickup-todo --reset` to sign in and pick one.");
        return 1;
    }

    TaskDetail launchTask;
    IReadOnlyList<CommentItem> launchComments;
    try
    {
        // Cache-first resolution, mirroring Ctrl+O: a snapshot hit yields the plain id and one correct
        // GET; a custom id / URL resolves live. Comments are then fetched by the *resolved* plain id.
        var snapshot = taskCache.Load(config) ?? [];
        launchTask = await taskService.ResolveLaunchTaskAsync(launchRef, snapshot, teamId);
        launchComments = await taskService.GetTaskCommentsWithRepliesAsync(launchTask.Id);
    }
    catch (ClickUpApiException ex) when (ex.StatusCode == 404)
    {
        Console.Error.WriteLine($"No task found matching '{launch.TaskId}'. Check the reference and try again.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not load task '{launch.TaskId}': {ex.Message}");
        return 1;
    }

    // The assignee-frequency candidate pool (#155) — same type the dashboard uses — powers @-mention
    // authoring in the single-task tab's Ctrl+N composer (#473). Its constructor loads any pool a prior
    // dashboard session persisted for this workspace (warm, frequency-ranked); single-task mode tallies no
    // working set, so absent a persisted pool it warms from the workspace members via TopUpAsync on boot.
    var singleTaskAssignees = new AssigneeFrequencyCache(
        stateStore, config.WorkspaceId, ct => client.GetWorkspaceMembersAsync(config.WorkspaceId, ct));

    // Hand the single-task tab the same cross-process nudge channel the dashboard gets (#377), so an
    // edit to the launched task in another tab surfaces here promptly rather than only on the 30s tick.
    new SingleTaskApp(taskService, config, configStore, launchTask, launchComments,
        changeMarkers: changeMarkers, assignees: singleTaskAssignees).Run(driverName);
    return 0;
}

// The feed aggregator, shared by the standalone --feed host below and the dashboard. Built here (ahead
// of the launch-mode branch) so both paths use the one instance; --task returned above without it.
var feedService = new FeedService(client, taskService, config);

// Standalone feed host (`--feed`, #509): boot straight into the mentions & comments feed as its own
// root host — the same NotificationsFeedScreen the dashboard opens with Ctrl+E — instead of the
// dashboard. `--task` wins if both flags are given (it returned above). Seed the host with the warm feed
// cache snapshot (comments only; #123) so the first paint is instant; FeedApp kicks a live refresh on
// show and wires the cross-process nudge channel (#377). The dashboard's Ctrl+E is unchanged — this is an
// additional launch path, not a replacement.
if (FeedLaunchArg.Parse(args).Present)
{
    var cachedFeed = feedCache.LoadSnapshot(config);
    var feedSeed = cachedFeed is { Items.Count: > 0 } ? new FeedResult(cachedFeed.Items, []) : FeedResult.Empty;
    new FeedApp(feedService, feedCache, config, configStore, feedSeed, changeMarkers: changeMarkers).Run(driverName);
    return 0;
}

var focusStore = new LocalFocusStore(config, configStore);
// The assignee-frequency candidate pool (#155) — warmed from the loaded tasks and topped up from
// the workspace members — rides the same state store, scoped to the active workspace.
var assigneeCache = new AssigneeFrequencyCache(
    stateStore, config.WorkspaceId, ct => client.GetWorkspaceMembersAsync(config.WorkspaceId, ct));
// The list-frequency candidate pool (#238) — warmed from the lists on the loaded tasks and backfilled
// by the scheduled list-hierarchy walk (#236) — rides the same state store, scoped to the workspace.
var listCache = new ListFrequencyCache(stateStore, config.WorkspaceId);
new TodoApp(taskService, feedService, config, configStore, focusStore, taskCache, feedCache, assigneeCache, listCache,
    changeMarkers: changeMarkers).Run(driverName);
return 0;

// Reads "--opt value" or "--opt=value" from args.
static string? GetOption(string[] argv, string name)
{
    for (var i = 0; i < argv.Length; i++)
    {
        if (argv[i] == name && i + 1 < argv.Length)
            return argv[i + 1];
        if (argv[i].StartsWith(name + "=", StringComparison.Ordinal))
            return argv[i][(name.Length + 1)..];
    }
    return null;
}
