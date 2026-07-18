using System.Runtime.InteropServices;
using ClickUpTodo.Agent;

namespace ClickUpTodo.Setup;

/// <summary>
/// A single concrete way to open a URL: an executable plus its argument vector (built as an array,
/// never a concatenated shell string, so the URL can never be read as a shell token), or a
/// shell-association open when <see cref="UseShellExecute"/> is set. The launcher tries a
/// planner-ordered list of these until one starts.
/// </summary>
/// <param name="FileName">Executable to run (resolved via <c>PATH</c>), or the URL itself when
/// <paramref name="UseShellExecute"/> is set.</param>
/// <param name="Arguments">Argument vector, passed verbatim to <c>ProcessStartInfo.ArgumentList</c>
/// (empty for the shell-execute path).</param>
/// <param name="UseShellExecute">Open via the OS shell association (Windows default browser) instead
/// of invoking a named opener.</param>
/// <param name="DisplayName">Human-readable name of the opener, for status/log messages.</param>
public sealed record BrowserCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool UseShellExecute,
    string DisplayName);

/// <summary>
/// Pure, I/O-free builder for the ordered list of browser-open candidates (issue #308). Given the OS,
/// a way to probe whether an executable is on <c>PATH</c>, the environment, and the URL, it returns
/// the <see cref="BrowserCommand"/>s to try in order — already filtered to openers that are present —
/// so the launcher just starts each until one succeeds.
///
/// Mirrors the cross-platform <see cref="TerminalCommandPlanner"/> strategy: Windows uses the shell
/// association (default browser); macOS uses <c>open</c>; Linux probes the usual openers
/// (<c>$BROWSER</c>, <c>xdg-open</c>, <c>gio</c>, …) and, when none is present (headless/minimal),
/// returns an empty list so the caller can show an actionable message instead of throwing.
/// </summary>
public static class BrowserLaunchPlanner
{
    public static IReadOnlyList<BrowserCommand> Plan(
        OSPlatformKind os,
        Func<string, bool> exists,
        Func<string, string?> getEnv,
        Uri url)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(getEnv);
        ArgumentNullException.ThrowIfNull(url);

        var target = url.ToString();
        return os switch
        {
            OSPlatformKind.Windows => [ShellExecute(target)],
            OSPlatformKind.MacOS => [new BrowserCommand("open", [target], false, "open"), ShellExecute(target)],
            OSPlatformKind.Linux => PlanLinux(exists, getEnv, target),
            _ => [ShellExecute(target)],
        };
    }

    /// <summary>Resolves the host OS family from the runtime, mirroring the terminal launcher.</summary>
    public static OSPlatformKind CurrentOS()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return OSPlatformKind.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OSPlatformKind.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return OSPlatformKind.Linux;
        return OSPlatformKind.Unknown;
    }

    /// <summary>A short install hint for when no opener is available, or <see langword="null"/>.</summary>
    public static string? OpenerHint(OSPlatformKind os)
        => os == OSPlatformKind.Linux ? "install xdg-utils for 'xdg-open'" : null;

    private static BrowserCommand ShellExecute(string target)
        => new(target, [], true, "system browser");

    // Linux: an explicit $BROWSER wins, then the well-known openers in preference order. Only openers
    // actually on PATH are emitted; if none is present the list is empty and the caller degrades with
    // a message (no doomed shell-execute — it would just shell out to the same missing xdg-open).
    private static IReadOnlyList<BrowserCommand> PlanLinux(Func<string, bool> exists, Func<string, string?> getEnv, string target)
    {
        var specs = new List<BrowserCommand>();

        // $BROWSER is an XDG-convention colon-separated list of commands, each of which may carry a
        // `%s`/`%c` placeholder (e.g. "firefox %s:chromium"). Honour the executable of every entry
        // that's on PATH, passing the URL as its argument — placeholder *position* isn't substituted
        // (the URL is appended), which is what browsers expect anyway. Malformed / not-found entries
        // are skipped; if that empties $BROWSER we still fall through to xdg-open (which re-reads it).
        var configured = getEnv("BROWSER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var entry in configured.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var exe = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(exe) && !exe.Contains('%') && exists(exe))
                    specs.Add(new BrowserCommand(exe, [target], false, exe));
            }
        }

        foreach (var opener in new[] { "xdg-open", "gio", "x-www-browser", "www-browser", "sensible-browser" })
        {
            if (!exists(opener))
                continue;

            // `gio` needs the `open` subcommand; the rest take the URL as their sole argument.
            var args = opener == "gio" ? new[] { "open", target } : [target];
            specs.Add(new BrowserCommand(opener, args, false, opener));
        }

        return specs;
    }
}
