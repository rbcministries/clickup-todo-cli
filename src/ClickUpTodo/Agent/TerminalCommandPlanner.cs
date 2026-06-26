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
                PreferredTerminal.Cmd when exists("cmd") => new LaunchSpec(
                    "cmd", ["/c", "start", "", "pwsh", "-NoExit", "-Command", command], cwd, "Command Prompt"),
                _ => null,
            };
            if (spec is not null)
                specs.Add(spec);
        }
        return specs;
    }

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
            specs.Add(new LaunchSpec(configured, ["-e", "bash", "-lc", inner], cwd, configured));

        // gnome-terminal dropped `-e` in favor of `--`; the others still take `-e`.
        foreach (var (name, sep) in new[] { ("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e") })
        {
            if (exists(name))
                specs.Add(new LaunchSpec(name, [sep, "bash", "-lc", inner], cwd, name));
        }

        return specs;
    }

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

    /// <summary>Escape for an AppleScript double-quoted string literal.</summary>
    private static string AppleScriptEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
