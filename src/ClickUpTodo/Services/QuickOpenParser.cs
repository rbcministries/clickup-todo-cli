using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>How a <see cref="QuickOpenRef"/> should be resolved.</summary>
public enum QuickOpenKind
{
    /// <summary>A plain ClickUp task id (e.g. <c>86abc123</c>) — resolvable directly.</summary>
    TaskId,

    /// <summary>A workspace custom id (e.g. <c>ABC-123</c>) — resolvable via
    /// <c>custom_task_ids=true&amp;team_id=…</c>.</summary>
    CustomId,

    /// <summary>The input couldn't be understood as a task reference; navigation is refused.</summary>
    Invalid,
}

/// <summary>A parsed quick-open target: its <see cref="Kind"/>, the extracted id/custom-id
/// <see cref="Value"/> (empty for <see cref="QuickOpenKind.Invalid"/>), and — only for a custom id
/// carried in a <c>/t/{team_id}/{custom_id}</c> URL — the URL's own <see cref="TeamId"/> (else null).</summary>
public readonly record struct QuickOpenRef(QuickOpenKind Kind, string Value, string? TeamId = null)
{
    /// <summary>The "couldn't parse" result.</summary>
    public static readonly QuickOpenRef Invalid = new(QuickOpenKind.Invalid, "");

    /// <summary>A plain-task-id reference.</summary>
    public static QuickOpenRef Task(string id) => new(QuickOpenKind.TaskId, id);

    /// <summary>A custom-id reference. <paramref name="teamId"/> carries the workspace/team id when it
    /// came from a <c>/t/{team_id}/{custom_id}</c> URL (so the caller can resolve against the URL's own
    /// workspace instead of the configured one); null for a bare custom id.</summary>
    public static QuickOpenRef Custom(string id, string? teamId = null) => new(QuickOpenKind.CustomId, id, teamId);
}

/// <summary>The <c>(taskId, name)</c> a new-tab / split-pane quick-open gesture (#615) hands the
/// cross-platform launcher: the resolved id and display name on a cache hit, else the raw typed token as
/// both (the child's <c>--task</c> resolves it, #464). See <see cref="QuickOpenParser.ResolveLaunch"/>.</summary>
public readonly record struct QuickOpenLaunch(string TaskId, string Name);

/// <summary>
/// Pure parsing + cache-resolution for the Ctrl+O quick-open feature (#303): turn a pasted/typed
/// task <b>id</b>, <b>custom id</b>, or <b>task URL</b> into a <see cref="QuickOpenRef"/>, and match a
/// ref against the already-loaded working set. Terminal.Gui-free (mirrors <c>RowHitTester</c> /
/// <c>SubtaskArranger</c>) so the URL/custom-id shapes and the cache-first resolution order are
/// unit-tested rather than buried in the host glue.
/// </summary>
public static class QuickOpenParser
{
    /// <summary>
    /// Classifies <paramref name="input"/> as a task-id, custom-id, or invalid reference. Accepts a
    /// bare id (<c>86abc123</c>), a bare custom id (<c>ABC-123</c>), or a ClickUp task URL on
    /// <b>any</b> <c>*.clickup.com</c> host (the subdomain is ignored while #304's stored subdomain is
    /// unavailable): <c>…/t/{id}</c> ⇒ task, <c>…/t/{team_id}/{custom_id}</c> ⇒ custom. A non-ClickUp
    /// URL, a ClickUp URL without a <c>/t/</c> task segment, and blank input all return
    /// <see cref="QuickOpenRef.Invalid"/>.
    /// <para>
    /// A <b>bare</b> custom id is recognized only when it carries a hyphen (ClickUp's usual
    /// <c>PREFIX-123</c> form); a hyphenless bare token is classified as a plain id, so an
    /// <em>uncached</em> hyphenless custom id resolves through the plain-id endpoint and won't be found.
    /// A custom id in a <c>/t/{team}/{custom}</c> URL, or any cached custom id (matched on the task's
    /// <see cref="TaskItem.CustomId"/> by <see cref="FindInCache"/> regardless of hyphen), is unaffected.
    /// </para>
    /// </summary>
    public static QuickOpenRef Parse(string? input)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0)
            return QuickOpenRef.Invalid;

        // Let a scheme-less clickup.com paste (e.g. "app.clickup.com/t/abc" or the apex "clickup.com/t/abc")
        // parse as a URL.
        var candidate = s;
        if (!s.Contains("://", StringComparison.Ordinal)
            && (s.Contains(".clickup.com/", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("clickup.com/", StringComparison.OrdinalIgnoreCase)))
            candidate = "https://" + s;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // A URL we can understand only if it's a ClickUp host; anything else is a foreign web link.
            return IsClickUpHost(uri.Host) ? FromTaskPath(uri.AbsolutePath) : QuickOpenRef.Invalid;
        }

        // A bare token: ClickUp custom ids carry a hyphen (ABC-123); plain task ids do not.
        return s.Contains('-', StringComparison.Ordinal) ? QuickOpenRef.Custom(s) : QuickOpenRef.Task(s);
    }

    /// <summary>
    /// The cache-first match for <paramref name="r"/> over the already-loaded <paramref name="universe"/>:
    /// by exact task <see cref="TaskItem.Id"/> first, then by <see cref="TaskItem.CustomId"/>
    /// (case-insensitive) — the issue's "match by task ID, then by CustomId (no API call)" order.
    /// Returns null on no match (the caller then resolves via the API) or an
    /// <see cref="QuickOpenKind.Invalid"/> ref.
    /// </summary>
    public static TaskItem? FindInCache(IReadOnlyList<TaskItem> universe, QuickOpenRef r)
    {
        if (r.Kind == QuickOpenKind.Invalid)
            return null;

        foreach (var t in universe)
            if (t.Id == r.Value)
                return t;

        foreach (var t in universe)
            if (!string.IsNullOrEmpty(t.CustomId)
                && string.Equals(t.CustomId, r.Value, StringComparison.OrdinalIgnoreCase))
                return t;

        return null;
    }

    /// <summary>
    /// Resolves a quick-open input to the <c>(taskId, name)</c> the cross-platform launcher should open
    /// for a <b>new-tab / split-pane</b> gesture (launch modes B, #615): a cache hit supplies the real id
    /// and name (for the status flash); a miss hands the <b>raw trimmed token</b> to the child as both id
    /// and display name — the child's <c>--task</c> resolves every form Ctrl+O does (a plain id, custom id,
    /// or task URL, #464), so there is no parent-side round-trip. Returns <c>null</c> when the input can't
    /// be parsed as a task reference (the caller flashes and launches nothing — the error belongs where the
    /// typing happened, not in a child process that opens and dies). Pure (mirrors <see cref="Parse"/> /
    /// <see cref="FindInCache"/>) so the cache-hit-vs-raw-token decision is unit-tested off the host glue.
    /// </summary>
    public static QuickOpenLaunch? ResolveLaunch(IReadOnlyList<TaskItem> universe, string? input)
    {
        var r = Parse(input);
        if (r.Kind == QuickOpenKind.Invalid)
            return null;

        if (FindInCache(universe, r) is { } cached)
            return new QuickOpenLaunch(cached.Id, cached.Name);

        // input is non-blank here: an all-whitespace input parses Invalid and returned above.
        var token = input!.Trim();
        return new QuickOpenLaunch(token, token);
    }

    private static bool IsClickUpHost(string host)
        => host.Equals("clickup.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".clickup.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the task/custom id from a ClickUp URL path. The task segment is <c>/t/</c>: one
    /// trailing segment ⇒ a plain id, two-or-more ⇒ a custom id preceded by the team id
    /// (<c>/t/{team_id}/{custom_id}</c>) — the team id is carried on the ref (<see cref="QuickOpenRef.TeamId"/>)
    /// so the caller can resolve the custom id against the URL's own workspace, not the configured one.
    /// A path without <c>/t/</c> (or with nothing after it) is not a task link.
    /// </summary>
    private static QuickOpenRef FromTaskPath(string path)
    {
        const string marker = "/t/";
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return QuickOpenRef.Invalid;

        var rest = path[(idx + marker.Length)..];
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length switch
        {
            0 => QuickOpenRef.Invalid,
            1 => QuickOpenRef.Task(segments[0]),
            _ => QuickOpenRef.Custom(segments[1], segments[0]),
        };
    }
}
