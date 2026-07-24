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
    /// The <c>*.clickup.com</c> labels that are ClickUp's own service hosts, never a workspace subdomain
    /// (#351). <see cref="SubdomainFromWorkspaceHost"/> rejects these so an auto-detect probe that lands on
    /// e.g. a login (<c>app</c>) or marketing (<c>www</c>) host doesn't record a bogus subdomain. Kept
    /// broader than <see cref="NormalizeSubdomain"/>'s <c>app</c>/<c>api</c> guard, which is about
    /// user-typed input rather than a redirect target.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedSubdomains =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "app", "api", "www", "help", "support", "docs", "sso", "sharing",
        };

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

        // A valid DNS label is [a-z0-9-] and neither starts nor ends with a hyphen (a leading/trailing
        // hyphen would yield an unresolvable host like `-odbm.clickup.com`).
        if (label[0] == '-' || label[^1] == '-')
            return "";
        foreach (var c in label)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                return "";

        return label;
    }

    /// <summary>
    /// Extracts the workspace subdomain label from a fully-qualified host, but <b>only</b> when the host is
    /// exactly <c>{label}.clickup.com</c> — a single DNS label under <see cref="BaseDomain"/> (#351). This
    /// is the auto-detect counterpart to <see cref="NormalizeSubdomain"/>: where that leniently takes the
    /// first label of <i>any</i> host (it parses user-typed input), this is strict about the shape of a
    /// <b>redirect target</b> so we never mistake a non-workspace host for a subdomain. Returns <c>""</c>
    /// when the host is blank, isn't <c>{label}.clickup.com</c> (bare <c>clickup.com</c>, a deeper host like
    /// <c>a.b.clickup.com</c>, or any non-ClickUp host), the label fails <see cref="NormalizeSubdomain"/>'s
    /// validation, or the label is a <see cref="ReservedSubdomains">reserved</see> service host.
    /// </summary>
    public static string SubdomainFromWorkspaceHost(string? host)
    {
        var value = host?.Trim().ToLowerInvariant() ?? "";
        if (value.Length == 0)
            return "";

        // Must be exactly one label under clickup.com: strip the ".clickup.com" suffix and reject anything
        // left that still contains a dot (a deeper host) or is empty (bare clickup.com).
        var suffix = "." + BaseDomain;
        if (!value.EndsWith(suffix, StringComparison.Ordinal))
            return "";
        var label = value[..^suffix.Length];
        if (label.Length == 0 || label.Contains('.'))
            return "";

        // Reuse the same charset/hyphen validation as user-typed input, then drop ClickUp's own hosts.
        if (NormalizeSubdomain(label).Length == 0 || ReservedSubdomains.Contains(label))
            return "";

        return label;
    }

    /// <summary>
    /// The subdomain implied by the final URL an <c>app.clickup.com</c> probe redirected to (#351): the
    /// workspace label when the probe landed on <c>{label}.clickup.com</c>, else <c>""</c> (including a
    /// null url, e.g. when the probe never resolved a final host). Thin adapter over
    /// <see cref="SubdomainFromWorkspaceHost"/> so <see cref="SubdomainProbe"/> stays I/O-only.
    /// </summary>
    public static string SubdomainFromFinalUrl(Uri? finalUrl)
        => finalUrl is null ? "" : SubdomainFromWorkspaceHost(finalUrl.Host);

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

        // Reconstruct swapping only the host, preserving the scheme, any userinfo, an explicit non-default
        // port, and the path/query/fragment byte-for-byte. Deliberately not UriBuilder: it re-encodes the
        // path (a percent-escaped segment would be decoded, corrupting the URL) and would surface the
        // scheme's default port as an explicit ":443". GetComponents with UriFormat.UriEscaped keeps the
        // already-escaped path/query/fragment intact for the reusable seam (#303).
        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? "" : uri.UserInfo + "@";
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rest = uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped);
        return $"{uri.Scheme}://{userInfo}{label}.{BaseDomain}{port}{rest}";
    }
}
