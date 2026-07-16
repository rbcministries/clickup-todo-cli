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
        // the command it runs in the new tab.
        var command = PlanCwd(OSPlatformKind.Windows, Present("wt"), "C:/work/dir")[0].Arguments[^1];

        Assert.StartsWith("Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop;", command);
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

    // ── new-tab launch location (#255): tab candidate first, new-window chain as fallback ──────
    //
    // Opt-in via TerminalLaunchLocation.NewTab. The tab spec is gated on both the emulator being
    // present and the env var it exports when we're running inside it; otherwise (or with the default
    // NewWindow) the plan is exactly the existing new-window chain.

    private static readonly TerminalLauncherOptions Tab = new() { LaunchLocation = TerminalLaunchLocation.NewTab };

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return k => map.TryGetValue(k, out var v) ? v : null;
    }

    [Fact]
    public void Windows_NewTab_InsideWindowsTerminal_TargetsCurrentWindowFirst_ThenWindowChain()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Tab, Env(("WT_SESSION", "abc-123")));

        Assert.Equal("Windows Terminal (tab)", specs[0].DisplayName);
        // `-w 0` targets the current window; the payload is the same -NoExit pwsh command as the window path.
        Assert.Equal(["-w", "0", "new-tab", "pwsh", "-NoExit", "-Command"], specs[0].Arguments.Take(6));
        Assert.Contains("Get-Content -Raw", specs[0].Arguments[^1]);
        // The full new-window chain is preserved as the fallback behind the tab candidate.
        Assert.Equal(["Windows Terminal (tab)", "Windows Terminal", "PowerShell (pwsh)"], specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Windows_NewTab_WithoutWtSession_YieldsNoTabCandidate()
    {
        // Not inside Windows Terminal (no WT_SESSION) → just the ordinary new-window chain.
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Tab, NoEnv);

        Assert.DoesNotContain("Windows Terminal (tab)", specs.Select(s => s.DisplayName));
        Assert.Equal(["Windows Terminal", "PowerShell (pwsh)"], specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Windows_NewTab_WithoutWt_YieldsNoTabCandidate()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("pwsh"), Tab, Env(("WT_SESSION", "abc")));

        Assert.DoesNotContain("Windows Terminal (tab)", specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Windows_NewWindowDefault_IgnoresWtSession_NoTabCandidate()
    {
        // The tab candidate is opt-in: NewWindow (default) never produces it even inside Windows Terminal.
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Defaults, Env(("WT_SESSION", "abc")));

        Assert.DoesNotContain("Windows Terminal (tab)", specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Linux_NewTab_InsideGnomeTerminal_UsesTabFlagFirst_ThenWindowChain()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("gnome-terminal"), Tab, Env(("GNOME_TERMINAL_SCREEN", "/org/gnome/x")));

        Assert.Equal("gnome-terminal (tab)", specs[0].DisplayName);
        Assert.Equal(["--tab", "--", "bash", "-lc"], specs[0].Arguments.Take(4));
        Assert.Equal(["gnome-terminal (tab)", "gnome-terminal"], specs.Select(s => s.DisplayName)); // window fallback kept
    }

    [Fact]
    public void Linux_NewTab_GnomeTerminal_DetectedViaVteVersion()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("gnome-terminal"), Tab, Env(("VTE_VERSION", "7600")));

        Assert.Equal("gnome-terminal (tab)", specs[0].DisplayName);
    }

    [Fact]
    public void Linux_NewTab_InsideKonsole_UsesNewTabFlagFirst()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("konsole"), Tab, Env(("KONSOLE_VERSION", "230804")));

        Assert.Equal("konsole (tab)", specs[0].DisplayName);
        Assert.Equal(["--new-tab", "-e", "bash", "-lc"], specs[0].Arguments.Take(4));
        Assert.Equal(["konsole (tab)", "konsole"], specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Linux_NewTab_WithoutHostEnv_YieldsNoTabCandidate()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("gnome-terminal", "konsole"), Tab, NoEnv);

        Assert.DoesNotContain(specs, s => s.DisplayName.EndsWith("(tab)", StringComparison.Ordinal));
        Assert.Equal(["gnome-terminal", "konsole"], specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void Linux_NewWindowDefault_IgnoresHostEnv_NoTabCandidate()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("gnome-terminal"), Defaults, Env(("GNOME_TERMINAL_SCREEN", "/x")));

        Assert.DoesNotContain(specs, s => s.DisplayName.EndsWith("(tab)", StringComparison.Ordinal));
    }

    [Fact]
    public void MacOS_NewTab_InsideITerm_CreatesTabFirst_ThenTerminalAppFallback()
    {
        var specs = Plan(OSPlatformKind.MacOS, Present("osascript"), Tab, Env(("TERM_PROGRAM", "iTerm.app")));

        Assert.Equal("iTerm2 (tab)", specs[0].DisplayName);
        Assert.Equal("osascript", specs[0].FileName);
        var script = string.Join("\n", specs[0].Arguments);
        Assert.Contains("tell application \"iTerm\"", script);
        Assert.Contains("create tab with default profile", script);
        Assert.Contains("$(cat '/tmp/clickup-todo/agent-prompt.txt')", script); // prompt stays file-indirected
        // Terminal.app window remains the fallback behind the iTerm tab candidate.
        Assert.Equal(["iTerm2 (tab)", "Terminal (osascript)"], specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void MacOS_NewTab_InsideTerminalApp_StaysWindowOnly()
    {
        // Terminal.app has no scriptable tab, so NewTab falls back to the window (do script) path only.
        var specs = Plan(OSPlatformKind.MacOS, Present("osascript"), Tab, Env(("TERM_PROGRAM", "Apple_Terminal")));

        Assert.Equal(["Terminal (osascript)"], specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void MacOS_NewTab_WithoutTermProgram_StaysWindowOnly()
    {
        var specs = Plan(OSPlatformKind.MacOS, Present("osascript"), Tab, NoEnv);

        Assert.Equal(["Terminal (osascript)"], specs.Select(s => s.DisplayName));
    }

    [Fact]
    public void NewTab_CarriesWorkingDirectoryIntoTheTabCommand()
    {
        // The cwd is baked into the same command the window path uses, so it rides along on the tab too.
        var wtTab = TerminalCommandPlanner.Plan(
            OSPlatformKind.Windows, Present("wt", "pwsh"), Env(("WT_SESSION", "s")), PromptFile, "C:/work/dir", Tab)[0];
        Assert.StartsWith("Set-Location -LiteralPath 'C:/work/dir' -ErrorAction Stop;", wtTab.Arguments[^1]);

        var gnomeTab = TerminalCommandPlanner.Plan(
            OSPlatformKind.Linux, Present("gnome-terminal"), Env(("VTE_VERSION", "1")), PromptFile, "/work/dir", Tab)[0];
        // gnome-terminal tab args are `--tab -- bash -lc <inner>`, so the command is the 5th arg (index 4).
        Assert.StartsWith("cd '/work/dir' && 'claude'", gnomeTab.Arguments[4]);
    }

    [Fact]
    public void NewTab_KeepsPromptFileIndirected_NeverInlineContent()
    {
        foreach (var (os, exists, env) in new (OSPlatformKind, Func<string, bool>, Func<string, string?>)[]
        {
            (OSPlatformKind.Windows, Present("wt", "pwsh"), Env(("WT_SESSION", "s"))),
            (OSPlatformKind.MacOS, Present("osascript"), Env(("TERM_PROGRAM", "iTerm.app"))),
            (OSPlatformKind.Linux, Present("konsole"), Env(("KONSOLE_VERSION", "1"))),
        })
        {
            var command = string.Join(" ", TerminalCommandPlanner
                .Plan(os, exists, env, PromptFile, null, Tab)[0].Arguments);
            Assert.Contains(PromptFile, command);
            Assert.Matches("Get-Content -Raw|cat ", command);
        }
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
