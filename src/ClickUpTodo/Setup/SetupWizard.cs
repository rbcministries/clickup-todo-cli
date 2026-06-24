using System.Text.RegularExpressions;
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

        // 3. Personal Tasks list selection.
        var personal = await ChoosePersonalListAsync(client, workspace.Id, ct);
        if (personal is null)
        {
            Console.WriteLine("  No list selected. Aborting.");
            return false;
        }
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

    /// <summary>
    /// Lets the user identify their Personal Tasks list either by pasting a list URL/id directly
    /// (fast), or by browsing a single chosen space. We deliberately do NOT enumerate every space's
    /// folders up front — large workspaces have dozens of spaces and that trips ClickUp's rate limit.
    /// </summary>
    private static async Task<ListChoice?> ChoosePersonalListAsync(
        ClickUpClient client, string workspaceId, CancellationToken ct)
    {
        Console.WriteLine("  Now choose your \"Personal Tasks\" list.");
        Console.WriteLine("  Tip: open the list in ClickUp and copy its URL, or paste its id directly.");
        Console.Write("  Paste a list URL/id, or press Enter to browse by space: ");
        var direct = Console.ReadLine();

        if (!string.IsNullOrWhiteSpace(direct))
        {
            var listId = ExtractListId(direct);
            try
            {
                var info = await client.GetListAsync(listId, ct);
                return new ListChoice(info.Id, info.Name, info.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Couldn't load list '{listId}' ({ex.Message}). Let's browse instead.");
            }
        }

        // Browse: pick one space, then enumerate only that space's lists (folderless + per folder).
        var spaces = await client.GetSpacesAsync(workspaceId, ct);
        if (spaces.Count == 0)
        {
            Console.WriteLine("  No spaces found in this workspace.");
            return null;
        }

        var space = spaces.Count == 1
            ? spaces[0]
            : ChooseFromMenu("  Which space is your list in?", spaces, s => s.Name);

        Console.WriteLine($"  Loading lists in {space.Name}…");
        var lists = await LoadListsInSpaceAsync(client, space, ct);
        if (lists.Count == 0)
        {
            Console.WriteLine("  No lists found in that space.");
            return null;
        }

        return ChooseFromMenu("  Which list is your \"Personal Tasks\" list?", lists, l => l.Label);
    }

    private static async Task<List<ListChoice>> LoadListsInSpaceAsync(
        ClickUpClient client, NamedEntity space, CancellationToken ct)
    {
        var result = new List<ListChoice>();
        foreach (var list in await client.GetFolderlessListsAsync(space.Id, ct))
            result.Add(new ListChoice(list.Id, list.Name, $"{space.Name} / {list.Name}"));

        foreach (var folder in await client.GetFoldersAsync(space.Id, ct))
            foreach (var list in await client.GetListsInFolderAsync(folder.Id, ct))
                result.Add(new ListChoice(list.Id, list.Name, $"{space.Name} / {folder.Name} / {list.Name}"));

        return result;
    }

    /// <summary>
    /// Extracts a numeric ClickUp list id from a pasted URL or raw id. ClickUp list URLs vary:
    /// <c>.../v/l/901401775377</c>, <c>.../v/li/901401775377</c>, or a composite view segment like
    /// <c>.../9014107164/v/l/6-901401775377-1</c> (where 9014107164 is the workspace id, not the list).
    /// </summary>
    internal static string ExtractListId(string input)
    {
        input = input.Trim();

        // A bare id was pasted.
        if (Regex.IsMatch(input, @"^\d+$"))
            return input;

        // Prefer the segment after /v/l/ or /v/li/, then take its longest numeric run (the list id),
        // which avoids grabbing the workspace id earlier in the path.
        var segment = Regex.Match(input, @"/v/li?/([^/?#]+)");
        var search = segment.Success ? segment.Groups[1].Value : input;

        var longest = Regex.Matches(search, @"\d+")
            .Select(m => m.Value)
            .OrderByDescending(n => n.Length)
            .FirstOrDefault();

        return string.IsNullOrEmpty(longest) ? input : longest;
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
