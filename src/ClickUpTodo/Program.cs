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
    Console.WriteLine("Usage: clickup-todo [--reset]");
    Console.WriteLine("  (no args)   Launch the to-do UI (runs first-time setup if needed).");
    Console.WriteLine("  --reset     Forget the saved token and settings.");
    Console.WriteLine("  --help      Show this help.");
    return 0;
}

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
new TodoApp(taskService, config, configStore).Run();
return 0;
