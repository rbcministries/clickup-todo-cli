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
    Console.WriteLine("Usage: clickup-todo [--task <id>] [--reset] [--driver <name>]");
    Console.WriteLine("  (no args)        Launch the task UI (runs first-time setup if needed).");
    Console.WriteLine("  --task <id>      Open straight into that task's detail view (a single-task tab).");
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
    Console.Error.WriteLine("Provide a task id, e.g. `clickup-todo --task 86abc123`.");
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
    TaskDetail launchTask;
    IReadOnlyList<CommentItem> launchComments;
    try
    {
        launchTask = await taskService.GetTaskDetailAsync(launch.TaskId!);
        launchComments = await taskService.GetTaskCommentsWithRepliesAsync(launch.TaskId!);
    }
    catch (ClickUpApiException ex) when (ex.StatusCode == 404)
    {
        Console.Error.WriteLine($"No task found with id '{launch.TaskId}'. Check the id and try again.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not load task '{launch.TaskId}': {ex.Message}");
        return 1;
    }

    // Hand the single-task tab the same cross-process nudge channel the dashboard gets (#377), so an
    // edit to the launched task in another tab surfaces here promptly rather than only on the 30s tick.
    new SingleTaskApp(taskService, config, configStore, launchTask, launchComments,
        changeMarkers: changeMarkers).Run(driverName);
    return 0;
}

var feedService = new FeedService(client, taskService, config);
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
