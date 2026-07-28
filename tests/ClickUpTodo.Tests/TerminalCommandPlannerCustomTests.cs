using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the user-configured custom terminal launch command (#385): a template that, when
/// set and its executable is present, is emitted as the first launch candidate on every platform,
/// ahead of the auto-detected chain, for both the agent-dispatch (<see cref="TerminalCommandPlanner.Plan"/>)
/// and app-launch (<see cref="TerminalCommandPlanner.PlanAppLaunch"/>) paths. An unset or unavailable
/// command is a strict no-op, so the existing chain is unchanged (pinned by the other planner suites).
/// </summary>
public sealed class TerminalCommandPlannerCustomTests
{
    private const string PromptFile = "/tmp/prompt.txt";
    private static readonly AppLaunchCommand AppCommand = new("clickup-todo", ["--task", "86abc"]);

    private static Func<string, bool> Present(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static Func<string, string?> NoEnv => _ => null;

    private static TerminalLauncherOptions Custom(params string[] template)
        => new() { CustomTerminalCommand = template };

    // Dispatch (claude) path.
    private static IReadOnlyList<LaunchSpec> PlanDispatch(OSPlatformKind os, Func<string, bool> exists, TerminalLauncherOptions options)
        => TerminalCommandPlanner.Plan(os, exists, NoEnv, PromptFile, workingDir: null, options);

    // App-launch (clickup-todo --task) path.
    private static IReadOnlyList<LaunchSpec> PlanApp(OSPlatformKind os, Func<string, bool> exists, TerminalLauncherOptions options)
        => TerminalCommandPlanner.PlanAppLaunch(os, exists, NoEnv, AppCommand, options);

    // ── Linux ────────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_CustomCommand_IsFirstCandidate_WithPlaceholderExpandedToBashLc()
    {
        var specs = PlanDispatch(OSPlatformKind.Linux, Present("alacritty", "gnome-terminal"), Custom("alacritty", "-e", "{}"));

        var first = specs[0];
        Assert.Equal("alacritty", first.FileName);
        Assert.Equal("alacritty (configured)", first.DisplayName);
        Assert.Equal(["-e", "bash", "-lc"], first.Arguments.Take(3));
        Assert.Contains("$(cat", first.Arguments[3]); // the dispatch inner (reads the prompt file)

        // The auto-detected chain still follows, untouched.
        Assert.Contains(specs.Skip(1), s => s.FileName == "gnome-terminal");
    }

    [Fact]
    public void Linux_CustomCommand_WithoutPlaceholder_AppendsBashLc()
    {
        var first = PlanApp(OSPlatformKind.Linux, Present("kitty"), Custom("kitty"))[0];

        Assert.Equal("kitty", first.FileName);
        Assert.Equal(["bash", "-lc"], first.Arguments.Take(2));
        Assert.Equal("'clickup-todo' '--task' '86abc'", first.Arguments[^1]);
    }

    [Fact]
    public void Linux_CustomCommand_SkippedWhenExecutableNotOnPath()
    {
        var specs = PlanApp(OSPlatformKind.Linux, Present("gnome-terminal"), Custom("ghostty", "-e", "{}"));

        // ghostty absent ⇒ no custom candidate; the normal chain leads.
        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("configured"));
        Assert.Equal("gnome-terminal", specs[0].FileName);
    }

    [Fact]
    public void Linux_CustomCommand_IsOnlyCandidate_WhenNothingElsePresent()
    {
        var spec = Assert.Single(PlanApp(OSPlatformKind.Linux, Present("ghostty"), Custom("ghostty", "-e", "{}")));

        Assert.Equal("ghostty", spec.FileName);
        Assert.Equal(["-e", "bash", "-lc", "'clickup-todo' '--task' '86abc'"], spec.Arguments);
    }

    // ── macOS ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MacOS_CustomCommand_IsFirst_ThenTerminalAppFallback()
    {
        var specs = PlanApp(OSPlatformKind.MacOS, Present("kitty", "osascript"), Custom("kitty", "{}"));

        Assert.Equal("kitty", specs[0].FileName);
        Assert.Equal("kitty (configured)", specs[0].DisplayName);
        Assert.Equal(["bash", "-lc", "'clickup-todo' '--task' '86abc'"], specs[0].Arguments);
        Assert.Equal("Terminal (osascript)", specs[1].DisplayName);
    }

    [Fact]
    public void MacOS_CustomCommand_EmittedEvenWhenOsascriptAbsent()
    {
        var spec = Assert.Single(PlanApp(OSPlatformKind.MacOS, Present("kitty"), Custom("kitty", "{}")));

        Assert.Equal("kitty", spec.FileName);
        Assert.Equal("kitty (configured)", spec.DisplayName);
    }

    // ── Windows ────────────────────────────────────────────────────────────────

    [Fact]
    public void Windows_CustomCommand_IsFirst_WithPlaceholderExpandedToPwshHost()
    {
        var specs = PlanApp(OSPlatformKind.Windows, Present("wt", "pwsh"), Custom("wt", "-w", "0", "new-tab", "{}"));

        var first = specs[0];
        Assert.Equal("wt", first.FileName);
        Assert.Equal("wt (configured)", first.DisplayName);
        Assert.Equal(["-w", "0", "new-tab", "pwsh", "-NoExit", "-Command"], first.Arguments.Take(6));
        Assert.Equal("& 'clickup-todo' '--task' '86abc'", first.Arguments[^1]);

        // The native Windows chain still follows.
        Assert.Contains(specs.Skip(1), s => s.DisplayName == "PowerShell (pwsh)");
    }

    [Fact]
    public void Windows_CustomCommand_UsesPowerShellHost_WhenPwshAbsent()
    {
        var first = PlanApp(OSPlatformKind.Windows, Present("myterm", "powershell"), Custom("myterm", "{}"))[0];

        Assert.Equal("myterm", first.FileName);
        Assert.Equal(["powershell", "-NoExit", "-Command", "& 'clickup-todo' '--task' '86abc'"], first.Arguments);
    }

    [Fact]
    public void Windows_CustomCommand_SkippedWhenNoPowerShellHostPresent()
    {
        // Neither pwsh nor powershell ⇒ the pwsh payload can't run, so no custom candidate is emitted.
        var specs = PlanApp(OSPlatformKind.Windows, Present("myterm", "cmd"), Custom("myterm", "{}"));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("configured"));
    }

    // ── Cross-cutting ──────────────────────────────────────────────────────────

    [Fact]
    public void NoCustomCommand_LeavesChainUnchanged()
    {
        var withOut = PlanApp(OSPlatformKind.Linux, Present("gnome-terminal", "konsole"), new TerminalLauncherOptions());

        Assert.Equal(["gnome-terminal", "konsole"], withOut.Select(s => s.FileName));
    }

    [Fact]
    public void SecondPlaceholder_IsLiteral_NotASecondSplice()
    {
        // A malformed `... {} {}` splices the host invocation only once; the stray `{}` stays literal.
        var first = PlanApp(OSPlatformKind.Linux, Present("myterm"), Custom("myterm", "-e", "{}", "{}"))[0];

        Assert.Equal(["-e", "bash", "-lc", "'clickup-todo' '--task' '86abc'", "{}"], first.Arguments);
    }

    [Fact]
    public void PlaceholderAttachedToToken_IsNotAPlaceholder()
    {
        // Only a token exactly equal to `{}` is the splice point; `{}foo` is a literal, so the host
        // invocation is appended (the no-placeholder path).
        var first = PlanApp(OSPlatformKind.Linux, Present("myterm"), Custom("myterm", "{}foo"))[0];

        Assert.Equal(["{}foo", "bash", "-lc", "'clickup-todo' '--task' '86abc'"], first.Arguments);
    }

    [Fact]
    public void PlaceholderAsExecutable_IsSkipped()
    {
        // A malformed template whose first token is the placeholder has no real executable.
        var specs = PlanApp(OSPlatformKind.Linux, Present("gnome-terminal"), Custom("{}"));

        Assert.DoesNotContain(specs, s => s.DisplayName.Contains("configured"));
        Assert.Equal("gnome-terminal", specs[0].FileName);
    }
}
