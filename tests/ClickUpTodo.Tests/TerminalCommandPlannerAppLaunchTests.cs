using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the app-launch planner path (#301) — <see cref="TerminalCommandPlanner.PlanAppLaunch"/>
/// and <see cref="TerminalLauncher.LaunchAppAsync"/> — which reuse the agent-dispatch emulator matrix to
/// open <c>clickup-todo --task &lt;id&gt;</c> in a new tab/window. The claude <c>Plan</c> path is pinned
/// by <see cref="TerminalLauncherTests"/>; these assert the app command is built correctly (no
/// prompt-file indirection, no <c>-p</c>, no keep-alive, no working directory) and rides the same matrix.
/// </summary>
public sealed class TerminalCommandPlannerAppLaunchTests
{
    private static readonly AppLaunchCommand Command = new("clickup-todo", ["--task", "86abc"]);
    private static readonly TerminalLauncherOptions Defaults = new();
    private static readonly TerminalLauncherOptions Tab = new() { LaunchLocation = LaunchLocation.NewTab };

    private static Func<string, bool> Present(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static Func<string, string?> NoEnv => _ => null;

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return k => map.TryGetValue(k, out var v) ? v : null;
    }

    private static IReadOnlyList<LaunchSpec> Plan(
        OSPlatformKind os, Func<string, bool> exists, TerminalLauncherOptions? options = null, Func<string, string?>? env = null)
        => TerminalCommandPlanner.PlanAppLaunch(os, exists, env ?? NoEnv, Command, options ?? Defaults);

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
    public void Windows_Pwsh_RunsTheAppCommand_NoPromptFile_NoDashP()
    {
        var command = Plan(OSPlatformKind.Windows, Present("pwsh"))[0].Arguments[^1];

        Assert.Equal("& 'clickup-todo' '--task' '86abc'", command);
        Assert.DoesNotContain("Get-Content", command);
        Assert.DoesNotContain("'-p'", command);
    }

    [Fact]
    public void Windows_NoWorkingDirectory_OmitsSetLocation()
    {
        var command = Plan(OSPlatformKind.Windows, Present("pwsh"))[0].Arguments[^1];

        Assert.DoesNotContain("Set-Location", command);
    }

    [Fact]
    public void Windows_Cmd_EncodesTheAppCommand_DecodingReproducesIt()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("pwsh", "cmd"));
        var encoded = specs.Single(s => s.FileName == "cmd").Arguments[^1];

        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal("& 'clickup-todo' '--task' '86abc'", decoded);
    }

    [Fact]
    public void Windows_NewTab_TargetsCurrentWindow_WhenInsideWt()
    {
        var spec = Plan(OSPlatformKind.Windows, Present("wt"), Tab, Env(("WT_SESSION", "abc")))[0];

        Assert.Equal(["-w", "0", "new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
        Assert.Equal("Windows Terminal (new tab)", spec.DisplayName);
        Assert.Equal("& 'clickup-todo' '--task' '86abc'", spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_NewTab_FallsBackToWindow_WhenNotInsideWt()
    {
        var spec = Plan(OSPlatformKind.Windows, Present("wt"), Tab, NoEnv)[0];

        Assert.Equal("Windows Terminal", spec.DisplayName);
        Assert.DoesNotContain("-w", spec.Arguments);
    }

    // ── macOS ────────────────────────────────────────────────────────────────

    [Fact]
    public void MacOS_Window_RunsAppCommand_NoCatNoKeepAlive()
    {
        var spec = Assert.Single(Plan(OSPlatformKind.MacOS, Present("osascript")));

        Assert.Equal("Terminal (osascript)", spec.DisplayName);
        Assert.Contains("tell application \"Terminal\" to do script", spec.Arguments[1]);
        Assert.Contains("'clickup-todo' '--task' '86abc'", spec.Arguments[1]);
        Assert.DoesNotContain("cat ", spec.Arguments[1]);
        Assert.DoesNotContain("read -r", spec.Arguments[1]);
    }

    [Fact]
    public void MacOS_NewTab_ITerm_OpensTabWithTerminalFallback()
    {
        var specs = Plan(OSPlatformKind.MacOS, Present("osascript"), Tab, Env(("TERM_PROGRAM", "iTerm.app")));

        Assert.Equal(2, specs.Count);
        Assert.Equal("iTerm2 (new tab)", specs[0].DisplayName);
        var script = string.Join("\n", specs[0].Arguments);
        Assert.Contains("create tab with default profile", script);
        Assert.Contains("'clickup-todo' '--task' '86abc'", script);
        Assert.Equal("Terminal (osascript)", specs[1].DisplayName);
    }

    // ── Linux ────────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_GnomeTerminal_RunsAppCommand_NoCatNoCd()
    {
        var spec = Plan(OSPlatformKind.Linux, Present("gnome-terminal"))[0];

        Assert.Equal(["--", "bash", "-lc"], spec.Arguments.Take(3));
        Assert.Equal("'clickup-todo' '--task' '86abc'", spec.Arguments[3]);
        Assert.DoesNotContain("cat ", spec.Arguments[3]);
        Assert.DoesNotContain("cd ", spec.Arguments[3]);
    }

    [Fact]
    public void Linux_ProbesBroadenedEmulatorList_InOrder()
    {
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
    public void Linux_NewTab_GnomeTerminal_UsesTabFlag_WhenDetected()
    {
        var spec = Plan(OSPlatformKind.Linux, Present("gnome-terminal"), Tab, Env(("VTE_VERSION", "6003")))[0];

        Assert.Equal(["--tab", "--", "bash", "-lc"], spec.Arguments.Take(4));
        Assert.Equal("gnome-terminal (new tab)", spec.DisplayName);
        Assert.Equal("'clickup-todo' '--task' '86abc'", spec.Arguments[^1]);
    }

    [Fact]
    public void Linux_Tmux_HeadlessSession_YieldsRunnableNewWindowSpec()
    {
        var spec = Assert.Single(Plan(OSPlatformKind.Linux, Present("tmux"), Defaults, Env(("TMUX", "/tmp/x,1,0"))));

        Assert.Equal(["new-window", "bash", "-lc"], spec.Arguments.Take(3));
        Assert.Equal("'clickup-todo' '--task' '86abc'", spec.Arguments[^1]);
    }

    // ── quoting / edge cases ────────────────────────────────────────────────

    [Fact]
    public void Posix_EscapesSingleQuoteInTaskId()
    {
        var weird = new AppLaunchCommand("clickup-todo", ["--task", "o'brien"]);

        var inner = TerminalCommandPlanner
            .PlanAppLaunch(OSPlatformKind.Linux, Present("konsole"), NoEnv, weird, Defaults)[0].Arguments[3];

        Assert.Contains("o'\\''brien", inner);
    }

    [Fact]
    public void Windows_EscapesSingleQuoteInExecutablePath_ForPowerShell()
    {
        var weird = new AppLaunchCommand("/opt/o'brien/clickup-todo", ["--task", "t"]);

        var command = TerminalCommandPlanner
            .PlanAppLaunch(OSPlatformKind.Windows, Present("pwsh"), NoEnv, weird, Defaults)[0].Arguments[^1];

        Assert.Contains("o''brien", command);
    }

    [Fact]
    public void Unknown_OS_NoCandidates()
        => Assert.Empty(Plan(OSPlatformKind.Unknown, Present("wt", "pwsh", "osascript")));

    [Fact]
    public void PlanAppLaunch_NullCommand_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => TerminalCommandPlanner.PlanAppLaunch(OSPlatformKind.Linux, Present("konsole"), NoEnv, null!, Defaults));

    // ── Launcher orchestration (no real process) ───────────────────────────────

    private static TerminalLauncher Launcher(
        OSPlatformKind os, Func<string, bool> exists, Func<LaunchSpec, bool> start, Func<string, string?>? env = null)
        => new(os: os, exists: exists, getEnv: env ?? NoEnv, fileExists: _ => true, start: start);

    [Fact]
    public async Task LaunchApp_TriesNextCandidate_WhenFirstFailsToStart()
    {
        var started = new List<string>();
        var launcher = Launcher(OSPlatformKind.Windows, Present("wt", "pwsh"),
            s => { started.Add(s.FileName); return s.FileName != "wt"; });

        var result = await launcher.LaunchAppAsync(Command, Defaults);

        Assert.True(result.Success);
        Assert.Equal(["wt", "pwsh"], started);
        Assert.Contains("pwsh", result.LaunchedWith);
    }

    [Fact]
    public async Task LaunchApp_Fails_WhenNoTerminalPresent()
    {
        var result = await Launcher(OSPlatformKind.Linux, Present(), _ => true).LaunchAppAsync(Command, Defaults);

        Assert.False(result.Success);
        Assert.Contains("No terminal", result.Error);
    }

    [Fact]
    public async Task LaunchApp_Fails_WhenEveryCandidateFailsToStart()
    {
        var result = await Launcher(OSPlatformKind.Windows, Present("wt", "pwsh"), _ => false)
            .LaunchAppAsync(Command, Defaults);

        Assert.False(result.Success);
        Assert.Contains("failed to start", result.Error);
    }

    [Fact]
    public async Task LaunchApp_NotesWhenAppNotOnPath_WithoutPollutingTerminalName()
    {
        // konsole present (a terminal starts) but `clickup-todo` absent from PATH.
        var result = await Launcher(OSPlatformKind.Linux, Present("konsole"), _ => true)
            .LaunchAppAsync(Command, Defaults);

        Assert.True(result.Success);
        Assert.Equal("konsole", result.LaunchedWith);
        Assert.Contains("was not found on PATH", result.Note);
        // #591: the Note rides the same app-launch status line as the softened NewTab lead, so it must not
        // hard-code "tab" either — the resolved surface can be a Zellij pane or a window fallback (#589).
        Assert.DoesNotContain("tab", result.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchApp_NoNote_WhenAppIsOnPath()
    {
        var result = await Launcher(OSPlatformKind.Linux, Present("konsole", "clickup-todo"), _ => true)
            .LaunchAppAsync(Command, Defaults);

        Assert.True(result.Success);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task LaunchApp_HonorsCancellation_BeforeStartingAProcess()
    {
        var started = false;
        var launcher = Launcher(OSPlatformKind.Linux, Present("konsole"), _ => { started = true; return true; });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => launcher.LaunchAppAsync(Command, Defaults, cts.Token));
        Assert.False(started);
    }

    [Fact]
    public async Task DefaultInterfaceMember_Throws_ForDoublesThatDontOverrideIt()
    {
        ITerminalLauncher stub = new LaunchAsyncOnlyStub();

        await Assert.ThrowsAsync<NotSupportedException>(() => stub.LaunchAppAsync(Command, Defaults));
    }

    private sealed class LaunchAsyncOnlyStub : ITerminalLauncher
    {
        public Task<LaunchResult> LaunchAsync(
            string promptFilePath, string? workingDir, TerminalLauncherOptions options, bool oneOff = false, CancellationToken ct = default)
            => Task.FromResult(LaunchResult.Ok("stub"));
    }
}
