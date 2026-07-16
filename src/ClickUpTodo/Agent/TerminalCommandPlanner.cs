using System.Text;

namespace ClickUpTodo.Agent;

/// <summary>
/// Pure, I/O-free builder for the ordered list of terminal-launch candidates. Given the OS, a way to
/// probe whether an executable exists, the environment, and the inputs, it returns the
/// <see cref="LaunchSpec"/>s to try in order — already filtered to executables that are present, so
/// the launcher just starts each until one succeeds.
///
/// The <c>claude</c> invocation always reads the prompt <b>from the file</b>
/// (<c>Get-Content -Raw</c> on Windows, <c>$(cat …)</c> on POSIX); only the file <b>path</b> ever
/// enters a command string, never the prompt content. Argument vectors are built as arrays.
///
/// The working directory is baked <b>into the command</b> (<c>Set-Location</c> on Windows,
/// <c>cd … &amp;&amp;</c> on POSIX) as well as onto <see cref="LaunchSpec.WorkingDirectory"/>.
/// The spec's directory only takes effect for hosts we start in-process (a direct <c>pwsh</c>); the
/// emulators we prefer — Windows Terminal, gnome-terminal, konsole, Terminal.app via <c>osascript</c>
/// — hand off to a pre-existing server/new login shell that ignores the launcher's cwd and would
/// otherwise open in <c>$HOME</c>. Changing directory inside the command is what actually lands the
/// session in the chosen directory (and lets Claude pick up that project's MCP config).
///
/// <paramref name="oneOff"/> selects the session mode (#94): interactive (default,
/// <c>claude "…"</c>) or a one-off <c>claude -p "…"</c> run that executes and exits. One-off adds the
/// <c>-p</c> flag and, on POSIX/macOS, a keep-alive so the terminal doesn't vanish before the user
/// reads the output (Windows hosts already launch with <c>-NoExit</c>). The full background-run
/// experience is #99; this is the interim terminal path.
/// </summary>
public static class TerminalCommandPlanner
{
    public static IReadOnlyList<LaunchSpec> Plan(
        OSPlatformKind os,
        Func<string, bool> exists,
        Func<string, string?> getEnv,
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options,
        bool oneOff = false) => os switch
        {
            OSPlatformKind.Windows => PlanWindows(exists, getEnv, promptFilePath, workingDir, options, oneOff),
            OSPlatformKind.MacOS => PlanMacOS(exists, getEnv, promptFilePath, workingDir, options, oneOff),
            OSPlatformKind.Linux => PlanLinux(exists, getEnv, promptFilePath, workingDir, options, oneOff),
            _ => [],
        };

    // ── New-tab launch location (#255) ──────────────────────────────────────────
    //
    // New-tab is opt-in, interactive-only, and detection-gated per emulator: we only emit a tab spec
    // when the user asked for a tab AND an env var proves we're inside that emulator. Otherwise we
    // emit today's new-window spec. A one-off `-p` run never gets a tab — the issue notes it runs
    // through the background runner with no terminal, so a tab is meaningless.

    private static bool NewTabRequested(TerminalLauncherOptions options, bool oneOff)
        => options.LaunchLocation == LaunchLocation.NewTab && !oneOff;

    /// <summary>True if any of <paramref name="vars"/> is set to a non-blank value.</summary>
    private static bool EnvPresent(Func<string, string?> getEnv, params string[] vars)
        => vars.Any(v => !string.IsNullOrWhiteSpace(getEnv(v)));

    // ── Windows: Windows Terminal → pwsh → powershell → cmd, all running the same pwsh command ──
    //
    // The first three launch the PowerShell host directly. The `cmd` last resort (#45) opens a new
    // window via `cmd /c start "" <host> …` and carries the payload as `-EncodedCommand <base64>`:
    // Base64 is [A-Za-z0-9+/=] only, so the `&`/parenthesis command survives cmd.exe's tokenizer
    // intact (that mis-tokenization is exactly why PR #42 first omitted the cmd path). `-EncodedCommand`
    // needs a PowerShell host, so the cmd candidate is gated on one being present — cmd alone can't run
    // the file-reading `claude` invocation. In practice powershell.exe is always in-box, so cmd sits
    // last and is reached only if the direct launches fail to start, or when it's explicitly preferred.

    private static IReadOnlyList<LaunchSpec> PlanWindows(
        Func<string, bool> exists, Func<string, string?> getEnv, string file, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        var command = PwshCommand(file, cwd, options, oneOff); // `Set-Location …; & 'claude' [-p] … (Get-Content -Raw '<file>')`

        // Windows Terminal is the only Windows host with a tab notion: `wt -w 0 new-tab` targets the
        // current window (vs. today's `wt new-tab`, which opens a new window). Gated on WT_SESSION so
        // we only do it when we're actually running inside Windows Terminal.
        var wtTab = NewTabRequested(options, oneOff) && EnvPresent(getEnv, "WT_SESSION");

        // Candidate builders keyed by the terminal they represent, in default fallback order.
        var order = new[]
        {
            PreferredTerminal.WindowsTerminal,
            PreferredTerminal.Pwsh,
            PreferredTerminal.PowerShell,
            PreferredTerminal.Cmd,
        };

        // Honor an explicit preference by moving it to the front of the chain (fallback preserved).
        IEnumerable<PreferredTerminal> chain = options.Preferred == PreferredTerminal.Auto
            ? order
            : new[] { options.Preferred }.Concat(order.Where(t => t != options.Preferred));

        var specs = new List<LaunchSpec>();
        foreach (var terminal in chain)
        {
            var spec = terminal switch
            {
                PreferredTerminal.WindowsTerminal when exists("wt") => wtTab
                    ? new LaunchSpec(
                        "wt", ["-w", "0", "new-tab", "pwsh", "-NoExit", "-Command", command], cwd, "Windows Terminal (new tab)")
                    : new LaunchSpec(
                        "wt", ["new-tab", "pwsh", "-NoExit", "-Command", command], cwd, "Windows Terminal"),
                PreferredTerminal.Pwsh when exists("pwsh") => new LaunchSpec(
                    "pwsh", ["-NoExit", "-Command", command], cwd, "PowerShell (pwsh)"),
                PreferredTerminal.PowerShell when exists("powershell") => new LaunchSpec(
                    "powershell", ["-NoExit", "-Command", command], cwd, "Windows PowerShell"),
                PreferredTerminal.Cmd when exists("cmd") && PwshHost(exists) is { } host => new LaunchSpec(
                    "cmd",
                    ["/c", "start", "", host, "-NoExit", "-EncodedCommand", EncodePwshCommand(command)],
                    cwd,
                    "Command Prompt (cmd)"),
                _ => null,
            };
            if (spec is not null)
                specs.Add(spec);
        }
        return specs;
    }

    /// <summary>The PowerShell host to run inside the cmd window — pwsh preferred, else powershell; null if neither.</summary>
    private static string? PwshHost(Func<string, bool> exists) =>
        exists("pwsh") ? "pwsh" : exists("powershell") ? "powershell" : null;

    // ── macOS: osascript drives Terminal to run the bash command ──

    private static IReadOnlyList<LaunchSpec> PlanMacOS(
        Func<string, bool> exists, Func<string, string?> getEnv, string file, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        if (!exists("osascript"))
            return [];

        var inner = PosixCommand(file, cwd, options, oneOff); // `cd …; 'claude' [-p] … "$(cat '<file>')"`

        // Terminal.app new-window path (the historical default) — `do script` only ever makes windows,
        // so it stays window-only and doubles as the fallback for the iTerm tab path below.
        var windowScript = $"tell application \"Terminal\" to do script \"{AppleScriptEscape(inner)}\"";
        var windowSpec = new LaunchSpec("osascript", ["-e", windowScript], cwd, "Terminal (osascript)");

        // iTerm2 has a real tab-scripting API. When the user asked for a tab and TERM_PROGRAM says
        // we're inside iTerm, open a tab in the current window and run the command there, keeping the
        // Terminal.app window spec after it as the fallback (macOS has no cross-emulator chain).
        if (NewTabRequested(options, oneOff) && getEnv("TERM_PROGRAM") == "iTerm.app")
        {
            var escaped = AppleScriptEscape(inner);
            var iTermSpec = new LaunchSpec(
                "osascript",
                [
                    "-e", "tell application \"iTerm\"",
                    "-e", "tell current window",
                    "-e", "create tab with default profile",
                    "-e", $"tell current session to write text \"{escaped}\"",
                    "-e", "end tell",
                    "-e", "end tell",
                ],
                cwd,
                "iTerm2 (new tab)");
            return [iTermSpec, windowSpec];
        }

        return [windowSpec];
    }

    // ── Linux: honor $TERMINAL, else probe common emulators ──

    private static IReadOnlyList<LaunchSpec> PlanLinux(
        Func<string, bool> exists, Func<string, string?> getEnv, string file, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        var inner = PosixCommand(file, cwd, options, oneOff);
        var tab = NewTabRequested(options, oneOff);
        var specs = new List<LaunchSpec>();

        // An explicit $TERMINAL stays window-only: it's an arbitrary emulator with no portable tab flag.
        var configured = getEnv("TERMINAL");
        if (!string.IsNullOrWhiteSpace(configured) && exists(configured))
            specs.Add(new LaunchSpec(configured, [ExecSeparator(configured), "bash", "-lc", inner], cwd, configured));

        foreach (var name in new[] { "x-terminal-emulator", "gnome-terminal", "konsole" })
        {
            if (!exists(name))
                continue;

            // gnome-terminal (shared server → `--tab` lands in the current window) and konsole
            // (`--new-tab`) can open a tab in the running instance when we detect we're inside them.
            // x-terminal-emulator is a generic alias with no portable tab flag, so it stays window-only.
            if (tab && name == "gnome-terminal" && EnvPresent(getEnv, "GNOME_TERMINAL_SCREEN", "VTE_VERSION"))
                specs.Add(new LaunchSpec(name, ["--tab", "--", "bash", "-lc", inner], cwd, "gnome-terminal (new tab)"));
            else if (tab && name == "konsole" && EnvPresent(getEnv, "KONSOLE_VERSION"))
                specs.Add(new LaunchSpec(name, ["--new-tab", "-e", "bash", "-lc", inner], cwd, "konsole (new tab)"));
            else
                specs.Add(new LaunchSpec(name, [ExecSeparator(name), "bash", "-lc", inner], cwd, name));
        }

        return specs;
    }

    /// <summary>
    /// The "run this command" separator for a Linux terminal. gnome-terminal dropped <c>-e</c> in
    /// favor of <c>--</c>; everything else (and an unknown <c>$TERMINAL</c>) takes <c>-e</c>.
    /// </summary>
    private static string ExecSeparator(string terminal) => terminal switch
    {
        "gnome-terminal" => "--",
        _ => "-e",
    };

    // ── Command construction (file-indirected; prompt content never inlined) ──

    /// <summary>
    /// PowerShell command that runs claude with the prompt read from the file via Get-Content -Raw.
    /// One-off (#94) inserts <c>-p</c> right after the executable; the PowerShell hosts already launch
    /// with <c>-NoExit</c>, so the window stays open after a one-off run finishes. A non-blank
    /// <paramref name="cwd"/> is prepended as a <c>Set-Location -LiteralPath … -ErrorAction Stop;</c>
    /// so the session starts there even when the host (e.g. a <c>wt</c> tab) ignores the process
    /// working directory, and a failed directory change aborts before claude runs.
    /// </summary>
    private static string PwshCommand(string file, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        var parts = new List<string> { "&", PwshQuote(options.ClaudeExecutable) };
        if (oneOff)
            parts.Add(PwshQuote("-p"));
        parts.AddRange(options.ExtraArgs.Select(PwshQuote));
        parts.Add($"(Get-Content -Raw {PwshQuote(file)})");
        var command = string.Join(" ", parts);
        // -ErrorAction Stop makes a failed directory change terminating, so claude never runs in the
        // wrong directory — parity with the POSIX `&&` guard. -NoExit keeps the window open on the error.
        return string.IsNullOrWhiteSpace(cwd)
            ? command
            : $"Set-Location -LiteralPath {PwshQuote(cwd)} -ErrorAction Stop; {command}";
    }

    /// <summary>
    /// POSIX shell command that runs claude with the prompt read from the file via $(cat …). One-off
    /// (#94) inserts <c>-p</c> right after the executable and appends a keep-alive so the terminal
    /// (Linux <c>bash -lc</c> / macOS <c>do script</c>) doesn't close before the user reads the
    /// output — the interim terminal path until the background-run experience (#99) lands. A non-blank
    /// <paramref name="cwd"/> is prepended as <c>cd '<dir>' &amp;&amp;</c> so the session starts there
    /// even when the emulator opens its new shell in <c>$HOME</c> rather than the launcher's cwd;
    /// the <c>&amp;&amp;</c> means claude only runs once the directory change succeeds, while a one-off's
    /// keep-alive is joined with <c>;</c> so the window still stays open to show a <c>cd</c> failure.
    /// </summary>
    private static string PosixCommand(string file, string? cwd, TerminalLauncherOptions options, bool oneOff)
    {
        var parts = new List<string> { PosixQuote(options.ClaudeExecutable) };
        if (oneOff)
            parts.Add("-p");
        parts.AddRange(options.ExtraArgs.Select(PosixQuote));
        parts.Add($"\"$(cat {PosixQuote(file)})\"");
        var command = string.Join(" ", parts);
        if (!string.IsNullOrWhiteSpace(cwd))
            command = $"cd {PosixQuote(cwd)} && {command}";
        return oneOff ? command + PosixKeepAlive : command;
    }

    /// <summary>
    /// Appended after a one-off POSIX/macOS <c>claude -p</c> run so the terminal stays open until the
    /// user dismisses it (otherwise the shell exits and the window vanishes with the output). No prompt
    /// content enters here — it stays file-indirected.
    /// </summary>
    private const string PosixKeepAlive =
        "; printf '\\n[claude -p finished - press Enter to close] '; read -r _";

    // ── Escaping helpers ──

    /// <summary>Single-quote for PowerShell: literal text, embedded <c>'</c> doubled.</summary>
    private static string PwshQuote(string s) => $"'{s.Replace("'", "''")}'";

    /// <summary>Single-quote for POSIX shells: literal text, embedded <c>'</c> → <c>'\''</c>.</summary>
    private static string PosixQuote(string s) => $"'{s.Replace("'", "'\\''")}'";

    /// <summary>
    /// Encode a PowerShell command for <c>-EncodedCommand</c>: Base64 of the UTF-16LE bytes. The
    /// result is <c>[A-Za-z0-9+/=]</c> only, so it survives cmd.exe's parser (and <c>start</c>) intact.
    /// </summary>
    private static string EncodePwshCommand(string command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    /// <summary>Escape for an AppleScript double-quoted string literal.</summary>
    private static string AppleScriptEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
