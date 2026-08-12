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

    // A split request with explicit geometry / focus (#505). Defaults match `Split` above, so an
    // all-default SplitWith() is the pre-#505 minimal split.
    private static TerminalLauncherOptions SplitWith(
        SplitDirection direction = SplitDirection.Auto,
        int? sizePercent = null,
        SplitFocus focus = SplitFocus.FollowPane)
        => new()
        {
            LaunchLocation = LaunchLocation.SplitPane,
            SplitDirection = direction,
            SplitSizePercent = sizePercent,
            SplitFocus = focus,
        };

    private static LaunchSpec SplitSpec(IReadOnlyList<LaunchSpec> specs) =>
        specs.Single(s => s.DisplayName.Contains("split pane", StringComparison.Ordinal));

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

    // ── Geometry: direction (#505) ───────────────────────────────────────────────

    [Fact]
    public void Windows_Split_Beside_EmitsVerticalDivider()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), SplitWith(SplitDirection.Beside), Env(("WT_SESSION", "1"))));

        Assert.Equal(["-w", "0", "sp", "-V", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(7));
    }

    [Fact]
    public void Windows_Split_Below_EmitsHorizontalDivider()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), SplitWith(SplitDirection.Below), Env(("WT_SESSION", "1"))));

        Assert.Equal(["-w", "0", "sp", "-H", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(7));
    }

    [Fact]
    public void Windows_Split_Auto_OmitsDirectionFlag_MatchingB()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), SplitWith(SplitDirection.Auto), Env(("WT_SESSION", "1"))));

        Assert.DoesNotContain("-V", spec.Arguments);
        Assert.DoesNotContain("-H", spec.Arguments);
        Assert.Equal(["-w", "0", "sp", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
    }

    [Fact]
    public void Linux_Split_Below_MapsPerHost()
    {
        LaunchSpec Split1(string exe, string envKey) =>
            SplitSpec(Plan(OSPlatformKind.Linux, Present(exe), SplitWith(SplitDirection.Below), Env((envKey, "1"))));

        Assert.Equal("-v", Split1("tmux", "TMUX").Arguments[1]);
        Assert.Contains("--bottom", Split1("wezterm", "WEZTERM_PANE").Arguments);
        Assert.Contains("--location=hsplit", Plan(OSPlatformKind.Linux, Present("kitten", "kitty"), SplitWith(SplitDirection.Below), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")))[0].Arguments);
        Assert.Equal(["action", "new-pane", "-d", "down", "--", "bash", "-lc"], Split1("zellij", "ZELLIJ").Arguments.Take(7));
    }

    [Fact]
    public void MacOS_Split_Below_UsesHorizontalSplitVerb()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.MacOS, Present("osascript"), SplitWith(SplitDirection.Below), Env(("TERM_PROGRAM", "iTerm.app"))));

        var script = string.Join("\n", spec.Arguments);
        Assert.Contains("split horizontally with default profile", script);
        Assert.DoesNotContain("split vertically", script);
    }

    // ── Geometry: size (#505) — best-effort, only where the host takes one ────────

    [Fact]
    public void Windows_Split_Size_MapsToParentFraction()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), SplitWith(sizePercent: 40), Env(("WT_SESSION", "1"))));

        // -s takes a 0–1 fraction of the parent; 40% → 0.4, invariant formatting.
        Assert.Equal(["-w", "0", "sp", "-s", "0.4", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(8));
    }

    [Fact]
    public void Windows_Split_DirectionAndSize_Compose()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), SplitWith(SplitDirection.Beside, 30), Env(("WT_SESSION", "1"))));

        Assert.Equal(["-w", "0", "sp", "-V", "-s", "0.3", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(9));
    }

    [Fact]
    public void Linux_Split_Size_MapsPerHost()
    {
        var tmux = SplitSpec(Plan(OSPlatformKind.Linux, Present("tmux"), SplitWith(sizePercent: 40), Env(("TMUX", "1"))));
        Assert.Equal(["split-window", "-h", "-l", "40%", "bash", "-lc"], tmux.Arguments.Take(6));

        var wez = SplitSpec(Plan(OSPlatformKind.Linux, Present("wezterm"), SplitWith(sizePercent: 40), Env(("WEZTERM_PANE", "0"))));
        Assert.Equal(["cli", "split-pane", "--right", "--percent", "40", "--", "bash", "-lc"], wez.Arguments.Take(8));
    }

    [Fact]
    public void Linux_Split_Size_IgnoredWhereHostSplitsEvenly()
    {
        // kitty and Zellij take no size argument — SplitSizePercent is silently dropped (documented).
        var kitty = Plan(OSPlatformKind.Linux, Present("kitten", "kitty"), SplitWith(sizePercent: 40), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")))[0];
        Assert.DoesNotContain(kitty.Arguments, a => a.Contains("40", StringComparison.Ordinal));

        var zellij = SplitSpec(Plan(OSPlatformKind.Linux, Present("zellij"), SplitWith(sizePercent: 40), Env(("ZELLIJ", "0"))));
        Assert.Equal(["action", "new-pane", "-d", "right", "--", "bash", "-lc"], zellij.Arguments.Take(7));
    }

    // ── Focus policy: best-effort stay-put (#505) ────────────────────────────────

    [Fact]
    public void Windows_Split_StayPut_ChainsMoveFocusPrevious_WithLiteralSemicolon()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), SplitWith(focus: SplitFocus.StayPut), Env(("WT_SESSION", "1"))));

        // The retention subcommand is appended after the command; the `;` separator stays literal
        // (unescaped) — WtArgs escapes `;` inside the payload, so a raw `;` proves it wasn't run through.
        Assert.Equal([";", "mf", "previous"], spec.Arguments.TakeLast(3));
        Assert.Contains(";", spec.Arguments);
    }

    [Fact]
    public void Windows_Split_StayPut_KeepsRetentionLiteral_EvenWithWorkingDirSemicolon()
    {
        // The Set-Location prefix contains a `;` that WtArgs escapes to `\;` in the payload; the trailing
        // focus separator must remain a bare `;` so WT still parses `mf previous` as its own subcommand.
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), SplitWith(focus: SplitFocus.StayPut), Env(("WT_SESSION", "1")), cwd: "C:\\work"));

        Assert.Equal([";", "mf", "previous"], spec.Arguments.TakeLast(3));
        // The payload arg still carries its escaped `\;` — exactly one bare `;` (the separator) remains.
        Assert.Single(spec.Arguments, a => a == ";");
    }

    [Fact]
    public void Linux_Split_StayPut_MapsOnSupportedHostsOnly()
    {
        // tmux `-d` and kitty `--dont-take-focus` are the supported stay-put tokens.
        var tmux = SplitSpec(Plan(OSPlatformKind.Linux, Present("tmux"), SplitWith(focus: SplitFocus.StayPut), Env(("TMUX", "1"))));
        Assert.Equal(["split-window", "-h", "-d", "bash", "-lc"], tmux.Arguments.Take(5));

        var kitty = Plan(OSPlatformKind.Linux, Present("kitten", "kitty"), SplitWith(focus: SplitFocus.StayPut), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")))[0];
        Assert.Equal(["@", "launch", "--location=vsplit", "--dont-take-focus", "--cwd=current", "bash", "-lc"], kitty.Arguments.Take(7));

        // WezTerm and Zellij have no stay-put flag — the argv is unchanged from FollowPane (unsupported).
        var wez = SplitSpec(Plan(OSPlatformKind.Linux, Present("wezterm"), SplitWith(focus: SplitFocus.StayPut), Env(("WEZTERM_PANE", "0"))));
        Assert.Equal(["cli", "split-pane", "--right", "--", "bash", "-lc"], wez.Arguments.Take(6));
        var zellij = SplitSpec(Plan(OSPlatformKind.Linux, Present("zellij"), SplitWith(focus: SplitFocus.StayPut), Env(("ZELLIJ", "0"))));
        Assert.Equal(["action", "new-pane", "-d", "right", "--", "bash", "-lc"], zellij.Arguments.Take(7));
    }

    [Fact]
    public void AllDefaultGeometryAndFocus_IsByteIdenticalToB()
    {
        // A SplitWith() with every default must equal the pre-#505 minimal split (`Split`) on every host,
        // so #505 is purely additive — the geometry/focus only appear when explicitly requested.
        void SameAsB(OSPlatformKind os, Func<string, bool> exists, Func<string, string?> env)
        {
            var b = Plan(os, exists, Split, env);
            var c = Plan(os, exists, SplitWith(), env);
            Assert.Equal(b.Select(s => s.Arguments), c.Select(s => s.Arguments));
        }

        SameAsB(OSPlatformKind.Windows, Present("wt", "pwsh"), Env(("WT_SESSION", "1")));
        SameAsB(OSPlatformKind.Linux, Present("tmux"), Env(("TMUX", "1")));
        SameAsB(OSPlatformKind.Linux, Present("wezterm"), Env(("WEZTERM_PANE", "0")));
        SameAsB(OSPlatformKind.Linux, Present("kitten", "kitty"), Env(("KITTY_LISTEN_ON", "unix:/tmp/k")));
        SameAsB(OSPlatformKind.Linux, Present("zellij"), Env(("ZELLIJ", "0")));
        SameAsB(OSPlatformKind.MacOS, Present("osascript"), Env(("TERM_PROGRAM", "iTerm.app")));
    }

    // ── Dispatch into a pane: #461 working directory, never --duplicate (#515, slice J) ──────────

    [Fact]
    public void Linux_Split_BakesTheWorkingDirectoryIntoThePayload()
    {
        // #461 gives dispatch a real project directory; the split pane must land there. The mechanism is
        // the command-prefix `cd '<dir>' &&` (WT/tmux/… hand off to a shell that ignores the launcher cwd),
        // exactly as the tab/window paths — the split reuses the same payload, so the dir rides along.
        var spec = SplitSpec(Plan(OSPlatformKind.Linux, Present("tmux"), Split, Env(("TMUX", "1")), cwd: "/proj/repo"));

        Assert.Contains("cd '/proj/repo' &&", spec.Arguments[^1]);
    }

    [Fact]
    public void Windows_Split_BakesTheWorkingDirectoryIntoThePayload()
    {
        var spec = SplitSpec(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Split, Env(("WT_SESSION", "1")), cwd: "C:\\work"));

        // The Set-Location prefix lands the session in the dir (its trailing `;` is escaped to `\;` by
        // WtArgs, #534; the path content is untouched).
        Assert.Contains("Set-Location -LiteralPath 'C:\\work'", spec.Arguments[^1]);
    }

    [Fact]
    public void Split_NeverUsesDuplicate_OnAnyHost()
    {
        // A dispatch pane must carry an explicit directory/profile, never `wt --duplicate`/`-D` (or any
        // host's duplicate), which would inherit *our* pane's profile and directory and defeat #461.
        static void NoDuplicate(IReadOnlyList<LaunchSpec> specs)
        {
            foreach (var s in specs)
            {
                Assert.DoesNotContain("--duplicate", s.Arguments);
                Assert.DoesNotContain("-D", s.Arguments);
            }
        }

        NoDuplicate(Plan(OSPlatformKind.Windows, Present("wt", "pwsh"), Split, Env(("WT_SESSION", "1")), cwd: "C:\\work"));
        NoDuplicate(Plan(OSPlatformKind.Linux, Present("tmux"), Split, Env(("TMUX", "1")), cwd: "/proj/repo"));
        NoDuplicate(Plan(OSPlatformKind.Linux, Present("wezterm"), Split, Env(("WEZTERM_PANE", "0")), cwd: "/proj/repo"));
        NoDuplicate(Plan(OSPlatformKind.Linux, Present("kitten", "kitty"), Split, Env(("KITTY_LISTEN_ON", "unix:/tmp/k")), cwd: "/proj/repo"));
        NoDuplicate(Plan(OSPlatformKind.Linux, Present("zellij"), Split, Env(("ZELLIJ", "0")), cwd: "/proj/repo"));
        NoDuplicate(Plan(OSPlatformKind.MacOS, Present("osascript"), Split, Env(("TERM_PROGRAM", "iTerm.app")), cwd: "/proj/repo"));
    }

    // ── Dispatch into a pane: pane lifetime persists across platforms (#515, slice J) ────────────

    [Fact]
    public void Linux_Split_PersistsThePaneAfterTheSessionEnds()
    {
        // The Linux split hosts run the command via `bash -lc <cmd>`, whose shell exits when the session
        // ends — closing the pane and relayouting the survivors. An interactive dispatch pane keeps it
        // open (matching WT `-NoExit` / iTerm2's session shell), so the pane behaves the same everywhere.
        foreach (var (exe, key) in new[] { ("tmux", "TMUX"), ("wezterm", "WEZTERM_PANE"), ("zellij", "ZELLIJ") })
        {
            var spec = SplitSpec(Plan(OSPlatformKind.Linux, Present(exe), Split, Env((key, "1"))));
            Assert.Contains("session ended", spec.Arguments[^1]);
        }

        var kitty = SplitSpec(Plan(OSPlatformKind.Linux, Present("kitten", "kitty"), Split, Env(("KITTY_LISTEN_ON", "unix:/tmp/k"))));
        Assert.Contains("session ended", kitty.Arguments[^1]);
    }

    [Fact]
    public void Linux_Split_DegradationRungs_DoNotPersist()
    {
        // Only the actual split payload is suffixed with the keep-alive; the tmux new-window degradation
        // rung keeps the plain command, so a SplitPane request that falls through to a tab/window stays
        // byte-identical to a NewTab one (the invariant Split_OnPaneIncapableHost also pins).
        var specs = Plan(OSPlatformKind.Linux, Present("tmux"), Split, Env(("TMUX", "1")));

        var window = specs.Single(s => s.DisplayName == "tmux (new window)");
        Assert.DoesNotContain("session ended", window.Arguments[^1]);
    }

    [Fact]
    public void AppLaunch_Split_DoesNotPersistThePane()
    {
        // An app-host split opens a long-running TUI that owns its pane; closing on exit is its natural
        // lifetime (mirroring PosixAppCommand), so no keep-alive is appended for an app launch.
        var command = new AppLaunchCommand("clickup-todo", ["--task", "86abc"]);

        var spec = SplitSpec(TerminalCommandPlanner.PlanAppLaunch(
            OSPlatformKind.Linux, Present("tmux"), Env(("TMUX", "1")), command, Split));

        Assert.DoesNotContain("session ended", spec.Arguments[^1]);
    }
}
