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
    Console.WriteLine("Cleared saved ClickUp token and settings. Run `clickup-todo` to sign in again.");
    return 0;
}

if (args.Any(a => a is "--help" or "-h" or "-?"))
{
    Console.WriteLine($"clickup-todo — {AppBranding.DisplayName}, a keyboard-driven ClickUp task list.");
    Console.WriteLine();
    Console.WriteLine("Usage: clickup-todo [--reset] [--driver <name>]");
    Console.WriteLine("  (no args)        Launch the task UI (runs first-time setup if needed).");
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

// Build the client with the provider that matches how the saved token was obtained (raw personal
// token vs OAuth Bearer), recorded in config.AuthMode.
using var client = ClickUpClientFactory.Create(config, token!);

long userId;
try
{
    userId = (await client.GetMeAsync()).Id;
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

var taskService = new TaskService(client, config, userId);
var feedService = new FeedService(client, taskService, config);
var focusStore = new LocalFocusStore(config, configStore);
new TodoApp(taskService, feedService, config, configStore, focusStore).Run(driverName);
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
