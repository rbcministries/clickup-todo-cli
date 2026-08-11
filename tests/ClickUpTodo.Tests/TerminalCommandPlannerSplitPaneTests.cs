using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the split-pane planner path (#502/#504) — <see cref="LaunchLocation.SplitPane"/> in
/// <see cref="TerminalCommandPlanner"/>. Each per-host split branch is gated on an in-session env probe
/// plus the executable being present, and the candidate ladder degrades split → tab → window. The planner
/// is pure, so every branch is covered here without a terminal. The claude payload / tab specs themselves
/// are pinned by <c>TerminalLauncherTests</c> / <c>TerminalCommandPlannerAppLaunchTests</c>; these assert
/// the split argv shapes, the detection gates, and the ladder ordering.
/// </summary>
public sealed class TerminalCommandPlannerSplitPaneTests
{
    private const string PromptFile = "/tmp/p.txt";

    private static readonly TerminalLauncherOptions Split = new() { LaunchLocation = LaunchLocation.SplitPane };
    private static readonly TerminalLauncherOptions Tab = new() { LaunchLocation = LaunchLocation.NewTab };
    private static readonly TerminalLauncherOptions Window = new() { LaunchLocation = LaunchLocation.NewWindow };

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
        OSPlatformKind os,
        Func<string, bool> exists,
        TerminalLauncherOptions options,
        Func<string, string?>? env = null,
        string? cwd = null,
        bool oneOff = false)
        => TerminalCommandPlanner.Plan(os, exists, env ?? NoEnv, PromptFile, cwd, options, oneOff);

    private static IReadOnlyList<string> Names(IReadOnlyList<LaunchSpec> specs) => specs.Select(s => s.DisplayName).ToList();

    // ── Windows Terminal ───────────────────────────────────────────────────────

    [Fact]
    public void Windows_Split_InsideWt_EmitsSplitSpec_First()
    {
        var spec = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Split, Env(("WT_SESSION", "1")))[0];

        Assert.Equal("wt", spec.FileName);
        Assert.Equal("Windows Terminal (split pane)", spec.DisplayName);
        Assert.Equal(["-w", "0", "sp", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
        Assert.Contains("claude", spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_Split_LaddersDown_SplitThenTabThenWindows()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh", "powershell"), Split, Env(("WT_SESSION", "1")));

        Assert.Equal(
            ["Windows Terminal (split pane)", "Windows Terminal (new tab)", "PowerShell (pwsh)", "Windows PowerShell"],
            Names(specs));
    }

    [Fact]
    public void Windows_Split_NotInsideWt_EmitsNoSplitSpec()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Split, NoEnv);

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Windows Terminal", "PowerShell (pwsh)"], Names(specs));
    }

    [Fact]
    public void Windows_Split_WtAbsent_EmitsNoSplitSpec()
    {
        // WT_SESSION set (a stale/inherited value) but `wt` not on PATH — the exe gate suppresses the split.
        var specs = Plan(OSPlatformKind.Windows, Present("pwsh"), Split, Env(("WT_SESSION", "1")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Windows_Split_CarriesTheWtProfile_WhenSet()
    {
        // The split spec reuses WtArgs, so a matched #462 profile is passed as `-p <profile>` exactly as
        // on the tab/window WT specs.
        var withProfile = new TerminalLauncherOptions { LaunchLocation = LaunchLocation.SplitPane, WindowsTerminalProfile = "Ubuntu" };
        var spec = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), withProfile, Env(("WT_SESSION", "1")))[0];

        Assert.Equal(["-w", "0", "sp", "-p", "Ubuntu", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(8));
    }

    [Fact]
    public void Windows_Split_EscapesTheWtDelimiter_InTheWorkingDirPrefix()
    {
        // The Set-Location prefix contains a `;`, which WT would treat as a subcommand delimiter — WtArgs
        // must escape it to `\;` on the split spec exactly as on the tab spec (#534).
        var spec = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Split, Env(("WT_SESSION", "1")), cwd: "C:\\work")[0];

        Assert.Contains("\\;", spec.Arguments[^1]);
    }

    // ── tmux (Linux) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_Split_InsideTmux_EmitsSplitSpec_ThenTmuxWindowRung()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("tmux"), Split, Env(("TMUX", "/tmp/x,1,0")));

        Assert.Equal(["tmux (split pane)", "tmux (new window)"], Names(specs));
        Assert.Equal(["split-window", "-h", "bash", "-lc"], specs[0].Arguments.Take(4));
        Assert.Contains("claude", specs[0].Arguments[^1]);
    }

    [Fact]
    public void Linux_Split_NotInsideTmux_EmitsNoSplitSpec()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("tmux"), Split, NoEnv);

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    // ── WezTerm (Linux) ──────────────────────────────────────────────────────────

    [Fact]
    public void Linux_Split_InsideWezterm_EmitsSplitSpec_AheadOfItsWindowFallback()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("wezterm"), Split, Env(("WEZTERM_PANE", "0")));

        Assert.Equal("WezTerm (split pane)", specs[0].DisplayName);
        Assert.Equal("wezterm", specs[0].FileName);
        Assert.Equal(["cli", "split-pane", "--right", "--", "bash", "-lc"], specs[0].Arguments.Take(6));
        // wezterm is in LinuxEmulators, so its window spec follows as the fallback rung.
        Assert.Contains("wezterm", Names(specs).Skip(1));
    }

    [Fact]
    public void Linux_Split_WeztermAbsent_EmitsNoSplitSpec()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("xterm"), Split, Env(("WEZTERM_PANE", "0")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    // ── kitty (Linux) ────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_Split_InsideKitty_EmitsSplitSpec_ViaKittenBinary()
    {
        var spec = Plan(OSPlatformKind.Linux, Present("kitten", "kitty"), Split, Env(("KITTY_LISTEN_ON", "unix:/tmp/k")))[0];

        Assert.Equal("kitten", spec.FileName);
        Assert.Equal("kitty (split pane)", spec.DisplayName);
        Assert.Equal(["@", "launch", "--location=vsplit", "--cwd=current", "bash", "-lc"], spec.Arguments.Take(6));
    }

    [Fact]
    public void Linux_Split_KittyWithoutRemoteControl_EmitsNoSplitSpec()
    {
        // kitten present but KITTY_LISTEN_ON unset — the honest gate: no `allow_remote_control`, no split.
        var specs = Plan(OSPlatformKind.Linux, Present("kitten", "kitty"), Split, NoEnv);

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Linux_Split_KittenBinaryAbsent_EmitsNoSplitSpec()
    {
        // KITTY_LISTEN_ON set and `kitty` present, but the `kitten` binary the split runs through is not.
        var specs = Plan(OSPlatformKind.Linux, Present("kitty"), Split, Env(("KITTY_LISTEN_ON", "unix:/tmp/k")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    // ── Zellij (Linux) ───────────────────────────────────────────────────────────

    [Fact]
    public void Linux_Split_InsideZellij_EmitsSplitSpec()
    {
        var spec = Assert.Single(Plan(OSPlatformKind.Linux, Present("zellij"), Split, Env(("ZELLIJ", "0"))));

        Assert.Equal("zellij", spec.FileName);
        Assert.Equal("Zellij (split pane)", spec.DisplayName);
        Assert.Equal(["action", "new-pane", "-d", "right", "--", "bash", "-lc"], spec.Arguments.Take(7));
    }

    [Fact]
    public void Linux_Split_ZellijAbsent_EmitsNoSplitSpec()
    {
        // ZELLIJ set (a stale/inherited value) but the `zellij` binary is not on PATH — the exe gate wins.
        var specs = Plan(OSPlatformKind.Linux, Present("xterm"), Split, Env(("ZELLIJ", "0")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Linux_Split_TmuxAbsent_EmitsNoSplitSpec()
    {
        // TMUX set but `tmux` not on PATH — no split spec, and no tmux window/tab rung either.
        var specs = Plan(OSPlatformKind.Linux, Present("xterm"), Split, Env(("TMUX", "1")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Linux_Split_OrdersSplitSpecs_PerHostTable()
    {
        // Nested/multiple in-place contexts (all env vars set): split specs come in the #504 table order.
        var specs = Plan(
            OSPlatformKind.Linux,
            Present("tmux", "wezterm", "kitten", "kitty", "zellij"),
            Split,
            Env(("TMUX", "1"), ("WEZTERM_PANE", "0"), ("KITTY_LISTEN_ON", "unix:/tmp/k"), ("ZELLIJ", "0")));

        Assert.Equal(
            ["tmux (split pane)", "WezTerm (split pane)", "kitty (split pane)", "Zellij (split pane)"],
            Names(specs).Where(n => n.Contains("split pane")).ToList());
    }

    // ── iTerm2 (macOS) ───────────────────────────────────────────────────────────

    [Fact]
    public void MacOS_Split_InsideIterm_EmitsSplitSpec_ThenTabThenWindow()
    {
        var specs = Plan(OSPlatformKind.MacOS, Present("osascript"), Split, Env(("TERM_PROGRAM", "iTerm.app")));

        Assert.Equal(["iTerm2 (split pane)", "iTerm2 (new tab)", "Terminal (osascript)"], Names(specs));
        Assert.Equal("osascript", specs[0].FileName);
        var script = string.Join("\n", specs[0].Arguments);
        Assert.Contains("split vertically with default profile", script);
        Assert.Contains("tell newSession to write text", script);
        Assert.Contains("claude", script);
    }

    [Fact]
    public void MacOS_Split_NotInsideIterm_EmitsNoSplitSpec()
    {
        var specs = Plan(OSPlatformKind.MacOS, Present("osascript"), Split, NoEnv);

        Assert.Equal(["Terminal (osascript)"], Names(specs));
    }

    // ── Degradation & no-regression ──────────────────────────────────────────────

    [Fact]
    public void Split_OnPaneIncapableHost_ProducesExactlyTodaysTabSpecs()
    {
        // gnome-terminal has a tab but no scriptable split — a SplitPane request must yield exactly what a
        // NewTab request yields (the split rung is simply absent, the ladder falls through to the tab).
        var exists = Present("gnome-terminal");
        var env = Env(("VTE_VERSION", "6003"));

        var split = Plan(OSPlatformKind.Linux, exists, Split, env);
        var tab = Plan(OSPlatformKind.Linux, exists, Tab, env);

        Assert.Equal(Names(tab), Names(split));
        Assert.Equal(tab.Select(s => s.Arguments), split.Select(s => s.Arguments));
    }

    [Fact]
    public void NewTab_Request_EmitsNoSplitSpec_EvenInsideASplitCapableHost()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("wezterm"), Tab, Env(("WEZTERM_PANE", "0")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewWindow_Request_EmitsNoSplitSpec_InsideASplitCapableHost()
    {
        var specs = Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Window, Env(("WT_SESSION", "1")));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Windows Terminal", "PowerShell (pwsh)"], Names(specs));
    }

    [Fact]
    public void Split_OneOff_EmitsNoSplitSpec()
    {
        // A one-off `claude -p` run has no terminal, so an in-place location is meaningless — no split,
        // no tab, just the window fallback (a tmux new-window here).
        var specs = Plan(OSPlatformKind.Linux, Present("tmux"), Split, Env(("TMUX", "1")), oneOff: true);

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("split", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["tmux (new window)"], Names(specs));
    }

    // ── The app-launch entry point splits too (so #504's AppHostLaunch can pass SplitPane) ──

    [Fact]
    public void AppLaunch_Split_InsideWt_EmitsSplitSpec_WithTheAppCommand()
    {
        var command = new AppLaunchCommand("clickup-todo", ["--task", "86abc"]);

        var spec = TerminalCommandPlanner.PlanAppLaunch(
            OSPlatformKind.Windows, Present("wt", "pwsh"), Env(("WT_SESSION", "1")), command, Split)[0];

        Assert.Equal("Windows Terminal (split pane)", spec.DisplayName);
        Assert.Equal(["-w", "0", "sp", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
        Assert.Equal("& 'clickup-todo' '--task' '86abc'", spec.Arguments[^1]);
    }
}
