using ClickUpTodo.Services;
using ClickUpTodo.Setup;

namespace ClickUpTodo.Tui;

/// <summary>
/// Shared "open this task in the browser" core (#346). Both TUI hosts rewrite an app.clickup.com link
/// onto the configured workspace subdomain (#304), parse it, and hand it to the injected
/// <see cref="IBrowserLauncher"/> — identical logic that previously lived (and drifted, per #346) in
/// each host. Only the <em>reporting</em> differs: the dashboard flashes success/failure on its live
/// status line, while the single-task host is fire-and-forget (it closes as it launches, so there is no
/// view left to flash). So this returns the outcome and each host formats its own message.
/// </summary>
internal static class ClickUpTaskBrowser
{
    /// <summary>The outcome of an <see cref="Open"/> attempt.</summary>
    internal enum Result
    {
        /// <summary>The URL was empty/whitespace — nothing to open.</summary>
        NoUrl,
        /// <summary>The (rewritten) URL did not parse as an absolute URI.</summary>
        InvalidUrl,
        /// <summary>A browser was launched.</summary>
        Opened,
        /// <summary>The URL was valid but no browser could be launched.</summary>
        LaunchFailed,
    }

    /// <summary>
    /// Rewrites <paramref name="url"/>'s host onto <paramref name="workspaceSubdomain"/> (#304), parses
    /// it, and tries to open it via <paramref name="browser"/>. Returns the outcome and the resolved
    /// target string (empty for <see cref="Result.NoUrl"/>) so the caller can format a message.
    /// </summary>
    public static (Result Result, string Target) Open(IBrowserLauncher browser, string? url, string? workspaceSubdomain)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (Result.NoUrl, "");

        var target = ClickUpUrl.RewriteHost(url, workspaceSubdomain);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return (Result.InvalidUrl, target);

        return (browser.TryOpen(uri) ? Result.Opened : Result.LaunchFailed, target);
    }
}
