using ClickUpTodo.Services;
using Terminal.Gui.App;

namespace ClickUpTodo.Tui;

/// <summary>Best-effort clipboard writes shared by the hosts: the launch-command fallback (#301) and the
/// Task Detail copy-URL gesture.</summary>
internal static class ClipboardText
{
    /// <summary>Writes <paramref name="text"/> to the clipboard; a headless/unsupported clipboard yields
    /// false so the caller shows the copy-it-yourself form instead.</summary>
    public static bool TryCopy(string text)
    {
        try
        {
            return Clipboard.TrySetClipboardData(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Copies a task's <paramref name="url"/> through the same app.clickup.com → workspace-subdomain
    /// rewrite Ctrl+B uses (#304), returning the status message the host flashes.</summary>
    public static string CopyTaskUrl(string? url, string? workspaceSubdomain, Func<string, bool> tryCopy)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL for this task.";

        var target = ClickUpUrl.RewriteHost(url, workspaceSubdomain);
        return tryCopy(target)
            ? $"Copied URL: {target}"
            : $"Couldn't reach the clipboard — copy the URL: {target}";
    }
}
