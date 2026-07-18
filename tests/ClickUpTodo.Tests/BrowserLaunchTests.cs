using ClickUpTodo.Agent;
using ClickUpTodo.Setup;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the cross-platform open-in-browser path (issue #308). The pure
/// <see cref="BrowserLaunchPlanner"/> per-OS candidate logic and the <see cref="SystemBrowserLauncher"/>
/// orchestration loop are fully exercised here without spawning a real process. The actual
/// <c>Process.Start</c> path can't run headlessly and is verified manually.
/// </summary>
public sealed class BrowserLaunchTests
{
    private static readonly Uri Url = new("https://app.clickup.com/t/abc123");

    private static Func<string, bool> Present(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static Func<string, bool> None => _ => false;
    private static Func<string, string?> NoEnv => _ => null;

    private static IReadOnlyList<BrowserCommand> Plan(
        OSPlatformKind os, Func<string, bool>? exists = null, Func<string, string?>? env = null)
        => BrowserLaunchPlanner.Plan(os, exists ?? None, env ?? NoEnv, Url);

    // ── Windows ──────────────────────────────────────────────────────────────

    [Fact]
    public void Windows_ShellExecutesTheUrl()
    {
        var specs = Plan(OSPlatformKind.Windows);

        var cmd = Assert.Single(specs);
        Assert.True(cmd.UseShellExecute);
        Assert.Equal(Url.ToString(), cmd.FileName);
        Assert.Empty(cmd.Arguments);
    }

    // ── macOS ────────────────────────────────────────────────────────────────

    [Fact]
    public void MacOS_PrefersOpen_ThenShellExecuteFallback()
    {
        var specs = Plan(OSPlatformKind.MacOS);

        Assert.Equal(2, specs.Count);
        Assert.Equal("open", specs[0].FileName);
        Assert.False(specs[0].UseShellExecute);
        Assert.Equal([Url.ToString()], specs[0].Arguments);
        Assert.True(specs[1].UseShellExecute);
    }

    // ── Linux ────────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_PrefersXdgOpen()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("xdg-open"));

        var cmd = Assert.Single(specs);
        Assert.Equal("xdg-open", cmd.FileName);
        Assert.False(cmd.UseShellExecute);
        Assert.Equal([Url.ToString()], cmd.Arguments);
    }

    [Fact]
    public void Linux_HonoursBrowserEnvVarFirst()
    {
        Func<string, string?> env = v => v == "BROWSER" ? "firefox" : null;
        var specs = Plan(OSPlatformKind.Linux, Present("firefox", "xdg-open"), env);

        Assert.Equal(["firefox", "xdg-open"], specs.Select(s => s.FileName));
        Assert.Equal([Url.ToString()], specs[0].Arguments);
    }

    [Fact]
    public void Linux_IgnoresBrowserEnvVarWhenNotOnPath()
    {
        Func<string, string?> env = v => v == "BROWSER" ? "firefox" : null;
        var specs = Plan(OSPlatformKind.Linux, Present("xdg-open"), env); // firefox not present

        Assert.Equal(["xdg-open"], specs.Select(s => s.FileName));
    }

    [Fact]
    public void Linux_GioTakesOpenSubcommand()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("gio"));

        var cmd = Assert.Single(specs);
        Assert.Equal("gio", cmd.FileName);
        Assert.Equal(["open", Url.ToString()], cmd.Arguments);
    }

    [Fact]
    public void Linux_FallbackChainInPreferenceOrder()
    {
        var specs = Plan(OSPlatformKind.Linux, Present("gio", "x-www-browser", "www-browser", "sensible-browser"));

        Assert.Equal(["gio", "x-www-browser", "www-browser", "sensible-browser"], specs.Select(s => s.FileName));
    }

    [Fact]
    public void Linux_NoOpenerPresent_YieldsEmpty()
    {
        Assert.Empty(Plan(OSPlatformKind.Linux, None));
    }

    // ── Unknown ──────────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_FallsBackToShellExecute()
    {
        var cmd = Assert.Single(Plan(OSPlatformKind.Unknown));
        Assert.True(cmd.UseShellExecute);
    }

    // ── Hint ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OSPlatformKind.Windows)]
    [InlineData(OSPlatformKind.MacOS)]
    [InlineData(OSPlatformKind.Unknown)]
    public void OpenerHint_NullExceptOnLinux(OSPlatformKind os)
        => Assert.Null(BrowserLaunchPlanner.OpenerHint(os));

    [Fact]
    public void OpenerHint_MentionsXdgOpenOnLinux()
        => Assert.Contains("xdg-open", BrowserLaunchPlanner.OpenerHint(OSPlatformKind.Linux));

    // ── Launcher orchestration ───────────────────────────────────────────────

    [Fact]
    public void Launcher_StartsFirstCandidateThatSucceeds()
    {
        var started = new List<string>();
        var launcher = new SystemBrowserLauncher(
            os: OSPlatformKind.Linux,
            exists: Present("xdg-open", "gio"),
            getEnv: NoEnv,
            start: cmd => { started.Add(cmd.FileName); return true; });

        Assert.True(launcher.TryOpen(Url));
        Assert.Equal(["xdg-open"], started); // stops at the first success
    }

    [Fact]
    public void Launcher_FallsThroughWhenFirstFails()
    {
        var started = new List<string>();
        var launcher = new SystemBrowserLauncher(
            os: OSPlatformKind.Linux,
            exists: Present("xdg-open", "gio"),
            getEnv: NoEnv,
            start: cmd => { started.Add(cmd.FileName); return cmd.FileName == "gio"; });

        Assert.True(launcher.TryOpen(Url));
        Assert.Equal(["xdg-open", "gio"], started);
    }

    [Fact]
    public void Launcher_ReturnsFalseWhenNoCandidateStarts()
    {
        var launcher = new SystemBrowserLauncher(
            os: OSPlatformKind.Linux,
            exists: Present("xdg-open"),
            getEnv: NoEnv,
            start: _ => false);

        Assert.False(launcher.TryOpen(Url));
    }

    [Fact]
    public void Launcher_ReturnsFalseWhenNothingPlanned()
    {
        var launcher = new SystemBrowserLauncher(
            os: OSPlatformKind.Linux,
            exists: None,
            getEnv: NoEnv,
            start: _ => throw new Xunit.Sdk.XunitException("start should not be called when no opener is planned"));

        Assert.False(launcher.TryOpen(Url));
    }
}
