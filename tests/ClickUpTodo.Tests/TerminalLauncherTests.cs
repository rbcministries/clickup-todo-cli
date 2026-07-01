using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the cross-platform terminal launcher (issue #25). The pure
/// <see cref="TerminalCommandPlanner"/> command/fallback logic and the
/// <see cref="TerminalLauncher"/> orchestration loop are fully exercised here without spawning a
/// real process. The actual <c>Process.Start</c> path can't run headlessly and is verified manually.
/// </summary>
public sealed class TerminalLauncherTests
{
    private const string PromptFile = "/tmp/clickup-todo/agent-prompt.txt";
    private static readonly TerminalLauncherOptions Defaults = new();

    private static Func<string, bool> Present(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static Func<string, string?> NoEnv => _ => null;

    private static IReadOnlyList<LaunchSpec> Plan(
        OSPlatformKind os, Func<string, bool> exists, TerminalLauncherOptions? options = null, Func<string, string?>? env = null)
        => TerminalCommandPlanner.Plan(os, exists, env ?? NoEnv, PromptFile, null, options ?? Defaults);

    // ── Windows ──────────────────────────────────────────────────────────────

    [Fact]
    public void Windows_PrefersWindowsTerminal_ThenFallsBackInOrder()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh", "powershell"));

        Assert.Equal(
            ["Windows Terminal", "PowerShell (pwsh)", "Windows PowerShell"],
            specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Windows_SkipsAbsentTerminals()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("powershell")); // no wt, no pwsh

        Assert.Equal(["powershell"], specs.Select(s => s.FileName));
    }

    [Fact]
    public void Windows_Cmd_RequiresPowerShellHost()
    {
        // cmd alone yields no candidate: `-EncodedCommand` needs a PowerShell host, and bare cmd.exe
        // can't run the file-reading claude invocation. So cmd only appears alongside pwsh/powershell.
        Assert.Empty(Plan(OSPlatformKind.Windows, Present("cmd")));
    }

    [Fact]
    public void Windows_Cmd_IsLastResortFallback()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh", "powershell", "cmd"));

        Assert.Equal(
            ["Windows Terminal", "PowerShell (pwsh)", "Windows PowerShell", "Command Prompt (cmd)"],
            specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Windows_Cmd_UsesStartToOpenNewWindow_HostingPwshWhenPresent()
    {
        var spec = Plan(OSPlatformKind.Windows, Present("cmd", "pwsh")).Single(s => s.FileName == "cmd");

        // `cmd /c start "" pwsh -NoExit -EncodedCommand <base64>` — the "" is start's window title.
        Assert.Equal(
            ["/c", "start", "", "pwsh", "-NoExit", "-EncodedCommand"],
            spec.Arguments.Take(6));
        Assert.Equal(7, spec.Arguments.Count);
    }

    [Fact]
    public void Windows_Cmd_FallsBackToPowerShellHost_WhenNoPwsh()
    {
        var spec = Plan(OSPlatformKind.Windows, Present("cmd", "powershell")).Single(s => s.FileName == "cmd");

        Assert.Equal("powershell", spec.Arguments[3]);
    }

    [Fact]
    public void Windows_Cmd_EncodesPwshPayload_SoItSurvivesCmdParsing()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("pwsh", "cmd"));
        var direct = specs.Single(s => s.FileName == "pwsh").Arguments[^1];   // `& 'claude' … (Get-Content -Raw '…')`
        var encoded = specs.Single(s => s.FileName == "cmd").Arguments[^1];   // the -EncodedCommand base64

        // The base64 blob carries no cmd/start-special characters, so cmd.exe can't mis-tokenize it.
        Assert.DoesNotContain(encoded, c => c is '&' or '(' or ')' or '\'' or '"' or ' ');

        // Decoding it (Base64 → UTF-16LE) reproduces the exact pwsh command the direct candidate runs.
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(direct, decoded);
        Assert.Contains("Get-Content -Raw", decoded);
        Assert.Contains(PromptFile, decoded);
    }

    [Fact]
    public void Windows_Cmd_Preferred_PinsToFront_KeepingFallback()
    {
        var options = Defaults with { Preferred = PreferredTerminal.Cmd };

        var specs = Plan(OSPlatformKind.Windows, Present("cmd", "powershell", "wt"), options);

        Assert.Equal("Command Prompt (cmd)", specs[0].DisplayName);
        Assert.Contains("Windows Terminal", specs.Select(s => s.DisplayName)); // fallback preserved
    }

    [Fact]
    public void Windows_Cmd_Preferred_ButNoPowerShellHost_YieldsNoCmdCandidate()
    {
        // Pinning cmd can't conjure a PowerShell host: the gate still applies and cmd is dropped,
        // falling through to whatever else is present (here, nothing).
        var options = Defaults with { Preferred = PreferredTerminal.Cmd };

        Assert.Empty(Plan(OSPlatformKind.Windows, Present("cmd"), options));
    }

    [Fact]
    public void Windows_WindowsTerminal_BuildsNewTabPwshArgv()
    {
        var spec = Plan(OSPlatformKind.Windows, Present("wt"))[0];

        Assert.Equal("wt", spec.FileName);
        Assert.Equal(["new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(4));
        Assert.Contains("Get-Content -Raw", spec.Arguments[^1]);
        Assert.Contains(PromptFile, spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_Pwsh_BuildsNoExitCommandArgv()
    {
        var spec = Plan(OSPlatformKind.Windows, Present("pwsh"))[0];

        Assert.Equal("pwsh", spec.FileName);
        Assert.Equal(["-NoExit", "-Command"], spec.Arguments.Take(2));
        Assert.Equal(3, spec.Arguments.Count);
    }

    [Fact]
    public void Windows_Preferred_PinsTerminalToFront_KeepingFallback()
    {
        var options = Defaults with { Preferred = PreferredTerminal.Pwsh };

        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), options);

        Assert.Equal("pwsh", specs[0].FileName);
        Assert.Equal(["pwsh", "wt"], specs.Select(s => s.FileName)); // preference first, rest follow
    }

    [Fact]
    public void Windows_Command_HonorsCustomExecutableAndExtraArgs_InOrder()
    {
        var options = Defaults with { ClaudeExecutable = "claude.cmd", ExtraArgs = ["--model", "opus"] };

        var command = Plan(OSPlatformKind.Windows, Present("pwsh"), options)[0].Arguments[^1];

        // Extra args land between the executable and the prompt argument, in order.
        Assert.Equal(
            "& 'claude.cmd' '--model' 'opus' (Get-Content -Raw '/tmp/clickup-todo/agent-prompt.txt')",
            command);
    }

    [Fact]
    public void Posix_Command_PlacesExtraArgsBeforePromptArgument()
    {
        var options = Defaults with { ExtraArgs = ["--model", "opus"] };

        var inner = Plan(OSPlatformKind.Linux, Present("konsole"), options)[0].Arguments[3];

        Assert.Equal(
            "'claude' '--model' 'opus' \"$(cat '/tmp/clickup-todo/agent-prompt.txt')\"",
            inner);
    }

    // ── macOS ────────────────────────────────────────────────────────────────

    [Fact]
    public void MacOS_UsesOsascriptDrivingTerminal()
    {
        var specs = Plan(OSPlatformKind.MacOS, Present("osascript"));

        var spec = Assert.Single(specs);
        Assert.Equal("osascript", spec.FileName);
        Assert.Equal("-e", spec.Arguments[0]);
        Assert.Contains("tell application \"Terminal\" to do script", spec.Arguments[1]);
        Assert.Contains("$(cat '/tmp/clickup-todo/agent-prompt.txt')", spec.Arguments[1]);
    }

    [Fact]
    public void MacOS_NoOsascript_NoCandidates()
        => Assert.Empty(Plan(OSPlatformKind.MacOS, Present()));

    // ── Linux ────────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_HonorsTerminalEnvFirst()
    {
        var env = (string k) => k == "TERMINAL" ? "alacritty" : null;

        var specs = Plan(OSPlatformKind.Linux, Present("alacritty", "gnome-terminal"), env: env);

        Assert.Equal("alacritty", specs[0].FileName);
        Assert.Equal(["-e", "bash", "-lc"], specs[0].Arguments.Take(3));
    }

    [Fact]
    public void Linux_TerminalEnv_UsesCorrectSeparatorForKnownTerminal()
    {
        // A user-set TERMINAL=gnome-terminal must get `--`, not the deprecated/removed `-e`.
        var env = (string k) => k == "TERMINAL" ? "gnome-terminal" : null;

        var spec = Plan(OSPlatformKind.Linux, Present("gnome-terminal"), env: env)[0];

        Assert.Equal("gnome-terminal", spec.FileName);
        Assert.Equal(["--", "bash", "-lc"], spec.Arguments.Take(3));
    }

    [Fact]
    public void Linux_ProbesKnownEmulatorsInOrder()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("gnome-terminal", "konsole", "x-terminal-emulator"));

        Assert.Equal(["x-terminal-emulator", "gnome-terminal", "konsole"], specs.Select(s => s.FileName));
    }

    [Fact]
    public void Linux_GnomeTerminal_UsesDoubleDashSeparator()
    {
        var spec = Plan(OSPlatformKind.Linux, Present("gnome-terminal"))[0];

        Assert.Equal("gnome-terminal", spec.FileName);
        Assert.Equal(["--", "bash", "-lc"], spec.Arguments.Take(3));
        Assert.Contains("\"$(cat '/tmp/clickup-todo/agent-prompt.txt')\"", spec.Arguments[3]);
    }

    [Fact]
    public void Linux_IgnoresBlankOrAbsentTerminalEnv()
    {
        var env = (string k) => k == "TERMINAL" ? "   " : null;

        var specs = Plan(OSPlatformKind.Linux, Present("konsole"), env: env);

        Assert.Equal("konsole", Assert.Single(specs).FileName);
    }

    // ── Safety: prompt content stays in the file, only the path is inlined ──────

    [Fact]
    public void AllPlatforms_ReferenceTheFileByPath_NeverInlinePromptContent()
    {
        foreach (var (os, exists, env) in new (OSPlatformKind, Func<string, bool>, Func<string, string?>)[]
        {
            (OSPlatformKind.Windows, Present("pwsh"), NoEnv),
            (OSPlatformKind.MacOS, Present("osascript"), NoEnv),
            (OSPlatformKind.Linux, Present("gnome-terminal"), NoEnv),
        })
        {
            var command = string.Join(" ", TerminalCommandPlanner
                .Plan(os, exists, env, PromptFile, null, Defaults)[0].Arguments);
            Assert.Contains(PromptFile, command); // the path is referenced
            Assert.Matches("Get-Content -Raw|cat ", command); // read from the file at run time
        }
    }

    [Fact]
    public void Windows_EscapesSingleQuoteInPath_ForPowerShell()
    {
        var weird = "/tmp/o'brien/prompt.txt";

        var command = TerminalCommandPlanner
            .Plan(OSPlatformKind.Windows, Present("pwsh"), NoEnv, weird, null, Defaults)[0].Arguments[^1];

        Assert.Contains("o''brien", command); // PowerShell doubles the embedded quote
    }

    [Fact]
    public void Posix_EscapesSingleQuoteInPath()
    {
        var weird = "/tmp/o'brien/prompt.txt";

        var command = TerminalCommandPlanner
            .Plan(OSPlatformKind.Linux, Present("konsole"), NoEnv, weird, null, Defaults)[0].Arguments[3];

        Assert.Contains("o'\\''brien", command); // POSIX '\'' escaping
    }

    [Fact]
    public void WorkingDirectory_FlowsOntoEverySpec()
    {
        var specs = TerminalCommandPlanner.Plan(
            OSPlatformKind.Windows, Present("wt", "pwsh", "cmd"), NoEnv, PromptFile, "/work/dir", Defaults);

        Assert.Contains(specs, s => s.FileName == "cmd"); // cmd candidate is in the set …
        Assert.All(specs, s => Assert.Equal("/work/dir", s.WorkingDirectory)); // … and carries the cwd too
    }

    [Fact]
    public void Unknown_OS_NoCandidates()
        => Assert.Empty(Plan(OSPlatformKind.Unknown, Present("wt", "pwsh", "bash", "osascript")));

    // ── Launcher orchestration (no real process) ───────────────────────────────

    private static TerminalLauncher Launcher(
        OSPlatformKind os, Func<string, bool> exists, Func<LaunchSpec, bool> start, Func<string, bool>? fileExists = null)
        => new(os: os, exists: exists, getEnv: NoEnv, fileExists: fileExists ?? (_ => true), start: start);

    [Fact]
    public async Task Launch_TriesNextCandidate_WhenFirstFailsToStart()
    {
        var started = new List<string>();
        Func<LaunchSpec, bool> start = s =>
        {
            started.Add(s.FileName);
            return s.FileName != "wt"; // wt fails, pwsh succeeds
        };
        var launcher = Launcher(OSPlatformKind.Windows, Present("wt", "pwsh"), start);

        var result = await launcher.LaunchAsync(PromptFile, null, Defaults);

        Assert.True(result.Success);
        Assert.Equal(["wt", "pwsh"], started); // fell through wt to pwsh
        Assert.Contains("pwsh", result.LaunchedWith);
    }

    [Fact]
    public async Task Launch_Fails_WhenNoTerminalPresent()
    {
        var launcher = Launcher(OSPlatformKind.Linux, Present(), _ => true);

        var result = await launcher.LaunchAsync(PromptFile, null, Defaults);

        Assert.False(result.Success);
        Assert.Contains("No terminal", result.Error);
    }

    [Fact]
    public async Task Launch_Fails_WhenEveryCandidateFailsToStart()
    {
        var launcher = Launcher(OSPlatformKind.Windows, Present("wt", "pwsh"), _ => false);

        var result = await launcher.LaunchAsync(PromptFile, null, Defaults);

        Assert.False(result.Success);
        Assert.Contains("failed to start", result.Error);
    }

    [Fact]
    public async Task Launch_Fails_WhenPromptFileMissing()
    {
        var launcher = Launcher(OSPlatformKind.Windows, Present("pwsh"), _ => true, fileExists: _ => false);

        var result = await launcher.LaunchAsync(PromptFile, null, Defaults);

        Assert.False(result.Success);
        Assert.Contains("Prompt file not found", result.Error);
    }

    [Fact]
    public async Task Launch_NotesWhenClaudeNotOnPath_WithoutPollutingTerminalName()
    {
        // pwsh present (so a terminal starts) but `claude` absent from PATH.
        var launcher = Launcher(OSPlatformKind.Windows, Present("pwsh"), _ => true);

        var result = await launcher.LaunchAsync(PromptFile, null, Defaults);

        Assert.True(result.Success);
        Assert.Equal("PowerShell (pwsh)", result.LaunchedWith); // clean terminal name only
        Assert.Contains("not found on PATH", result.Note);      // warning lives in Note
    }

    [Fact]
    public async Task Launch_NoNote_WhenClaudeIsOnPath()
    {
        var launcher = Launcher(OSPlatformKind.Windows, Present("pwsh", "claude"), _ => true);

        var result = await launcher.LaunchAsync(PromptFile, null, Defaults);

        Assert.True(result.Success);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task Launch_HonorsCancellation_BeforeStartingAProcess()
    {
        var started = false;
        var launcher = Launcher(OSPlatformKind.Windows, Present("pwsh"), _ => { started = true; return true; });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => launcher.LaunchAsync(PromptFile, null, Defaults, cts.Token));
        Assert.False(started); // cancelled before any process was started
    }
}
