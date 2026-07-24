using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure app-launch command resolver (#301) — how the app decides to relaunch itself
/// to open a task in its own terminal tab. The PATH probe and process path are injected so every branch
/// is exercised without touching the real PATH or process.
/// </summary>
public sealed class AppLaunchCommandTests
{
    private static Func<string, bool> Present(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static Func<string, bool> None => _ => false;

    [Fact]
    public void ForTask_PrefersClickUpTodoOnPath()
    {
        var cmd = AppLaunchCommand.ForTask("86abc", Present("clickup-todo"), "/opt/whatever/dotnet");

        Assert.Equal("clickup-todo", cmd.FileName);
        Assert.Equal(["--task", "86abc"], cmd.Arguments);
    }

    [Fact]
    public void ForTask_UsesProcessPath_WhenNotOnPath_AndProcessIsARealApphost()
    {
        var cmd = AppLaunchCommand.ForTask("86abc", None, "/home/me/.dotnet/tools/clickup-todo");

        Assert.Equal("/home/me/.dotnet/tools/clickup-todo", cmd.FileName);
        Assert.Equal(["--task", "86abc"], cmd.Arguments);
    }

    [Fact]
    public void ForTask_ResolvesApphostByFileName_EvenWithWindowsExeExtension()
    {
        var cmd = AppLaunchCommand.ForTask("t", None, @"C:\tools\clickup-todo.exe");

        Assert.Equal(@"C:\tools\clickup-todo.exe", cmd.FileName);
    }

    [Theory]
    [InlineData("/usr/bin/dotnet")]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void ForTask_FallsBackToToolName_WhenProcessIsTheDotnetMuxer(string muxer)
    {
        // A `dotnet run` / `dotnet ClickUpTodo.dll` dev launch can't be relaunched with `--task`, so the
        // resolver names the tool (which won't be on PATH in dev) and lets the caller surface the
        // copy-command fallback rather than launching `dotnet --task <id>`.
        var cmd = AppLaunchCommand.ForTask("t", None, muxer);

        Assert.Equal("clickup-todo", cmd.FileName);
        Assert.Equal(["--task", "t"], cmd.Arguments);
    }

    [Fact]
    public void ForTask_FallsBackToToolName_WhenProcessPathIsNullOrBlank()
    {
        Assert.Equal("clickup-todo", AppLaunchCommand.ForTask("t", None, null).FileName);
        Assert.Equal("clickup-todo", AppLaunchCommand.ForTask("t", None, "   ").FileName);
    }

    [Fact]
    public void ForTask_TrimsTaskId()
    {
        var cmd = AppLaunchCommand.ForTask("  86abc  ", Present("clickup-todo"), null);

        Assert.Equal(["--task", "86abc"], cmd.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForTask_Rejects_BlankTaskId(string id)
        => Assert.Throws<ArgumentException>(() => AppLaunchCommand.ForTask(id, Present("clickup-todo"), null));

    [Fact]
    public void ToDisplayCommand_QuotesOnlyTokensWithSpaces()
    {
        var onPath = new AppLaunchCommand("clickup-todo", ["--task", "86abc"]);
        Assert.Equal("clickup-todo --task 86abc", onPath.ToDisplayCommand());

        var spaced = new AppLaunchCommand("/Applications/My Tools/clickup-todo", ["--task", "86abc"]);
        Assert.Equal("\"/Applications/My Tools/clickup-todo\" --task 86abc", spaced.ToDisplayCommand());
    }
}
