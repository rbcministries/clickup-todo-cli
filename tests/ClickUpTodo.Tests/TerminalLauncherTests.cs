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

    // ── session mode: one-off `claude -p` vs interactive (#94) ─────────────────

    private static IReadOnlyList<LaunchSpec> PlanOneOff(
        OSPlatformKind os, Func<string, bool> exists, TerminalLauncherOptions? options = null, Func<string, string?>? env = null)
        => TerminalCommandPlanner.Plan(os, exists, env ?? NoEnv, PromptFile, null, options ?? Defaults, oneOff: true);

    [Fact]
    public void OneOff_Pwsh_InsertsDashP_AfterExecutable_BeforeExtraArgs_PromptStillFileRead()
    {
        var options = Defaults with { ExtraArgs = ["--model", "opus"] };

        var command = PlanOneOff(OSPlatformKind.Windows, Present("pwsh"), options)[0].Arguments[^1];

        // `-p` lands right after the executable, before the user's extra args; prompt still file-read.
        Assert.Equal(
            "& 'claude' '-p' '--model' 'opus' (Get-Content -Raw '/tmp/clickup-todo/agent-prompt.txt')",
            command);
    }

    [Fact]
    public void Interactive_Pwsh_OmitsDashP()
    {
        var command = Plan(OSPlatformKind.Windows, Present("pwsh"))[0].Arguments[^1];

        Assert.DoesNotContain("'-p'", command);
    }

    [Fact]
    public void OneOff_Pwsh_NeedsNoKeepAlive_HostsAlreadyUseNoExit()
    {
        // The PowerShell hosts launch with -NoExit, so the window survives a one-off; no `read`
        // keep-alive is appended (that's the POSIX-only concern).
        var command = PlanOneOff(OSPlatformKind.Windows, Present("pwsh"))[0].Arguments[^1];

        Assert.DoesNotContain("read -r", command);
        Assert.Contains("-NoExit", Plan(OSPlatformKind.Windows, Present("pwsh"))[0].Arguments); // sanity
    }

    [Fact]
    public void OneOff_Posix_InsertsDashP_AndAppendsKeepAlive_PromptStillFileRead()
    {
        var options = Defaults with { ExtraArgs = ["--model", "opus"] };

        var inner = PlanOneOff(OSPlatformKind.Linux, Present("konsole"), options)[0].Arguments[3];

        // `-p` after the executable, prompt still read from the file, and a keep-alive so the terminal
        // doesn't close before the user reads the output.
        Assert.StartsWith(
            "'claude' -p '--model' 'opus' \"$(cat '/tmp/clickup-todo/agent-prompt.txt')\"",
            inner);
        Assert.Contains("$(cat '/tmp/clickup-todo/agent-prompt.txt')", inner); // file-read, not inlined
        Assert.Contains("read -r _", inner);                                   // keep-alive
    }

    [Fact]
    public void Interactive_Posix_OmitsDashP_AndKeepAlive()
    {
        var inner = Plan(OSPlatformKind.Linux, Present("konsole"))[0].Arguments[3];

        Assert.Equal("'claude' \"$(cat '/tmp/clickup-todo/agent-prompt.txt')\"", inner);
        Assert.DoesNotContain("read -r", inner);
    }

    [Fact]
    public void OneOff_MacOS_InsertsDashP_AndKeepAlive_InTheDoScript()
    {
        var script = PlanOneOff(OSPlatformKind.MacOS, Present("osascript"))[0].Arguments[1];

        Assert.Contains("'claude' -p ", script);
        Assert.Contains("$(cat '/tmp/clickup-todo/agent-prompt.txt')", script); // file-read
        Assert.Contains("read -r _", script);                                   // keep-alive
        // AppleScriptEscape doubles the backslash so osascript's string parse hands printf a real
        // "\n"; pin it so a future escaping refactor can't silently break the keep-alive newline.
        Assert.Contains(@"printf '\\n", script);
    }

    [Fact]
    public void OneOff_AllPlatforms_KeepPromptFileIndirected_NeverInlineContent()
    {
        foreach (var (os, exists, env) in new (OSPlatformKind, Func<string, bool>, Func<string, string?>)[]
        {
            (OSPlatformKind.Windows, Present("pwsh"), NoEnv),
            (OSPlatformKind.MacOS, Present("osascript"), NoEnv),
            (OSPlatformKind.Linux, Present("gnome-terminal"), NoEnv),
        })
        {
            var command = string.Join(" ", PlanOneOff(os, exists, env: env)[0].Arguments);
            Assert.Contains(PromptFile, command);
            Assert.Matches("Get-Content -Raw|cat ", command);
        }
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

    // ── Linux: broadened emulator detection (#307) ─────────────────────────────
    //
    // The issue calls for detecting `xterm` and the modern emulators (not just the original
    // x-terminal-emulator/gnome-terminal/konsole three), each with the right command-invocation
    // syntax, plus a tmux/multiplexer path.

    [Theory]
    [InlineData("xterm", new[] { "-e", "bash", "-lc" })]
    [InlineData("alacritty", new[] { "-e", "bash", "-lc" })]
    [InlineData("xfce4-terminal", new[] { "-x", "bash", "-lc" })]
    [InlineData("terminator", new[] { "-x", "bash", "-lc" })]
    [InlineData("kitty", new[] { "bash", "-lc" })]
    [InlineData("foot", new[] { "bash", "-lc" })]
    [InlineData("wezterm", new[] { "start", "--", "bash", "-lc" })]
    public void Linux_NewEmulator_UsesCorrectExecPrefix(string emulator, string[] expectedPrefix)
    {
        var spec = Assert.Single(Plan(OSPlatformKind.Linux, Present(emulator)));

        Assert.Equal(emulator, spec.FileName);
        Assert.Equal(expectedPrefix, spec.Arguments.Take(expectedPrefix.Length));
        // The inner command is always the final argument, prompt still file-indirected.
        Assert.Equal("'claude' \"$(cat '/tmp/clickup-todo/agent-prompt.txt')\"", spec.Arguments[^1]);
        Assert.Equal(expectedPrefix.Length + 1, spec.Arguments.Count);
    }

    [Fact]
    public void Linux_ProbesBroadenedEmulatorList_InOrder()
    {
        // Every supported emulator present at once — confirm the documented fallback order.
        var all = Present(
            "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal",
            "alacritty", "kitty", "wezterm", "foot", "xterm", "terminator");

        var specs = Plan(OSPlatformKind.Linux, all);

        Assert.Equal(
            [
                "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal",
                "alacritty", "kitty", "wezterm", "foot", "xterm", "terminator",
            ],
            specs.Select(s => s.FileName));
    }

    [Fact]
    public void Linux_NewEmulator_BakesCdIntoWorkingDirectory()
    {
        // A non-VTE emulator (kitty, no exec flag) must still land in the working directory.
        var inner = PlanCwd(OSPlatformKind.Linux, Present("kitty"), "/work/dir")[0].Arguments[^1];

        Assert.Equal(
            "cd '/work/dir' && 'claude' \"$(cat '/tmp/clickup-todo/agent-prompt.txt')\"",
            inner);
    }

    [Fact]
    public void Linux_TerminalEnv_NamingAProbedEmulator_IsNotAddedTwice()
    {
        // $TERMINAL=xterm and xterm also on PATH: it must appear exactly once (the $TERMINAL entry),
        // not once for $TERMINAL and again from the probe loop.
        var env = (string k) => k == "TERMINAL" ? "xterm" : null;

        var specs = Plan(OSPlatformKind.Linux, Present("xterm"), env: env);

        Assert.Equal("xterm", Assert.Single(specs).FileName);
    }

    // ── Linux: tmux multiplexer path (#307) ────────────────────────────────────

    [Fact]
    public void Linux_Tmux_HeadlessSession_YieldsRunnableNewWindowSpec()
    {
        // No GUI emulator on PATH, but we're inside tmux: instead of "no terminal found", emit a
        // `tmux new-window` spec so a headless tmux-over-SSH session can still launch.
        var env = Env(("TMUX", "/tmp/tmux-1000/default,123,0"));

        var spec = Assert.Single(Plan(OSPlatformKind.Linux, Present("tmux"), env: env));

        Assert.Equal("tmux", spec.FileName);
        Assert.Equal(["new-window", "bash", "-lc"], spec.Arguments.Take(3));
        Assert.Equal("tmux (new window)", spec.DisplayName);
        Assert.Contains("$(cat '/tmp/clickup-todo/agent-prompt.txt')", spec.Arguments[^1]); // file-indirected
    }

    [Fact]
    public void Linux_Tmux_NewWindowRequest_TmuxIsLastResortAfterGuiEmulators()
    {
        // Default (new-window) inside tmux with a GUI emulator present: the GUI window is preferred,
        // tmux is the final fallback (so the launcher only reaches it if the GUI window won't start).
        var env = Env(("TMUX", "/tmp/tmux-1000/default,123,0"));

        var specs = Plan(OSPlatformKind.Linux, Present("gnome-terminal", "tmux"), env: env);

        Assert.Equal(["gnome-terminal", "tmux"], specs.Select(s => s.FileName));
    }

    [Fact]
    public void Linux_Tmux_TabRequest_TmuxTabIsTriedBeforeWindowFallbacks()
    {
        // A tab request inside tmux (no detected GUI-emulator tab) puts the tmux new-window ahead of
        // the GUI window fallback, mirroring the gnome/konsole tab-before-window ordering.
        var env = Env(("TMUX", "/tmp/tmux-1000/default,123,0"));

        var specs = PlanTab(OSPlatformKind.Linux, Present("xterm", "tmux"), env);

        Assert.Equal("tmux", specs[0].FileName);
        Assert.Equal("tmux (new window)", specs[0].DisplayName);
        Assert.Contains(specs, s => s.FileName == "xterm"); // window fallback retained
    }

    [Fact]
    public void Linux_Tmux_DetectedGuiTab_IsPreferredOverTmux()
    {
        // Inside both gnome-terminal and tmux with a tab request: the real gnome tab is tried first,
        // tmux after it.
        var env = Env(("VTE_VERSION", "6003"), ("TMUX", "/tmp/tmux-1000/default,123,0"));

        var specs = PlanTab(OSPlatformKind.Linux, Present("gnome-terminal", "tmux"), env);

        Assert.Equal("gnome-terminal (new tab)", specs[0].DisplayName);
        Assert.Contains(specs, s => s.FileName == "tmux");
    }

    [Fact]
    public void Linux_Tmux_NotInsideTmux_NoTmuxSpec()
    {
        // tmux is installed but $TMUX is unset (we're not inside a session): no tmux spec is emitted.
        var specs = Plan(OSPlatformKind.Linux, Present("tmux"));

        Assert.Empty(specs);
    }

    [Fact]
    public void Linux_Tmux_InsideTmuxButTmuxNotOnPath_NoTmuxSpec()
    {
        // The other half of the guard: $TMUX is set but tmux isn't on PATH (e.g. inherited env in a
        // container where tmux was removed). Only the GUI emulator is offered — no bogus tmux spec.
        var env = Env(("TMUX", "/tmp/tmux-1000/default,123,0"));

        var specs = Plan(OSPlatformKind.Linux, Present("xterm"), env: env);

        Assert.Equal("xterm", Assert.Single(specs).FileName);
    }

    [Fact]
    public void Linux_Tmux_OneOff_KeepAliveRidesAlong()
    {
        var env = Env(("TMUX", "/tmp/tmux-1000/default,123,0"));

        var spec = Assert.Single(PlanOneOff(OSPlatformKind.Linux, Present("tmux"), env: env));

        Assert.Equal("tmux", spec.FileName);
        Assert.Contains("'claude' -p ", spec.Arguments[^1]);
        Assert.Contains("read -r _", spec.Arguments[^1]); // POSIX keep-alive
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

    // ── working directory baked into the command ───────────────────────────────
    //
    // The emulators we prefer (wt, gnome-terminal, konsole, Terminal.app) ignore the launcher's
    // process cwd and open in $HOME, so the command itself must change directory. These pin that the
    // cd/Set-Location is present, correctly escaped, and ordered so claude runs *in* the directory.

    private static IReadOnlyList<LaunchSpec> PlanCwd(
        OSPlatformKind os, Func<string, bool> exists, string? cwd, bool oneOff = false, TerminalLauncherOptions? options = null, Func<string, string?>? env = null)
        => TerminalCommandPlanner.Plan(os, exists, env ?? NoEnv, PromptFile, cwd, options ?? Defaults, oneOff);

    [Fact]
    public void Posix_Interactive_PrependsCdIntoWorkingDirectory()
    {
        var inner = PlanCwd(OSPlatformKind.Linux, Present("konsole"), "/work/dir")[0].Arguments[3];

        Assert.Equal(
            "cd '/work/dir' && 'claude' \"$(cat '/tmp/clickup-todo/agent-prompt.txt')\"",
            inner);
    }

    [Fact]
    public void Posix_OneOff_CdRunsBeforeClaude_KeepAliveStaysAfter()
    {
        var inner = PlanCwd(OSPlatformKind.Linux, Present("konsole"), "/work/dir", oneOff: true)[0].Arguments[3];

        // cd guards claude with `&&`, but the keep-alive is joined with `;` so the window stays open
        // to show a cd failure too.
        Assert.StartsWith("cd '/work/dir' && 'claude' -p ", inner);
        Assert.Contains("$(cat '/tmp/clickup-todo/agent-prompt.txt')", inner);
        Assert.Contains("read -r _", inner);
        Assert.True(inner.IndexOf("cd '/work/dir'", StringComparison.Ordinal)
            < inner.IndexOf("read -r _", StringComparison.Ordinal));
    }

    [Fact]
    public void Posix_NoWorkingDirectory_OmitsCd()
    {
        var inner = PlanCwd(OSPlatformKind.Linux, Present("konsole"), null)[0].Arguments[3];

        Assert.DoesNotContain("cd ", inner);
    }

    [Fact]
    public void Posix_EscapesSingleQuoteInWorkingDirectory()
    {
        var inner = PlanCwd(OSPlatformKind.Linux, Present("konsole"), "/work/o'brien")[0].Arguments[3];

        Assert.StartsWith("cd '/work/o'\\''brien' &&", inner); // POSIX '\'' escaping on the dir too
    }

    [Fact]
    public void MacOS_BakesCdIntoTheDoScript()
    {
        var script = PlanCwd(OSPlatformKind.MacOS, Present("osascript"), "/work/dir")[0].Arguments[1];

        Assert.Contains("cd '/work/dir' && 'claude'", script);
    }

    [Fact]
    public void Windows_PrependsSetLocationIntoWorkingDirectory()
    {
        var command = PlanCwd(OSPlatformKind.Windows, Present("pwsh"), "C:/work/dir")[0].Arguments[^1];

        Assert.Equal(
            "Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop; & 'claude' (Get-Content -Raw '/tmp/clickup-todo/agent-prompt.txt')",
            command);
    }

    [Fact]
    public void Windows_WindowsTerminal_BakesSetLocationIntoTheTab()
    {
        // wt is the emulator that most visibly ignores the process cwd, so the Set-Location must reach
        // the command it runs in the new tab. The `;` between Set-Location and the claude invocation is
        // escaped as `\;` (#534) so Windows Terminal doesn't split it into a bogus second tab.
        var command = PlanCwd(OSPlatformKind.Windows, Present("wt"), "C:/work/dir")[0].Arguments[^1];

        Assert.StartsWith(@"Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop\;", command);
    }

    [Fact]
    public void Windows_Cmd_EncodedCommand_CarriesSetLocation()
    {
        // The cmd fallback base64-encodes the same pwsh command, so the Set-Location rides along by
        // construction; decode it back to prove the cwd survives the EncodedCommand hop.
        var encoded = PlanCwd(OSPlatformKind.Windows, Present("cmd", "pwsh"), "C:/work/dir")
            .Single(s => s.FileName == "cmd").Arguments[^1];

        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.StartsWith("Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop;", decoded);
    }

    [Fact]
    public void Linux_GnomeTerminal_BakesCdIntoWorkingDirectory()
    {
        // gnome-terminal uses the `--` separator (not `-e`); confirm the cwd reaches its command too.
        var inner = PlanCwd(OSPlatformKind.Linux, Present("gnome-terminal"), "/work/dir")[0].Arguments[3];

        Assert.StartsWith("cd '/work/dir' && 'claude'", inner);
    }

    [Fact]
    public void Windows_NoWorkingDirectory_OmitsSetLocation()
    {
        var command = PlanCwd(OSPlatformKind.Windows, Present("pwsh"), null)[0].Arguments[^1];

        Assert.DoesNotContain("Set-Location", command);
    }

    [Fact]
    public void Windows_EscapesSingleQuoteInWorkingDirectory_ForPowerShell()
    {
        var command = PlanCwd(OSPlatformKind.Windows, Present("pwsh"), "C:/work/o'brien")[0].Arguments[^1];

        Assert.StartsWith("Set-Location -LiteralPath 'C:/work/o''brien' -ErrorAction Stop;", command); // PowerShell doubles the quote
    }

    [Fact]
    public void WorkingDirectoryInCommand_KeepsPromptFileIndirected_NeverInlineContent()
    {
        foreach (var (os, exists) in new (OSPlatformKind, Func<string, bool>)[]
        {
            (OSPlatformKind.Windows, Present("pwsh")),
            (OSPlatformKind.MacOS, Present("osascript")),
            (OSPlatformKind.Linux, Present("gnome-terminal")),
        })
        {
            var command = string.Join(" ", PlanCwd(os, exists, "/work/dir")[0].Arguments);
            Assert.Contains(PromptFile, command);
            Assert.Matches("Get-Content -Raw|cat ", command);
        }
    }

    // ── Windows Terminal: escape the `;` subcommand delimiter (#534) ────────────
    //
    // `;` is WT's own subcommand delimiter and WT splits on it *inside* arguments (quoting doesn't
    // protect it). The `Set-Location …; <command>` working-directory prefix (#252) therefore tore the
    // dispatch into two tabs — pwsh in the right dir with no claude, plus a bogus default-profile tab
    // failing with 0x80070002. `WtArgs` now escapes every `;` as `\;` (WT's documented escape). Only
    // the `wt` argv changes; pwsh/powershell/cmd and POSIX/macOS stay byte-identical.

    /// <summary>
    /// True if <paramref name="s"/> contains a Windows-Terminal-splitting <c>;</c> that is not escaped
    /// as <c>\;</c>. (Strip the escaped occurrences, then look for any bare <c>;</c> left.)
    /// </summary>
    private static bool HasUnescapedSemicolon(string s) => s.Replace("\\;", "").Contains(';');

    private static LaunchSpec WtSpec(
        Func<string, bool> present, TerminalLauncherOptions options, string? cwd = null, Func<string, string?>? env = null)
        => TerminalCommandPlanner
            .Plan(OSPlatformKind.Windows, present, env ?? NoEnv, PromptFile, cwd, options)
            .Single(s => s.FileName == "wt");

    [Fact]
    public void Windows_Wt_WorkingDirectory_NewWindowForm_HasNoUnescapedSemicolon()
    {
        var spec = WtSpec(Present("wt"), Defaults, "C:/work/dir");

        Assert.Equal(["new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(4)); // structural tokens intact
        Assert.All(spec.Arguments, a => Assert.False(HasUnescapedSemicolon(a)));
        Assert.Contains(@"Stop\;", spec.Arguments[^1]); // the Set-Location `;` is actually escaped, not dropped
    }

    [Fact]
    public void Windows_Wt_WorkingDirectory_CurrentWindowTabForm_HasNoUnescapedSemicolon()
    {
        // The `-w 0 new-tab` form (inside WT, tab requested) goes through the same WtArgs choke point.
        var options = Defaults with { LaunchLocation = LaunchLocation.NewTab };
        var spec = WtSpec(Present("wt"), options, "C:/work/dir", Env(("WT_SESSION", "abc")));

        Assert.Equal(["-w", "0", "new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
        Assert.All(spec.Arguments, a => Assert.False(HasUnescapedSemicolon(a)));
        Assert.Contains(@"Stop\;", spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_Wt_WorkingDirectory_WithProfile_HasNoUnescapedSemicolon()
    {
        // With a #462 `-p <profile>` present, the argv still carries no unescaped `;`.
        var options = Defaults with { WindowsTerminalProfile = "My Project" };
        var spec = WtSpec(Present("wt"), options, "C:/work/dir");

        Assert.Equal(["new-tab", "-p", "My Project", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
        Assert.All(spec.Arguments, a => Assert.False(HasUnescapedSemicolon(a)));
        Assert.Contains(@"Stop\;", spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_Wt_EscapesSemicolon_InClaudeExecutable()
    {
        // A `;` embedded anywhere that reaches the wt argv is escaped — here in the executable path,
        // with no working directory so the executable is the only source of a `;`.
        var options = Defaults with { ClaudeExecutable = "cla;ude" };
        var spec = WtSpec(Present("wt"), options);

        Assert.All(spec.Arguments, a => Assert.False(HasUnescapedSemicolon(a)));
        Assert.Contains(@"'cla\;ude'", spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_Wt_EscapesSemicolon_InExtraArgs()
    {
        var options = Defaults with { ExtraArgs = ["--flag=a;b"] };
        var spec = WtSpec(Present("wt"), options);

        Assert.All(spec.Arguments, a => Assert.False(HasUnescapedSemicolon(a)));
        Assert.Contains(@"'--flag=a\;b'", spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_Wt_EscapesSemicolon_InProfileName()
    {
        // A `;` in a matched profile name must be escaped in its own `-p` arg (no working directory,
        // so the profile is the only `;` source).
        var options = Defaults with { WindowsTerminalProfile = "My;Profile" };
        var spec = WtSpec(Present("wt"), options);

        var pIndex = Array.IndexOf(spec.Arguments.ToArray(), "-p");
        Assert.True(pIndex >= 0);
        Assert.Equal(@"My\;Profile", spec.Arguments[pIndex + 1]);
        Assert.All(spec.Arguments, a => Assert.False(HasUnescapedSemicolon(a)));
    }

    [Fact]
    public void Windows_Wt_BlankWorkingDirectory_HasNoSemicolonAtAll_ByteIdenticalToToday()
    {
        // No working directory ⇒ no Set-Location prefix ⇒ no `;` to escape; the argv is exactly today's.
        var spec = WtSpec(Present("wt"), Defaults);

        Assert.DoesNotContain(spec.Arguments, a => a.Contains(';') || a.Contains('\\'));
        Assert.Equal(
            ["new-tab", "pwsh", "-NoExit", "-Command",
             "& 'claude' (Get-Content -Raw '/tmp/clickup-todo/agent-prompt.txt')"],
            spec.Arguments);
    }

    [Fact]
    public void Windows_NonWt_Hosts_KeepLiteralSemicolon_NoEscapingLeaks()
    {
        // The escape lives only at the wt boundary. The direct pwsh host re-parses nothing, so its
        // Set-Location `;` stays literal; and the cmd `-EncodedCommand` base64 must still decode to the
        // literal (unescaped) `;` command — proving no `\;` leaked into paths that don't need it.
        var specs = TerminalCommandPlanner.Plan(
            OSPlatformKind.Windows, Present("pwsh", "cmd"), NoEnv, PromptFile, "C:/work/dir", Defaults);

        var pwsh = specs.Single(s => s.FileName == "pwsh").Arguments[^1];
        Assert.StartsWith("Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop;", pwsh);
        Assert.DoesNotContain(@"Stop\;", pwsh);

        var encoded = specs.Single(s => s.FileName == "cmd").Arguments[^1];
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.StartsWith("Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop;", decoded);
        Assert.DoesNotContain(@"Stop\;", decoded);
    }

    [Fact]
    public void PlanAppLaunch_Wt_ArgvByteIdentical_NoSemicolonToEscape()
    {
        // The app-launch command has no working directory and no `;`, so the escape is a no-op and the
        // wt argv is byte-identical to today.
        var spec = TerminalCommandPlanner.PlanAppLaunch(
            OSPlatformKind.Windows, Present("wt"), NoEnv, new AppLaunchCommand("clickup-todo", ["--task", "86abc"]),
            Defaults).Single(s => s.FileName == "wt");

        Assert.DoesNotContain(spec.Arguments, a => a.Contains(';') || a.Contains('\\'));
        Assert.Equal(
            ["new-tab", "pwsh", "-NoExit", "-Command", "& 'clickup-todo' '--task' '86abc'"],
            spec.Arguments);
    }

    // ── New-tab launch location (#255) ──────────────────────────────────────────
    //
    // Opt-in, interactive-only, detection-gated per emulator: a tab spec is emitted only when the
    // user asked for a tab AND an env var proves we're inside that host; otherwise today's window
    // spec stands. The baked-in cd/Set-Location (#252) must ride along on the tab command too.

    private static readonly TerminalLauncherOptions Tab = new() { LaunchLocation = LaunchLocation.NewTab };

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return k => map.TryGetValue(k, out var v) ? v : null;
    }

    private static IReadOnlyList<LaunchSpec> PlanTab(
        OSPlatformKind os, Func<string, bool> exists, Func<string, string?> env, string? cwd = null, bool oneOff = false)
        => TerminalCommandPlanner.Plan(os, exists, env, PromptFile, cwd, Tab, oneOff);

    [Fact]
    public void NewTab_Windows_WindowsTerminal_TargetsCurrentWindow_WhenInsideWt()
    {
        var spec = PlanTab(OSPlatformKind.Windows, Present("wt"), Env(("WT_SESSION", "abc")))[0];

        Assert.Equal("wt", spec.FileName);
        Assert.Equal(["-w", "0", "new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
        Assert.Equal("Windows Terminal (new tab)", spec.DisplayName);
    }

    [Fact]
    public void NewTab_Windows_FallsBackToNewWindow_WhenNotInsideWt()
    {
        // No WT_SESSION → we can't target the current window, so keep today's `wt new-tab` (new window).
        var spec = PlanTab(OSPlatformKind.Windows, Present("wt"), NoEnv)[0];

        Assert.Equal(["new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(4));
        Assert.Equal("Windows Terminal", spec.DisplayName);
    }

    [Fact]
    public void NewTab_Windows_OneOff_StaysNewWindow_EvenInsideWt()
    {
        // A one-off `-p` terminal launch is never a tab (the issue: new-tab is meaningless for one-off).
        var spec = PlanTab(OSPlatformKind.Windows, Present("wt"), Env(("WT_SESSION", "abc")), oneOff: true)[0];

        Assert.Equal("Windows Terminal", spec.DisplayName);
        Assert.DoesNotContain("-w", spec.Arguments);
    }

    [Fact]
    public void DefaultLaunchLocation_IsNewWindow_UnchangedWtBehavior_EvenInsideWt()
    {
        var spec = TerminalCommandPlanner.Plan(
            OSPlatformKind.Windows, Present("wt"), Env(("WT_SESSION", "1")), PromptFile, null, Defaults)[0];

        Assert.Equal(["new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(4));
        Assert.Equal("Windows Terminal", spec.DisplayName);
    }

    [Fact]
    public void NewTab_Linux_GnomeTerminal_UsesTabFlag_WhenInsideGnome_ViaVteVersion()
    {
        var spec = PlanTab(OSPlatformKind.Linux, Present("gnome-terminal"), Env(("VTE_VERSION", "6003")))[0];

        Assert.Equal("gnome-terminal", spec.FileName);
        Assert.Equal(["--tab", "--", "bash", "-lc"], spec.Arguments.Take(4));
        Assert.Equal("gnome-terminal (new tab)", spec.DisplayName);
    }

    [Fact]
    public void NewTab_Linux_GnomeTerminal_DetectsViaGnomeTerminalScreen()
    {
        var spec = PlanTab(OSPlatformKind.Linux, Present("gnome-terminal"), Env(("GNOME_TERMINAL_SCREEN", "/org/gnome/x")))[0];

        Assert.Equal(["--tab", "--", "bash", "-lc"], spec.Arguments.Take(4));
    }

    [Fact]
    public void NewTab_Linux_GnomeTerminal_FallsBackToWindow_WhenNotDetected()
    {
        var spec = PlanTab(OSPlatformKind.Linux, Present("gnome-terminal"), NoEnv)[0];

        Assert.Equal(["--", "bash", "-lc"], spec.Arguments.Take(3));
        Assert.Equal("gnome-terminal", spec.DisplayName);
    }

    [Fact]
    public void NewTab_Linux_Konsole_UsesNewTabFlag_WhenInsideKonsole()
    {
        var spec = PlanTab(OSPlatformKind.Linux, Present("konsole"), Env(("KONSOLE_VERSION", "220300")))[0];

        Assert.Equal("konsole", spec.FileName);
        Assert.Equal(["--new-tab", "-e", "bash", "-lc"], spec.Arguments.Take(4));
        Assert.Equal("konsole (new tab)", spec.DisplayName);
    }

    [Fact]
    public void NewTab_Linux_Konsole_FallsBackToWindow_WhenNotDetected()
    {
        var spec = PlanTab(OSPlatformKind.Linux, Present("konsole"), NoEnv)[0];

        Assert.Equal(["-e", "bash", "-lc"], spec.Arguments.Take(3));
        Assert.Equal("konsole", spec.DisplayName);
    }

    [Fact]
    public void NewTab_Linux_Wezterm_SpawnsTab_WhenInsideWezterm_AheadOfWindowFallback()
    {
        // WezTerm has no portable `--tab` flag but a scriptable `cli spawn` that opens a tab in the current
        // window (#589). Gated on WEZTERM_PANE; wezterm is in LinuxEmulators, so its window spec follows.
        var specs = PlanTab(OSPlatformKind.Linux, Present("wezterm"), Env(("WEZTERM_PANE", "0")));

        Assert.Equal("WezTerm (new tab)", specs[0].DisplayName);
        Assert.Equal("wezterm", specs[0].FileName);
        Assert.Equal(["cli", "spawn", "--", "bash", "-lc"], specs[0].Arguments.Take(5));
        Assert.Contains(specs, s => s.DisplayName == "wezterm"); // window fallback retained
    }

    [Fact]
    public void NewTab_Linux_Wezterm_FallsBackToWindow_WhenNotInsideWezterm()
    {
        // WEZTERM_PANE unset — no in-session spawn; keep today's window spec (via the `start --` prefix).
        var specs = PlanTab(OSPlatformKind.Linux, Present("wezterm"), NoEnv);

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("new tab", StringComparison.OrdinalIgnoreCase));
        var window = Assert.Single(specs);
        Assert.Equal("wezterm", window.DisplayName);
        Assert.Equal(["start", "--", "bash", "-lc"], window.Arguments.Take(4));
    }

    [Fact]
    public void NewTab_Linux_Kitty_LaunchesTab_ViaKitten_WhenRemoteControlEnabled()
    {
        // kitty opens a tab via `kitten @ launch --type=tab` (#589), gated on KITTY_LISTEN_ON (the same
        // `allow_remote_control` probe as its split) plus the `kitten` binary.
        var specs = PlanTab(OSPlatformKind.Linux, Present("kitten", "kitty"), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")));

        Assert.Equal("kitty (new tab)", specs[0].DisplayName);
        Assert.Equal("kitten", specs[0].FileName);
        Assert.Equal(["@", "launch", "--type=tab", "--cwd=current", "bash", "-lc"], specs[0].Arguments.Take(6));
        Assert.Contains(specs, s => s.DisplayName == "kitty"); // window fallback retained
    }

    [Fact]
    public void NewTab_Linux_Kitty_FallsBackToWindow_WhenNoRemoteControl()
    {
        // kitten present but KITTY_LISTEN_ON unset — the honest gate: no remote control, no tab.
        var specs = PlanTab(OSPlatformKind.Linux, Present("kitten", "kitty"), NoEnv);

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("new tab", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(specs, s => s.DisplayName == "kitty");
    }

    [Fact]
    public void NewTab_Linux_Kitty_FallsBackToWindow_WhenKittenAbsent()
    {
        // KITTY_LISTEN_ON set and `kitty` present, but the `kitten` binary the tab runs through is not.
        var specs = PlanTab(OSPlatformKind.Linux, Present("kitty"), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("new tab", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(specs, s => s.DisplayName == "kitty");
    }

    [Fact]
    public void NewTab_Linux_Zellij_OpensInSessionPane_WhenInsideZellij()
    {
        // Zellij has no window concept and `action new-tab` can't carry a command, so a NewTab opens an
        // in-session new pane (#589). In a Zellij-only session it's the sole candidate.
        var spec = Assert.Single(PlanTab(OSPlatformKind.Linux, Present("zellij"), Env(("ZELLIJ", "0"))));

        Assert.Equal("Zellij (new pane)", spec.DisplayName);
        Assert.Equal("zellij", spec.FileName);
        Assert.Equal(["action", "new-pane", "--", "bash", "-lc"], spec.Arguments.Take(5));
    }

    [Fact]
    public void ZellijOnlySession_AlwaysYieldsACandidate_ForEveryLaunchLocation()
    {
        // AC (#589): a Zellij-only session (no tmux, no GUI emulator on PATH) must never produce an empty
        // candidate list — for a window, a tab, or a split request alike.
        Func<string, string?> zellij = k => k == "ZELLIJ" ? "0" : null;

        foreach (var location in new[] { LaunchLocation.NewWindow, LaunchLocation.NewTab, LaunchLocation.SplitPane })
        {
            var specs = TerminalCommandPlanner.Plan(
                OSPlatformKind.Linux, Present("zellij"), zellij, PromptFile, null,
                new TerminalLauncherOptions { LaunchLocation = location });

            Assert.NotEmpty(specs);
            Assert.All(specs, s => Assert.Equal("zellij", s.FileName));
        }
    }

    [Fact]
    public void NewWindow_Linux_ModernHosts_EmitNoTabRung_JustTheWindow()
    {
        // A NewWindow request must not trip the new in-place tab rungs (#589): the `if (tab && …)` gate is
        // false for NewWindow, so WezTerm/kitty emit only their LinuxEmulators window spec.
        var window = new TerminalLauncherOptions { LaunchLocation = LaunchLocation.NewWindow };

        var wezterm = TerminalCommandPlanner.Plan(
            OSPlatformKind.Linux, Present("wezterm"), Env(("WEZTERM_PANE", "0")), PromptFile, null, window);
        Assert.DoesNotContain(wezterm, s => s.DisplayName.Contains("new tab", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("wezterm", Assert.Single(wezterm).DisplayName);

        var kitty = TerminalCommandPlanner.Plan(
            OSPlatformKind.Linux, Present("kitten", "kitty"), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")), PromptFile, null, window);
        Assert.DoesNotContain(kitty, s => s.DisplayName.Contains("new tab", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("kitty", Assert.Single(kitty).DisplayName);
    }

    [Fact]
    public void NewTab_Linux_ModernHosts_BakeWorkingDirectoryIntoTheCommand()
    {
        // The baked-in `cd … && …` (#252) must ride along on the new WezTerm/kitty/Zellij tab commands too.
        var wezterm = PlanTab(OSPlatformKind.Linux, Present("wezterm"), Env(("WEZTERM_PANE", "0")), "/work/dir")[0];
        Assert.Contains("cd '/work/dir' && 'claude'", wezterm.Arguments[^1]);

        var kitty = PlanTab(OSPlatformKind.Linux, Present("kitten", "kitty"), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")), "/work/dir")[0];
        Assert.Contains("cd '/work/dir' && 'claude'", kitty.Arguments[^1]);

        var zellij = PlanTab(OSPlatformKind.Linux, Present("zellij"), Env(("ZELLIJ", "0")), "/work/dir")[0];
        Assert.Contains("cd '/work/dir' && 'claude'", zellij.Arguments[^1]);
    }

    [Fact]
    public void NewTab_Linux_XTerminalEmulator_StaysWindowOnly_NoPortableTabFlag()
    {
        // Generic alias: even with a tab preference and a detection env present, keep the window form.
        var spec = PlanTab(OSPlatformKind.Linux, Present("x-terminal-emulator"), Env(("VTE_VERSION", "6003")))[0];

        Assert.Equal("x-terminal-emulator", spec.FileName);
        Assert.Equal(["-e", "bash", "-lc"], spec.Arguments.Take(3));
    }

    [Fact]
    public void NewTab_Linux_TerminalEnv_StaysWindowOnly()
    {
        // An explicit $TERMINAL is an arbitrary emulator — window-only regardless of a tab preference.
        var spec = PlanTab(OSPlatformKind.Linux, Present("alacritty"), Env(("TERMINAL", "alacritty"), ("VTE_VERSION", "6003")))[0];

        Assert.Equal("alacritty", spec.FileName);
        Assert.Equal(["-e", "bash", "-lc"], spec.Arguments.Take(3));
    }

    [Fact]
    public void NewTab_Linux_DetectedTabSpec_IsTriedBeforeGenericWindowSpecs()
    {
        // The realistic Debian/Ubuntu case: x-terminal-emulator (an alternatives symlink) is present
        // alongside the gnome-terminal we're actually inside. Its window-only spec must NOT preempt the
        // gnome tab — the launcher stops at the first spec that starts, so the tab spec must be first.
        var specs = PlanTab(
            OSPlatformKind.Linux, Present("x-terminal-emulator", "gnome-terminal"), Env(("VTE_VERSION", "6003")));

        Assert.Equal("gnome-terminal (new tab)", specs[0].DisplayName);
        Assert.Equal(["--tab", "--", "bash", "-lc"], specs[0].Arguments.Take(4));
        Assert.Contains(specs, s => s.FileName == "x-terminal-emulator"); // window spec retained as fallback
    }

    [Fact]
    public void NewTab_Linux_TerminalEnv_DoesNotPreemptDetectedTabSpec()
    {
        // A set $TERMINAL adds a window spec, but a detected-emulator tab spec must still be tried first.
        var specs = PlanTab(
            OSPlatformKind.Linux, Present("konsole"), Env(("TERMINAL", "konsole"), ("KONSOLE_VERSION", "220300")));

        Assert.Equal("konsole (new tab)", specs[0].DisplayName);
        Assert.Equal(["--new-tab", "-e", "bash", "-lc"], specs[0].Arguments.Take(4));
        Assert.Contains(specs, s => s.DisplayName == "konsole" && s.Arguments[0] == "-e"); // window fallback
    }

    [Fact]
    public void NewTab_MacOS_ITerm_OpensTabViaAppleScript_WithTerminalWindowFallback()
    {
        var specs = PlanTab(OSPlatformKind.MacOS, Present("osascript"), Env(("TERM_PROGRAM", "iTerm.app")));

        Assert.Equal(2, specs.Count);
        var iterm = specs[0];
        Assert.Equal("osascript", iterm.FileName);
        Assert.Equal("iTerm2 (new tab)", iterm.DisplayName);
        var script = string.Join("\n", iterm.Arguments);
        Assert.Contains("tell application \"iTerm\"", script);
        Assert.Contains("create tab with default profile", script);
        Assert.Contains("write text", script);
        Assert.Contains("$(cat '/tmp/clickup-todo/agent-prompt.txt')", script); // prompt stays file-indirected

        Assert.Equal("Terminal (osascript)", specs[1].DisplayName); // window fallback retained
    }

    [Fact]
    public void NewTab_MacOS_AppleTerminal_StaysWindowOnly()
    {
        var specs = PlanTab(OSPlatformKind.MacOS, Present("osascript"), Env(("TERM_PROGRAM", "Apple_Terminal")));

        var spec = Assert.Single(specs);
        Assert.Equal("Terminal (osascript)", spec.DisplayName);
        Assert.Contains("do script", spec.Arguments[1]);
    }

    [Fact]
    public void NewTab_MacOS_UnknownHost_StaysWindowOnly()
    {
        var specs = PlanTab(OSPlatformKind.MacOS, Present("osascript"), NoEnv);

        Assert.Equal("Terminal (osascript)", Assert.Single(specs).DisplayName);
    }

    [Fact]
    public void NewTab_BakesWorkingDirectoryIntoTheTabCommand()
    {
        var wt = PlanTab(OSPlatformKind.Windows, Present("wt"), Env(("WT_SESSION", "1")), "C:/work/dir")[0];
        Assert.StartsWith(@"Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop\;", wt.Arguments[^1]); // #534: `;` escaped

        var gnome = PlanTab(OSPlatformKind.Linux, Present("gnome-terminal"), Env(("VTE_VERSION", "1")), "/work/dir")[0];
        Assert.StartsWith("cd '/work/dir' && 'claude'", gnome.Arguments[^1]);

        var konsole = PlanTab(OSPlatformKind.Linux, Present("konsole"), Env(("KONSOLE_VERSION", "1")), "/work/dir")[0];
        Assert.StartsWith("cd '/work/dir' && 'claude'", konsole.Arguments[^1]);

        var iterm = PlanTab(OSPlatformKind.MacOS, Present("osascript"), Env(("TERM_PROGRAM", "iTerm.app")), "/work/dir")[0];
        Assert.Contains("cd '/work/dir' && 'claude'", string.Join("\n", iterm.Arguments));
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
            () => launcher.LaunchAsync(PromptFile, null, Defaults, ct: cts.Token));
        Assert.False(started); // cancelled before any process was started
    }

    [Fact]
    public async Task Launch_ThreadsOneOff_IntoTheBuiltCommand()
    {
        LaunchSpec? captured = null;
        Func<LaunchSpec, bool> start = s => { captured = s; return true; };
        var launcher = Launcher(OSPlatformKind.Linux, Present("konsole"), start);

        await launcher.LaunchAsync(PromptFile, null, Defaults, oneOff: true);

        Assert.NotNull(captured);
        var command = string.Join(" ", captured!.Arguments);
        Assert.Contains("'claude' -p ", command); // one-off flag reached the planned command
        Assert.Contains("read -r _", command);     // and the POSIX keep-alive
    }
}
