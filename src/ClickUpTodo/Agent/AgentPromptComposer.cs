using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Agent;

/// <summary>
/// Composes the prompt that seeds a dispatched <c>claude</c> session (issue #24, S1 of the #23
/// epic) and writes it to a temp file for the <see cref="ITerminalLauncher"/> to feed in.
///
/// The prompt is produced from an editable <b>template</b> (#100) whose tokens are substituted with
/// the task's data: <see cref="DefaultTemplate"/> renders the user's prompt, an optional output-subdir
/// instruction (#98), a fixed preamble, then a single JSON object <c>{ "task": {…}, "comments": [...] }</c>
/// — the same text the pre-#100 composer emitted. A user's saved template overrides it. Keeping the JSON
/// in a file (not on the command line) is what makes launching safe — the launcher reads the file at run
/// time (<c>Get-Content -Raw</c> / <c>$(cat …)</c>) rather than inlining the content as an argument.
///
/// Pure and API-free: it consumes the already-fetched <see cref="TaskDetail"/> + comments (from the
/// #17 detail fetch) so it is fully unit-testable in isolation.
/// </summary>
public static class AgentPromptComposer
{
    /// <summary>The fixed preamble line, written inline in <see cref="DefaultTemplate"/> (not a
    /// placeholder — the user edits it as literal text, #100).</summary>
    public const string Preamble =
        "JSON below has task details and comment history; use MCP tools if more detail required.";

    /// <summary>
    /// The default prompt template. When neither the output subdirectory (#98) nor the
    /// post-to-Comments toggle (#97) is supplied, rendering it is <b>byte-for-byte identical</b> to
    /// the pre-#100 composer output (<c>{userPrompt}\n\n{Preamble}\n\n{contextJson}</c>) — both
    /// instruction placeholders expand to empty. When a subdirectory is supplied,
    /// <c>{outputDirInstruction}</c> expands to a "write outputs to ./{subdir}" paragraph; when the
    /// post-to-Comments toggle is on, <c>{postCommentInstruction}</c> expands to a "post a summary
    /// comment" paragraph — both slot in between the prompt and the preamble. Uses <c>\n</c>
    /// throughout so the output is identical across platforms.
    /// </summary>
    public const string DefaultTemplate =
        "{userPrompt}\n\n{outputDirInstruction}{postCommentInstruction}" + Preamble + "\n\n{contextJson}";

    /// <summary>The task description is truncated to this many characters to keep the prompt tight.</summary>
    public const int MaxDescriptionLength = 2000;

    /// <summary>
    /// The placeholders a template may use, each with a one-line description. The editor screen (#100)
    /// renders this as its reference list, and it is the authoritative "known token" set for
    /// <see cref="Render"/> (unknown tokens are left literal).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Placeholders =
    [
        ("userPrompt", "The free-text prompt typed in the Dispatch pane"),
        ("taskJson", "The task object, as JSON"),
        ("commentsJson", "The comments, as a JSON array"),
        ("contextJson", "The combined { task, comments } object (what the default uses)"),
        ("taskId", "The raw task id"),
        ("customId", "The task's custom id (falls back to the task id)"),
        ("postCommentInstruction", "Post-results-to-Comments instruction (empty unless enabled)"),
        ("outputDirInstruction", "Output-subdirectory instruction + blank line (empty unless enabled)"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed escaping keeps the payload human/agent-readable (URLs, quotes, non-ASCII stay as
        // typed); quotes, backslashes and control chars are still escaped, so the JSON stays valid.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Builds the full prompt text by rendering <paramref name="template"/> (blank ⇒
    /// <see cref="DefaultTemplate"/>) with the task's placeholder values substituted in. The user
    /// prompt is trimmed; the <c>custom id</c> placeholder falls back to the task id (#98); a non-blank
    /// <paramref name="outputSubdirectory"/> (the task-derived working-dir mode, #98) fills
    /// <c>{outputDirInstruction}</c> with a "write outputs to ./{subdir}" paragraph, else it is empty;
    /// <paramref name="postToComments"/> (the #97 toggle) fills <c>{postCommentInstruction}</c> with a
    /// "post a summary comment to the ClickUp task" paragraph, else it is empty.
    /// </summary>
    public static string Compose(
        TaskDetail task, IReadOnlyList<CommentItem> comments, string userPrompt,
        string? template = null, string? outputSubdirectory = null, bool postToComments = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        comments ??= [];
        var tmpl = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["userPrompt"] = (userPrompt ?? string.Empty).Trim(),
            ["taskJson"] = BuildTaskJson(task),
            ["commentsJson"] = BuildCommentsJson(comments),
            ["contextJson"] = BuildJson(task, comments),
            ["taskId"] = task.Id ?? string.Empty,
            ["customId"] = string.IsNullOrWhiteSpace(task.CustomId) ? (task.Id ?? string.Empty) : task.CustomId,
            ["postCommentInstruction"] = PostCommentInstruction(task, postToComments),
            ["outputDirInstruction"] = OutputDirInstruction(outputSubdirectory),
        };

        return Render(tmpl, values);
    }

    /// <summary>
    /// Renders <paramref name="template"/> in a single left-to-right pass: <c>{{</c> and <c>}}</c>
    /// escape to a literal brace, and <c>{name}</c> — a brace pair whose contents are a single-line
    /// token with no nested <c>{</c> — is replaced by <paramref name="values"/>[name] when known and
    /// emitted <b>literally</b> otherwise. Any other <c>{</c> (no closing brace, or a span that reaches
    /// across a newline or a nested <c>{</c> before the next <c>}</c>) is emitted as a literal single
    /// character and scanning resumes at the next character — so a stray brace never swallows a real
    /// placeholder that follows it. Substituted values are never rescanned, so JSON braces inside a
    /// value (e.g. <c>{contextJson}</c>) are not re-interpreted as placeholders.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var sb = new StringBuilder(template.Length + 512);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    sb.Append('{');
                    i += 2;
                    continue;
                }

                var end = template.IndexOf('}', i + 1);
                if (end > i)
                {
                    var name = template[(i + 1)..end];
                    // A real token name is a single line with no nested '{'. If the span isn't one, this
                    // '{' doesn't open a token — emit it literally and let the scan find a genuine token
                    // later in the span (otherwise a lone '{' would capture forward to a downstream
                    // placeholder's '}' and swallow it).
                    if (!name.Contains('{') && !name.Contains('\n'))
                    {
                        if (values.TryGetValue(name, out var value))
                            sb.Append(value);
                        else
                            sb.Append(template, i, end - i + 1); // unknown token: leave literal
                        i = end + 1;
                        continue;
                    }
                }

                sb.Append('{'); // no closing brace (or not a token span): literal
                i++;
                continue;
            }

            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    sb.Append('}');
                    i += 2;
                    continue;
                }

                sb.Append('}');
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The <c>{outputDirInstruction}</c> value for a task-derived launch (#98): a
    /// "write outputs to <c>./{subdir}</c>" instruction <b>followed by a blank line</b> so it slots in
    /// as its own paragraph ahead of the preamble, or empty when no subdirectory is supplied (keeping
    /// the default layout byte-identical to zero-config dispatch).
    /// </summary>
    internal static string OutputDirInstruction(string? outputSubdirectory)
    {
        var token = (outputSubdirectory ?? string.Empty).Trim();
        return token.Length == 0
            ? string.Empty
            : $"Write any output files to the subdirectory ./{token} (create it if needed).\n\n";
    }

    /// <summary>
    /// The <c>{postCommentInstruction}</c> value for the post-results-to-Comments toggle (#97): an
    /// instruction telling the dispatched agent to post a brief summary comment back to the ClickUp
    /// task <b>followed by a blank line</b> so it slots in as its own paragraph ahead of the preamble,
    /// or empty when the toggle is off (keeping the default layout byte-identical to a plain dispatch).
    /// The instruction keys on the raw task id — that is what ClickUp's comment API / MCP tools expect
    /// — and notes the agent needs ClickUp MCP tool access, since the app never posts the comment
    /// itself; it only instructs the agent to.
    /// </summary>
    internal static string PostCommentInstruction(TaskDetail task, bool postToComments)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!postToComments)
            return string.Empty;
        var id = task.Id ?? string.Empty;
        return $"When you finish, post a brief summary comment of your work to ClickUp task {id} " +
            "using your ClickUp MCP tools (requires ClickUp MCP access to this workspace).\n\n";
    }

    /// <summary>
    /// The per-task output subdirectory token for the <see cref="Configuration.AgentWorkingDirectory.TaskDerived"/>
    /// mode (#98): the task's custom id when set, else its id, reduced to a filesystem-safe token
    /// (via the same <see cref="SafeToken"/> used for temp filenames, so separators / traversal
    /// can't escape the base dir). The agent is told to write outputs under <c>./{token}</c> so each
    /// task's work stays separated inside the shared base working directory.
    /// </summary>
    public static string OutputSubdirectoryToken(TaskDetail task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var raw = string.IsNullOrWhiteSpace(task.CustomId) ? task.Id : task.CustomId;
        return SafeToken(raw ?? string.Empty);
    }

    /// <summary>
    /// The <see cref="DefaultTemplate"/> with its inline preamble line replaced by
    /// <paramref name="preamble"/> — used by the #27→#100 migration to carry a saved single-line
    /// preamble forward as an equivalent full template. A blank value yields the unchanged default.
    /// </summary>
    public static string DefaultTemplateWithPreamble(string? preamble)
    {
        var lead = (preamble ?? string.Empty).Trim();
        return lead.Length == 0 ? DefaultTemplate : DefaultTemplate.Replace(Preamble, lead);
    }

    /// <summary>
    /// Composes the prompt and writes it to <paramref name="directory"/> (default
    /// <c>&lt;temp&gt;/clickup-todo</c>), creating the directory if needed. Returns the file path.
    /// The file is intentionally left in place for the launched session to read; OS temp-dir cleanup
    /// reclaims it.
    /// </summary>
    public static string WritePromptFile(
        TaskDetail task, IReadOnlyList<CommentItem> comments, string userPrompt,
        string? directory = null, string? template = null, string? outputSubdirectory = null,
        bool postToComments = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        var dir = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Path.GetTempPath(), "clickup-todo")
            : directory;
        Directory.CreateDirectory(dir);

        var fileName = $"agent-prompt-{SafeToken(task.Id)}-{Guid.NewGuid():N}.txt";
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, Compose(task, comments, userPrompt, template, outputSubdirectory, postToComments));
        return path;
    }

    /// <summary>Serializes the combined <c>{ "task": {…}, "comments": [...] }</c> payload
    /// (the <c>{contextJson}</c> placeholder).</summary>
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

    /// <summary>Serializes just the <c>task</c> object (the <c>{taskJson}</c> placeholder). The field
    /// shape is kept in sync with the <c>task</c> object built in <see cref="BuildJson"/>.</summary>
    internal static string BuildTaskJson(TaskDetail task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var hasList = task.ListId is not null || task.ListName is not null;
        var payload = new
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
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Serializes just the <c>comments</c> array (the <c>{commentsJson}</c> placeholder). The
    /// element shape is kept in sync with the <c>comments</c> array built in <see cref="BuildJson"/>.</summary>
    internal static string BuildCommentsJson(IReadOnlyList<CommentItem> comments)
    {
        comments ??= [];
        var payload = comments.Select(c => new
        {
            id = c.Id,
            author = c.Author,
            date = c.DateMs,
            text = c.Text,
            resolved = c.Resolved,
        });
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Truncates to at most <paramref name="max"/> chars then appends <c>…</c>; empty → null
    /// (omitted). The cut steps back off a trailing high surrogate so truncation never splits a
    /// surrogate pair (which would leave a stray replacement char before the ellipsis).
    /// </summary>
    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.Length <= max)
            return value;
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return string.Concat(value.AsSpan(0, cut), "…");
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
