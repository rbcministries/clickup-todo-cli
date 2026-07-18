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
/// for a <see cref="LinkKind.Task"/> link — the target <see cref="TaskId"/>. Immutable and Terminal.Gui-free
/// so the render (#317) / click (#318) / focus-traversal (#319) layers can consume it without pulling in UI
/// types.
/// </summary>
public readonly record struct LinkSpan(int Start, int Length, LinkKind Kind, string Url, string? TaskId = null)
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
/// parser mirrors the style of <c>SetupWizard.ExtractListId</c>.
/// </para>
/// <para>
/// Scope for this slice: bare <c>http(s)://</c> URLs and the API-id <c>/t/{id}</c> task-URL form (and its
/// workspace-prefixed <c>/{workspaceId}/t/{id}</c> variant). Markdown-style <c>[text](url)</c> link spans
/// and custom-id task URLs are deferred follow-ups.
/// </para>
/// </summary>
public static class TaskLinkExtractor
{
    // Bare http/https URLs: the scheme, then a run of non-whitespace. Trailing sentence punctuation is
    // trimmed afterwards (TrimUrl) rather than excluded here, so a URL followed by "." or wrapped in "()"
    // is captured then tidied. Compiled once — the panes re-extract on every (re)render.
    private static readonly Regex UrlPattern =
        new(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Trailing characters trimmed off a matched URL: sentence punctuation and closing brackets. A closing
    // ')' is only trimmed when the URL has no matching '(' (see TrimUrl), so balanced parenthetical URLs
    // like the Wikipedia "..._(disambiguation)" shape survive.
    private const string TrailingTrim = ".,;:!?\"')]}";

    /// <summary>
    /// Scans <paramref name="text"/> for bare <c>http(s)://</c> URLs, in document order, returning one
    /// <see cref="LinkSpan"/> per link. Offsets are char indices into <paramref name="text"/>. A blank or
    /// null input yields an empty list. Task links carry <see cref="LinkKind.Task"/> and their
    /// <see cref="LinkSpan.TaskId"/>; everything else is <see cref="LinkKind.Web"/>.
    /// </summary>
    public static IReadOnlyList<LinkSpan> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<LinkSpan>();

        var spans = new List<LinkSpan>();
        foreach (Match m in UrlPattern.Matches(text))
        {
            var url = TrimUrl(m.Value);
            if (url.Length == 0)
                continue;

            var kind = TryParseTaskUrl(url, out var taskId) ? LinkKind.Task : LinkKind.Web;
            spans.Add(new LinkSpan(m.Index, url.Length, kind, url, kind == LinkKind.Task ? taskId : null));
        }

        return spans;
    }

    /// <summary>
    /// Recognizes a ClickUp task URL and extracts its task id. True when <paramref name="url"/> is an
    /// absolute URL whose host is <c>clickup.com</c> or a <c>*.clickup.com</c> subdomain and whose path is
    /// <c>/t/{id}</c> or <c>/{workspaceId}/t/{id}</c> (id = one path segment). The id is returned in
    /// <paramref name="taskId"/>; on a non-match <paramref name="taskId"/> is empty and the result is false.
    /// Mirrors the documented-shape style of <c>SetupWizard.ExtractListId</c>.
    /// </summary>
    public static bool TryParseTaskUrl(string url, out string taskId)
    {
        taskId = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!IsClickUpHost(uri.Host))
            return false;

        // AbsolutePath is percent-encoded and always starts with '/'. The task id is the segment after
        // "/t/", allowing an optional numeric workspace-id segment before it.
        var match = Regex.Match(
            uri.AbsolutePath, @"^(?:/\d+)?/t/([^/]+)/?$", RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var id = Uri.UnescapeDataString(match.Groups[1].Value);
        if (id.Length == 0)
            return false;

        taskId = id;
        return true;
    }

    // A ClickUp host is clickup.com itself or any subdomain of it (app.clickup.com, etc.), case-insensitive.
    private static bool IsClickUpHost(string host)
        => host.Equals("clickup.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".clickup.com", StringComparison.OrdinalIgnoreCase);

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
