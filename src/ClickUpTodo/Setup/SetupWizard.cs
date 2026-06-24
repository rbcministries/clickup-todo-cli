using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Setup;

/// <summary>
/// First-run console flow: collect and validate a personal API token, choose a workspace and the
/// "Personal Tasks" list, set a refresh interval, then persist config + encrypted token.
/// </summary>
public static class SetupWizard
{
    public static async Task<bool> RunAsync(ConfigStore configStore, TokenStore tokenStore, CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine("  ClickUp To-Do — first-time setup");
        Console.WriteLine("  ────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("  You'll need a ClickUp personal API token.");
        Console.WriteLine("  Get one at: ClickUp -> Settings -> Apps -> API Token (starts with 'pk_').");
        Console.WriteLine();

        ClickUpClient? client = null;
        ClickUpUser? me = null;
        string token = "";

        // 1. Token entry + validation (retry until valid or the user gives up).
        while (me is null)
        {
            token = ReadSecret("  Paste your personal API token: ");
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.WriteLine("  No token entered. Press Ctrl+C to abort, or try again.");
                continue;
            }

            client?.Dispose();
            client = new ClickUpClient(token);
            try
            {
                me = await client.GetMeAsync(ct);
            }
            catch (ClickUpApiException ex) when (ex.IsAuthFailure)
            {
                Console.WriteLine("  That token was rejected by ClickUp. Please check it and try again.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Could not reach ClickUp: {ex.Message}");
                Console.WriteLine("  Check your connection and try again.");
            }
        }

        Console.WriteLine($"  Signed in as {me.DisplayName}.");
        Console.WriteLine();

        // 2. Workspace selection.
        var workspaces = await client!.GetWorkspacesAsync(ct);
        if (workspaces.Count == 0)
        {
            Console.WriteLine("  No workspaces are available for this token. Aborting.");
            return false;
        }

        var workspace = workspaces.Count == 1
            ? workspaces[0]
            : ChooseFromMenu("  Select a workspace:", workspaces, w => w.Name);
        Console.WriteLine($"  Workspace: {workspace.Name}");
        Console.WriteLine();

        // 3. Personal Tasks list selection (flattened space/folder/list hierarchy).
        Console.WriteLine("  Loading lists…");
        var lists = await LoadAllListsAsync(client, workspace.Id, ct);
        if (lists.Count == 0)
        {
            Console.WriteLine("  No lists found in this workspace. Aborting.");
            return false;
        }

        var personal = ChooseFromMenu(
            "  Which list is your \"Personal Tasks\" list?", lists, l => l.Label);
        Console.WriteLine($"  Personal Tasks list: {personal.Label}");
        Console.WriteLine();

        // 4. Refresh interval.
        var refresh = ReadInt("  Refresh interval in seconds", defaultValue: 60, min: 10, max: 3600);

        // 5. Persist.
        var config = new AppConfig
        {
            WorkspaceId = workspace.Id,
            WorkspaceName = workspace.Name,
            PersonalTasksListId = personal.Id,
            PersonalTasksListName = personal.Name,
            RefreshSeconds = refresh,
        };
        configStore.Save(config);
        tokenStore.Save(token);
        client.Dispose();

        Console.WriteLine();
        Console.WriteLine($"  Setup complete. Settings saved to {configStore.ConfigPath}");
        Console.WriteLine("  Starting…");
        await Task.Delay(600, ct);
        return true;
    }

    /// <summary>A flattened list entry with a breadcrumb label for display.</summary>
    private sealed record ListChoice(string Id, string Name, string Label);

    private static async Task<List<ListChoice>> LoadAllListsAsync(
        ClickUpClient client, string workspaceId, CancellationToken ct)
    {
        var result = new List<ListChoice>();
        foreach (var space in await client.GetSpacesAsync(workspaceId, ct))
        {
            foreach (var list in await client.GetFolderlessListsAsync(space.Id, ct))
                result.Add(new ListChoice(list.Id, list.Name, $"{space.Name} / {list.Name}"));

            foreach (var folder in await client.GetFoldersAsync(space.Id, ct))
                foreach (var list in await client.GetListsInFolderAsync(folder.Id, ct))
                    result.Add(new ListChoice(list.Id, list.Name, $"{space.Name} / {folder.Name} / {list.Name}"));
        }
        return result;
    }

    // ── Console input helpers ──────────────────────────────────────────────

    private static T ChooseFromMenu<T>(string prompt, IReadOnlyList<T> items, Func<T, string> label)
    {
        Console.WriteLine(prompt);
        for (var i = 0; i < items.Count; i++)
            Console.WriteLine($"    {i + 1,3}. {label(items[i])}");

        while (true)
        {
            Console.Write($"  Enter a number (1-{items.Count}): ");
            var input = Console.ReadLine();
            if (int.TryParse(input, out var choice) && choice >= 1 && choice <= items.Count)
                return items[choice - 1];
            Console.WriteLine("  Invalid selection. Try again.");
        }
    }

    private static int ReadInt(string prompt, int defaultValue, int min, int max)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                return defaultValue;
            if (int.TryParse(input, out var value) && value >= min && value <= max)
                return value;
            Console.WriteLine($"  Enter a whole number between {min} and {max}.");
        }
    }

    /// <summary>Reads a line of input, echoing '*' for each character (with backspace support).</summary>
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);

        // Console.ReadKey(intercept) requires an interactive console; fall back if redirected.
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return new string(chars.ToArray());
                case ConsoleKey.Backspace when chars.Count > 0:
                    chars.RemoveAt(chars.Count - 1);
                    Console.Write("\b \b");
                    break;
                case ConsoleKey.Backspace:
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        chars.Add(key.KeyChar);
                        Console.Write('*');
                    }
                    break;
            }
        }
    }
}
