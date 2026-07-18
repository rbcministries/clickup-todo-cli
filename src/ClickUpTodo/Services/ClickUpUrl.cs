namespace ClickUpTodo.Services;

/// <summary>
/// Pure helpers for the "which hosts are ours" question around ClickUp task URLs (#304): normalizing a
/// user-entered workspace subdomain and rewriting an <c>app.clickup.com</c> link onto that subdomain so a
/// Ctrl+B browser launch lands directly on the workspace host instead of eating the
/// <c>app.clickup.com</c> → subdomain redirect. No Terminal.Gui / I/O, so it unit-tests cleanly; the
/// stored subdomain and this rewrite are the seam #303's URL parser reuses.
/// </summary>
public static class ClickUpUrl
{
    /// <summary>The generic ClickUp web host every task URL comes back on; the one we rewrite away from.</summary>
    public const string AppHost = "app.clickup.com";

    /// <summary>The ClickUp API host — a reserved non-workspace label, never a valid subdomain.</summary>
    public const string ApiHost = "api.clickup.com";

    /// <summary>The base domain a workspace subdomain hangs off (<c>{subdomain}.clickup.com</c>).</summary>
    public const string BaseDomain = "clickup.com";

    /// <summary>
    /// Normalizes a user-entered workspace subdomain to its bare DNS label (e.g. <c>odbm</c>). Accepts the
    /// label alone (<c>odbm</c>), a full host (<c>odbm.clickup.com</c>), or a pasted URL
    /// (<c>https://odbm.clickup.com/12345/v/l/…</c>): it strips any scheme, path/query, and port, takes the
    /// first DNS label, lowercases it, and validates the <c>[a-z0-9-]</c> charset. Returns <c>""</c> for
    /// blank/whitespace/invalid input or for ClickUp's own non-workspace hosts <c>app</c> / <c>api</c>.
    /// <c>""</c> is the "unset" sentinel — <see cref="RewriteHost"/> then leaves URLs untouched.
    /// </summary>
    public static string NormalizeSubdomain(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
            return "";

        // Drop a scheme (everything up to and including "://") so a pasted URL reduces to host/path.
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            value = value[(scheme + 3)..];

        // Keep only the authority (host[:port]) — drop any path/query/fragment.
        var slash = value.IndexOfAny(['/', '?', '#']);
        if (slash >= 0)
            value = value[..slash];

        // Drop a port, then take the first DNS label of the host.
        var colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[..colon];
        var dot = value.IndexOf('.');
        if (dot >= 0)
            value = value[..dot];

        var label = value.Trim().ToLowerInvariant();
        if (label.Length == 0 || label is "app" or "api")
            return "";

        foreach (var c in label)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                return "";

        return label;
    }

    /// <summary>
    /// Rewrites an <c>app.clickup.com</c> task URL's host to <c>{subdomain}.clickup.com</c> so a browser
    /// launch skips the app→workspace redirect (#304), preserving the scheme, path, query, fragment, and
    /// any explicit port. Returns <paramref name="url"/> unchanged when the (normalized) subdomain is
    /// blank, the url isn't an absolute http/https URL, or its host isn't <c>app.clickup.com</c> — so a
    /// non-ClickUp or already-workspace URL passes through untouched.
    /// </summary>
    public static string RewriteHost(string? url, string? subdomain)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url ?? "";

        var label = NormalizeSubdomain(subdomain);
        if (label.Length == 0)
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return url;
        if (!string.Equals(uri.Host, AppHost, StringComparison.OrdinalIgnoreCase))
            return url;

        var builder = new UriBuilder(uri) { Host = $"{label}.{BaseDomain}" };
        // UriBuilder(uri) fills Port with the scheme default (e.g. 443), which would surface as an explicit
        // ":443" in the output; drop it back to -1 when the original used the default port so the rewritten
        // URL stays byte-for-byte apart from the host.
        if (uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri.ToString();
    }
}
