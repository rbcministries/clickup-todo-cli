using System.Text.RegularExpressions;

namespace ClickUpTodo.Tui;

/// <summary>Whether a <see cref="LinkSpan"/> points at a ClickUp task or an ordinary web page.</summary>
public enum LinkKind
{
    /// <summary>An ordinary web link (anything that isn't a recognized ClickUp task URL).</summary>
    Web,

    /// <summary>A ClickUp task URL (<c>app.clickup.com/t/{id}</c>); <see cref="LinkSpan.TaskId"/> is set.</summary>
    Task,
}

/// <summary>
/// One detected link in a body of text: its position (<see cref="Start"/> + <see cref="Length"/> as
/// UTF-16 char offsets into the source string), its classification, the resolved <see cref="Url"/>, and —
/// for a <see cref="LinkKind.Task"/> link — the target <see cref="TaskId"/> plus whether that id is a
/// ClickUp <em>custom</em> id (<see cref="IsCustomTaskId"/>). Immutable and Terminal.Gui-free so the render
/// (#317) / click (#318) / focus-traversal (#319) layers can consume it without pulling in UI types.
/// </summary>
/// <remarks>
/// For a bare URL the span covers the whole URL. For a markdown <c>[text](url)</c> link the span covers only
/// the <b>visible text</b> (the chars between <c>[</c> and <c>]</c>), never the surrounding markup — so
/// <c>source.Substring(Start, Length)</c> is exactly what the reader sees and <see cref="Url"/> is the
/// resolved target (issue #356).
/// </remarks>
public readonly record struct LinkSpan(
    int Start, int Length, LinkKind Kind, string Url, string? TaskId = null, bool IsCustomTaskId = false)
{
    /// <summary>The exclusive end offset (<see cref="Start"/> + <see cref="Length"/>).</summary>
    public int End => Start + Length;
}

/// <summary>
/// Pure, unit-tested link model for the task detail panes (issue #316, foundation for #317/#318/#319).
/// Scans the rendered Description / Comment / Stream body text produced by <see cref="TaskDetailFormatter"/>,
/// finds bare <c>http(s)://</c> URLs, and classifies each as a ClickUp <b>task</b> link (with its task id)
/// or an <b>other web</b> link — returning immutable <see cref="LinkSpan"/>s whose offsets index into the
/// exact string passed in. <see cref="DetailPaneView"/> lays the same string out one line per <c>'\n'</c>,
/// so a consumer maps a global offset to (line, column) by counting newlines before <see cref="LinkSpan.Start"/>.
/// <para>
/// No Terminal.Gui dependency (mirrors <see cref="TaskDetailFormatter"/>), so the detection and
/// classification are covered by unit tests while the render/activation glue stays thin. The task-URL
/// parser mirrors the style of <c>SetupWizard.ExtractListId</c>. Body text is short (a description or a
/// comment), so the per-URL <see cref="Uri"/> parse and <see cref="Regex"/> match are inconsequential.
/// </para>
/// <para>
/// Detects both <b>bare</b> <c>http(s)://</c> URLs and <b>markdown</b> <c>[text](url)</c> links (#356). A
/// markdown link yields a span over just its visible text with the resolved URL as the target. Task URLs are
/// recognized in the API-id <c>/t/{id}</c> form (and its workspace-prefixed <c>/{workspaceId}/t/{id}</c>
/// variant) and the <b>custom-id</b> <c>/t/{teamId}/{customId}</c> form, the latter flagged via
/// <see cref="LinkSpan.IsCustomTaskId"/> for the activation layer (#318/#320).
/// </para>
/// </summary>
public static class TaskLinkExtractor
{
    // One combined, ordered-alternation scanner. The markdown alternative is listed first, so at a '[' the
    // engine matches the whole "[text](url)" and the URL inside the parens is consumed — the bare
    // alternative never re-detects it, so a markdown link yields exactly one span (no duplicate for its own
    // URL). A malformed markdown link (no closing ')') fails the first alternative; its URL, if bare, is
    // still caught by the second. Case-insensitive so an upper-cased scheme ("HTTPS://…", as some clients
    // emit) is still detected. Compiled once — the panes re-extract on every (re)render.
    //   md   → "[text](url)": mdtext = the visible text (single line — it may not span a newline, so a
    //          span always sits on one rendered line); mdurl = the target, a run of non-space/non-paren
    //          chars that also admits one level of balanced "(...)" so a URL like
    //          ".../Foo_(bar)" isn't truncated at its inner '(' (mirrors the bare path's TrimUrl handling).
    //   bare → a run of non-whitespace after the scheme; trailing sentence punctuation is trimmed by TrimUrl.
    private static readonly Regex LinkPattern = new(
        @"(?<md>\[(?<mdtext>[^\r\n\]]*)\]\((?<mdurl>(?:[^()\s]|\([^()\s]*\))+)\))|(?<bare>https?://[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Trailing characters trimmed off a matched URL: sentence punctuation and closing brackets. A closing
    // ')' is only trimmed when the URL has no matching '(' (see TrimUrl), so balanced parenthetical URLs
    // like the Wikipedia "..._(disambiguation)" shape survive.
    private const string TrailingTrim = ".,;:!?\"')]}";

    /// <summary>
    /// Scans <paramref name="text"/> for bare <c>http(s)://</c> URLs and markdown <c>[text](url)</c> links,
    /// in document order, returning one <see cref="LinkSpan"/> per link. Offsets are char indices into
    /// <paramref name="text"/>: a bare-URL span covers the whole URL; a markdown span covers only the visible
    /// text (see <see cref="LinkSpan"/>). A blank or null input yields an empty list. Task links carry
    /// <see cref="LinkKind.Task"/>, their <see cref="LinkSpan.TaskId"/>, and — for a custom-id URL —
    /// <see cref="LinkSpan.IsCustomTaskId"/>; everything else is <see cref="LinkKind.Web"/>.
    /// </summary>
    public static IReadOnlyList<LinkSpan> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<LinkSpan>();

        var spans = new List<LinkSpan>();
        foreach (Match m in LinkPattern.Matches(text))
        {
            if (m.Groups["md"].Success)
            {
                var visible = m.Groups["mdtext"];
                // A markdown URL is already delimited by ')', so it needs no trailing-punctuation trim.
                // Guard well-formedness the same way as bare URLs, and skip a blank visible text
                // ("[](url)" / "[   ](url)") — there is nothing for the reader to see or click.
                if (string.IsNullOrWhiteSpace(visible.Value)
                    || !Uri.TryCreate(m.Groups["mdurl"].Value, UriKind.Absolute, out var mdUri)
                    || !IsHttpUrl(mdUri))
                    continue;

                spans.Add(SpanFor(visible.Index, visible.Length, m.Groups["mdurl"].Value, mdUri));
                continue;
            }

            var url = TrimUrl(m.Value);
            // Only emit well-formed http(s) links with a real host. This rejects a degenerate match left
            // by trimming (e.g. "https://." → "https://", which has no host), so every LinkSpan.Url is a
            // navigable absolute URL the render/activation layers can rely on.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsHttpUrl(uri))
                continue;

            spans.Add(SpanFor(m.Index, url.Length, url, uri));
        }

        return spans;
    }

    // Builds a LinkSpan for a validated http(s) Uri, classifying it as a ClickUp task link (with its id and
    // the custom-id flag) or an ordinary web link. `start`/`length` index into the source string; `url` is
    // the exact text carried on the span.
    private static LinkSpan SpanFor(int start, int length, string url, Uri uri)
    {
        var isTask = TryParseTaskUri(uri, out var taskId, out var isCustom);
        return new LinkSpan(
            start, length, isTask ? LinkKind.Task : LinkKind.Web, url,
            isTask ? taskId : null, isTask && isCustom);
    }

    /// <summary>
    /// Recognizes a ClickUp task URL and extracts its task id. True when <paramref name="url"/> is an
    /// absolute http(s) URL whose host is <c>app.clickup.com</c> / <c>clickup.com</c> and whose path is the
    /// API-id <c>/t/{id}</c> (or workspace-prefixed <c>/{workspaceId}/t/{id}</c>) form, or the custom-id
    /// <c>/t/{teamId}/{customId}</c> form. The id is returned in <paramref name="taskId"/>; on a non-match
    /// <paramref name="taskId"/> is empty and the result is false. Mirrors the documented-shape style of
    /// <c>SetupWizard.ExtractListId</c>.
    /// </summary>
    public static bool TryParseTaskUrl(string url, out string taskId)
        => TryParseTaskUrl(url, out taskId, out _);

    /// <summary>
    /// As <see cref="TryParseTaskUrl(string, out string)"/>, additionally reporting whether the matched id is
    /// a ClickUp <b>custom</b> id (the <c>/t/{teamId}/{customId}</c> form) via <paramref name="isCustomId"/>,
    /// so the activation layer (#318/#320) can distinguish it from an API id. <paramref name="isCustomId"/>
    /// is false on a non-match.
    /// </summary>
    public static bool TryParseTaskUrl(string url, out string taskId, out bool isCustomId)
    {
        taskId = string.Empty;
        isCustomId = false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && TryParseTaskUri(uri, out taskId, out isCustomId);
    }

    // The Uri-based core shared by TryParseTaskUrl and Extract (which already holds a parsed Uri), so a URL
    // is parsed once. A task URL is an http(s) ClickUp host whose path is the API-id "/t/{id}" /
    // "/{workspaceId}/t/{id}" form, or the custom-id "/t/{teamId}/{customId}" form (a numeric team id then
    // the custom id). The two shapes can't collide: the API pattern requires a single segment after "/t/",
    // the custom pattern two, so "/{workspaceId}/t/{id}/extra" matches neither and stays a non-task link.
    private static bool TryParseTaskUri(Uri uri, out string taskId, out bool isCustomId)
    {
        taskId = string.Empty;
        isCustomId = false;
        if (!IsHttpUrl(uri) || !IsClickUpTaskHost(uri.Host))
            return false;

        // AbsolutePath is percent-encoded and always starts with '/' (query/fragment live elsewhere on the
        // Uri, so they don't reach here). Try the API-id shape first, then the custom-id shape.
        var apiMatch = Regex.Match(uri.AbsolutePath, @"^(?:/\d+)?/t/([^/]+)/?$", RegexOptions.CultureInvariant);
        if (apiMatch.Success)
            return TrySetId(apiMatch.Groups[1].Value, isCustom: false, out taskId, out isCustomId);

        var customMatch = Regex.Match(uri.AbsolutePath, @"^/t/\d+/([^/]+)/?$", RegexOptions.CultureInvariant);
        if (customMatch.Success)
            return TrySetId(customMatch.Groups[1].Value, isCustom: true, out taskId, out isCustomId);

        return false;

        static bool TrySetId(string rawId, bool isCustom, out string taskId, out bool isCustomId)
        {
            taskId = string.Empty;
            isCustomId = false;
            var id = Uri.UnescapeDataString(rawId);
            if (id.Length == 0)
                return false;

            taskId = id;
            isCustomId = isCustom;
            return true;
        }
    }

    // A navigable web link: an absolute http/https URL with a non-empty host.
    private static bool IsHttpUrl(Uri uri)
        => (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    // ClickUp task URLs live on app.clickup.com (or bare clickup.com), case-insensitive. Deliberately not
    // any "*.clickup.com" subdomain: help/marketing subdomains can carry an unrelated "/t/" path and must
    // not be mistaken for task links.
    private static bool IsClickUpTaskHost(string host)
        => host.Equals("app.clickup.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("clickup.com", StringComparison.OrdinalIgnoreCase);

    // Trims trailing sentence punctuation / closing brackets from a captured URL. A trailing ')' is kept
    // when the remaining URL still contains an unmatched '(' so balanced parenthetical URLs are preserved;
    // once no unmatched '(' remains, a trailing ')' is treated as prose punctuation and trimmed.
    private static string TrimUrl(string url)
    {
        var end = url.Length;
        while (end > 0)
        {
            var c = url[end - 1];
            if (c == ')' && HasUnmatchedOpenParen(url, end - 1))
                break;
            if (TrailingTrim.IndexOf(c) < 0)
                break;
            end--;
        }

        return url[..end];
    }

    // True when url[..count] contains more '(' than ')', i.e. a ')' at index `count` would balance an
    // earlier '(' and therefore belongs to the URL rather than the surrounding prose.
    private static bool HasUnmatchedOpenParen(string url, int count)
    {
        var depth = 0;
        for (var i = 0; i < count; i++)
        {
            if (url[i] == '(')
                depth++;
            else if (url[i] == ')' && depth > 0)
                depth--;
        }

        return depth > 0;
    }
}
