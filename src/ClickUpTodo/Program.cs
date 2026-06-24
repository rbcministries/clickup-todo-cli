using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;
using ClickUpTodo.Tui;

var configStore = new ConfigStore();
var tokenStore = new TokenStore();

// `clickup-todo --reset` / `--logout`: forget the saved token and settings, then exit.
if (args.Any(a => a is "--reset" or "--logout"))
{
    tokenStore.Delete();
    if (File.Exists(configStore.ConfigPath))
        File.Delete(configStore.ConfigPath);
    Console.WriteLine("Cleared saved ClickUp token and settings. Run `clickup-todo` to sign in again.");
    return 0;
}

if (args.Any(a => a is "--help" or "-h" or "-?"))
{
    Console.WriteLine("clickup-todo — a keyboard-driven ClickUp to-do list.");
    Console.WriteLine();
    Console.WriteLine("Usage: clickup-todo [--reset] [--driver <name>]");
    Console.WriteLine("  (no args)        Launch the to-do UI (runs first-time setup if needed).");
    Console.WriteLine("  --reset          Forget the saved token and settings.");
    Console.WriteLine("  --driver <name>  Force a Terminal.Gui console driver (e.g. v2net, v2win, net).");
    Console.WriteLine("                   Use to work around input latency; also CLICKUP_TODO_DRIVER env var.");
    Console.WriteLine("  --help           Show this help.");
    return 0;
}

// Optional console-driver override to experiment with input latency (#3): --driver <name> or the
// CLICKUP_TODO_DRIVER env var. Null means Terminal.Gui's default for the platform.
var driverName = GetOption(args, "--driver") ?? Environment.GetEnvironmentVariable("CLICKUP_TODO_DRIVER");

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

using var client = new ClickUpClient(token!);

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
new TodoApp(taskService, config, configStore).Run(driverName);
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
