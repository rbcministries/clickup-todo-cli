using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Agent;

/// <summary>
/// Composes the prompt that seeds a dispatched <c>claude</c> session (issue #24, S1 of the #23
/// epic) and writes it to a temp file for the <see cref="ITerminalLauncher"/> to feed in.
///
/// The composed text is: the user's prompt, a blank line, a fixed preamble, a blank line, then a
/// single JSON object <c>{ "task": {…}, "comments": [...] }</c>. Keeping the JSON in a file (not on
/// the command line) is what makes launching safe — the launcher reads the file at run time
/// (<c>Get-Content -Raw</c> / <c>$(cat …)</c>) rather than inlining the content as an argument.
///
/// Pure and API-free: it consumes the already-fetched <see cref="TaskDetail"/> + comments (from the
/// #17 detail fetch) so it is fully unit-testable in isolation.
/// </summary>
public static class AgentPromptComposer
{
    /// <summary>The fixed line between the user prompt and the JSON payload.</summary>
    public const string Preamble =
        "JSON below has task details and comment history; use MCP tools if more detail required.";

    /// <summary>The task description is truncated to this many characters to keep the prompt tight.</summary>
    public const int MaxDescriptionLength = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed escaping keeps the payload human/agent-readable (URLs, quotes, non-ASCII stay as
        // typed); quotes, backslashes and control chars are still escaped, so the JSON stays valid.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Builds the full prompt text: <c>{userPrompt}\n\n{Preamble}\n\n{json}</c>. Uses <c>\n</c>
    /// throughout so the output is identical across platforms.
    /// </summary>
    public static string Compose(TaskDetail task, IReadOnlyList<CommentItem> comments, string userPrompt)
    {
        ArgumentNullException.ThrowIfNull(task);
        var prompt = (userPrompt ?? string.Empty).Trim();
        return $"{prompt}\n\n{Preamble}\n\n{BuildJson(task, comments)}";
    }

    /// <summary>
    /// Composes the prompt and writes it to <paramref name="directory"/> (default
    /// <c>&lt;temp&gt;/clickup-todo</c>), creating the directory if needed. Returns the file path.
    /// The file is intentionally left in place for the launched session to read; OS temp-dir cleanup
    /// reclaims it.
    /// </summary>
    public static string WritePromptFile(
        TaskDetail task, IReadOnlyList<CommentItem> comments, string userPrompt, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var dir = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Path.GetTempPath(), "clickup-todo")
            : directory;
        Directory.CreateDirectory(dir);

        var fileName = $"agent-prompt-{SafeToken(task.Id)}-{Guid.NewGuid():N}.txt";
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, Compose(task, comments, userPrompt));
        return path;
    }

    /// <summary>Serializes the <c>{ "task": {…}, "comments": [...] }</c> payload.</summary>
    internal static string BuildJson(TaskDetail task, IReadOnlyList<CommentItem> comments)
    {
        comments ??= [];

        var hasList = task.ListId is not null || task.ListName is not null;
        var payload = new
        {
            task = new
            {
                id = task.Id,
                custom_id = task.CustomId,
                name = task.Name,
                status = task.StatusName,
                list = hasList ? new { id = task.ListId, name = task.ListName } : null,
                url = task.Url,
                due_date = task.DueDateMs,
                priority = task.Priority,
                assignees = task.Assignees,
                tags = task.Tags,
                description = Truncate(task.Description, MaxDescriptionLength),
            },
            comments = comments.Select(c => new
            {
                id = c.Id,
                author = c.Author,
                date = c.DateMs,
                text = c.Text,
                resolved = c.Resolved,
            }),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Truncates to <paramref name="max"/> chars (appending <c>…</c>); empty → null (omitted).</summary>
    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
    }

    /// <summary>Reduces a task id to a filesystem-safe token for the temp filename.</summary>
    private static string SafeToken(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "task";
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }
}
