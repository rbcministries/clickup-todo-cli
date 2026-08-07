using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the #462 Windows Terminal profile threading: when a dispatch matched a WT profile,
/// <see cref="TerminalLauncherOptions.WindowsTerminalProfile"/> inserts <c>-p &lt;profile&gt;</c> into
/// the <c>wt</c> candidate (both the new-window and new-tab forms), and a blank profile leaves the
/// argv byte-identical to today. The app-launch (#301) path never emits <c>-p</c>.
/// </summary>
public sealed class TerminalCommandPlannerWtProfileTests
{
    private const string PromptFile = "/tmp/prompt.txt";
    private static readonly AppLaunchCommand AppCommand = new("clickup-todo", ["--task", "86abc"]);

    private static Func<string, bool> Present(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var map = vars.ToDictionary(v => v.Item1, v => v.Item2, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static LaunchSpec WtSpec(TerminalLauncherOptions options, Func<string, string?>? env = null)
        => TerminalCommandPlanner.Plan(OSPlatformKind.Windows, Present("wt"), env ?? (_ => null), PromptFile, null, options)
            .Single(s => s.FileName == "wt");

    [Fact]
    public void NewWindow_InsertsProfileAfterNewTabSubcommand()
    {
        var spec = WtSpec(new TerminalLauncherOptions { WindowsTerminalProfile = "{bbb}" });

        Assert.Equal(["new-tab", "-p", "{bbb}", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(6));
        Assert.Equal("Windows Terminal", spec.DisplayName);
    }

    [Fact]
    public void NewTab_InsertsProfileAfterNewTabSubcommand()
    {
        var options = new TerminalLauncherOptions
        {
            WindowsTerminalProfile = "My Project",
            LaunchLocation = LaunchLocation.NewTab,
        };
        var spec = WtSpec(options, Env(("WT_SESSION", "abc")));

        Assert.Equal(["-w", "0", "new-tab", "-p", "My Project", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(8));
        Assert.Equal("Windows Terminal (new tab)", spec.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankProfile_IsByteIdenticalToToday(string? profile)
    {
        var spec = WtSpec(new TerminalLauncherOptions { WindowsTerminalProfile = profile });

        // Exactly the pre-#462 argv — no `-p` anywhere.
        Assert.Equal(["new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(4));
        Assert.DoesNotContain("-p", spec.Arguments);
    }

    [Fact]
    public void OtherHosts_AreUnaffectedByProfile()
    {
        // The profile only decorates the wt candidate; pwsh/powershell fallbacks stay intact.
        var specs = TerminalCommandPlanner.Plan(
            OSPlatformKind.Windows, Present("wt", "pwsh", "powershell"), _ => null, PromptFile, null,
            new TerminalLauncherOptions { WindowsTerminalProfile = "{bbb}" });

        var pwsh = specs.Single(s => s.FileName == "pwsh");
        Assert.Equal(["-NoExit", "-Command"], pwsh.Arguments.Take(2));
        Assert.DoesNotContain("-p", pwsh.Arguments);
    }

    [Fact]
    public void AppLaunch_NeverEmitsProfile_EvenWhenOptionSet()
    {
        // The "open this app in a new tab" gesture has no working directory, so a profile is meaningless
        // and must never leak into its wt argv even if a caller supplies one.
        var spec = TerminalCommandPlanner.PlanAppLaunch(
            OSPlatformKind.Windows, Present("wt"), _ => null, AppCommand,
            new TerminalLauncherOptions { WindowsTerminalProfile = "{bbb}" })
            .Single(s => s.FileName == "wt");

        Assert.DoesNotContain("-p", spec.Arguments);
        Assert.Equal(["new-tab", "pwsh", "-NoExit", "-Command"], spec.Arguments.Take(4));
    }
}
