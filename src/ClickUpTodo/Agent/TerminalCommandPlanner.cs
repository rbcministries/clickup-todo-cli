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
/// </summary>
public static class TerminalCommandPlanner
{
    public static IReadOnlyList<LaunchSpec> Plan(
        OSPlatformKind os,
        Func<string, bool> exists,
        Func<string, string?> getEnv,
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options) => os switch
        {
            OSPlatformKind.Windows => PlanWindows(exists, promptFilePath, workingDir, options),
            OSPlatformKind.MacOS => PlanMacOS(exists, promptFilePath, workingDir, options),
            OSPlatformKind.Linux => PlanLinux(exists, getEnv, promptFilePath, workingDir, options),
            _ => [],
        };

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
        Func<string, bool> exists, string file, string? cwd, TerminalLauncherOptions options)
    {
        var command = PwshCommand(file, options); // `& 'claude' … (Get-Content -Raw '<file>')`

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
                PreferredTerminal.WindowsTerminal when exists("wt") => new LaunchSpec(
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
        Func<string, bool> exists, string file, string? cwd, TerminalLauncherOptions options)
    {
        if (!exists("osascript"))
            return [];

        var inner = PosixCommand(file, options); // `'claude' … "$(cat '<file>')"`
        var script = $"tell application \"Terminal\" to do script \"{AppleScriptEscape(inner)}\"";
        return [new LaunchSpec("osascript", ["-e", script], cwd, "Terminal (osascript)")];
    }

    // ── Linux: honor $TERMINAL, else probe common emulators ──

    private static IReadOnlyList<LaunchSpec> PlanLinux(
        Func<string, bool> exists, Func<string, string?> getEnv, string file, string? cwd, TerminalLauncherOptions options)
    {
        var inner = PosixCommand(file, options);
        var specs = new List<LaunchSpec>();

        var configured = getEnv("TERMINAL");
        if (!string.IsNullOrWhiteSpace(configured) && exists(configured))
            specs.Add(new LaunchSpec(configured, [ExecSeparator(configured), "bash", "-lc", inner], cwd, configured));

        foreach (var name in new[] { "x-terminal-emulator", "gnome-terminal", "konsole" })
        {
            if (exists(name))
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

    /// <summary>PowerShell command that runs claude with the prompt read from the file via Get-Content -Raw.</summary>
    private static string PwshCommand(string file, TerminalLauncherOptions options)
    {
        var parts = new List<string> { "&", PwshQuote(options.ClaudeExecutable) };
        parts.AddRange(options.ExtraArgs.Select(PwshQuote));
        parts.Add($"(Get-Content -Raw {PwshQuote(file)})");
        return string.Join(" ", parts);
    }

    /// <summary>POSIX shell command that runs claude with the prompt read from the file via $(cat …).</summary>
    private static string PosixCommand(string file, TerminalLauncherOptions options)
    {
        var parts = new List<string> { PosixQuote(options.ClaudeExecutable) };
        parts.AddRange(options.ExtraArgs.Select(PosixQuote));
        parts.Add($"\"$(cat {PosixQuote(file)})\"");
        return string.Join(" ", parts);
    }

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
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));

    /// <summary>Escape for an AppleScript double-quoted string literal.</summary>
    private static string AppleScriptEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
