using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the agent-dispatch settings model (#27): the pure config→launcher-options
/// mapping and the working-directory resolver. No API / no Terminal.Gui.
/// </summary>
public sealed class AgentDispatchSettingsTests
{
    [Fact]
    public void Defaults_AreZeroConfig()
    {
        var s = new AgentDispatchSettings();

        Assert.True(s.IsDefault);
        Assert.Equal(PreferredTerminal.Auto, s.PreferredTerminal);
        Assert.Equal("claude", s.ClaudeExecutable);
        Assert.Empty(s.ExtraArgs);
        Assert.Equal(AgentWorkingDirectory.TaskDerived, s.WorkingDirectory);
        Assert.Equal(AgentSessionMode.Interactive, s.DefaultSessionMode);
        Assert.False(s.DefaultPostResultsToComments);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDefault_TreatsBlankOrDefaultExecutableAsDefault(string exe)
        => Assert.True(new AgentDispatchSettings { ClaudeExecutable = exe }.IsDefault);

    [Fact]
    public void IsDefault_FalseOnceAnythingIsCustomised()
    {
        Assert.False(new AgentDispatchSettings { PreferredTerminal = PreferredTerminal.Pwsh }.IsDefault);
        Assert.False(new AgentDispatchSettings { ClaudeExecutable = "/opt/claude" }.IsDefault);
        Assert.False(new AgentDispatchSettings { ExtraArgs = ["--model", "opus"] }.IsDefault);
        Assert.False(new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home }.IsDefault);
        Assert.False(new AgentDispatchSettings { FixedWorkingDirectory = "/work" }.IsDefault);
        Assert.False(new AgentDispatchSettings { DefaultSessionMode = AgentSessionMode.OneOff }.IsDefault);
        Assert.False(new AgentDispatchSettings { DefaultPostResultsToComments = true }.IsDefault);
        Assert.False(new AgentDispatchSettings { PromptPreamble = "Custom." }.IsDefault);
    }

    // ── ToLauncherOptions ──────────────────────────────────────────────────────────

    [Fact]
    public void ToLauncherOptions_CopiesExecutableArgsAndPreference()
    {
        var opts = new AgentDispatchSettings
        {
            ClaudeExecutable = "/opt/claude",
            ExtraArgs = ["--model", "opus"],
            PreferredTerminal = PreferredTerminal.Pwsh,
        }.ToLauncherOptions();

        Assert.Equal("/opt/claude", opts.ClaudeExecutable);
        Assert.Equal(["--model", "opus"], opts.ExtraArgs);
        Assert.Equal(PreferredTerminal.Pwsh, opts.Preferred);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToLauncherOptions_CoalescesBlankExecutableToClaude(string exe)
        => Assert.Equal("claude", new AgentDispatchSettings { ClaudeExecutable = exe }.ToLauncherOptions().ClaudeExecutable);

    [Fact]
    public void ToLauncherOptions_TrimsExecutable()
        => Assert.Equal("claude-x", new AgentDispatchSettings { ClaudeExecutable = "  claude-x  " }.ToLauncherOptions().ClaudeExecutable);

    [Fact]
    public void ToLauncherOptions_TrimsAndDropsBlankExtraArgs()
    {
        var opts = new AgentDispatchSettings { ExtraArgs = ["  --model ", "", "  ", "opus"] }.ToLauncherOptions();
        Assert.Equal(["--model", "opus"], opts.ExtraArgs);
    }

    [Fact]
    public void ToLauncherOptions_ExtraArgsIsADistinctList()
    {
        var settings = new AgentDispatchSettings { ExtraArgs = ["--model"] };
        var opts = settings.ToLauncherOptions();

        settings.ExtraArgs.Add("mutated");

        Assert.Equal(["--model"], opts.ExtraArgs); // isolated from later mutation of the source list
    }

    // ── ResolveWorkingDirectory ────────────────────────────────────────────────────

    [Fact]
    public void ResolveWorkingDirectory_TaskDerived_UsesTheCandidate()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.TaskDerived };
        Assert.Equal("/repos/task", s.ResolveWorkingDirectory("/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveWorkingDirectory_TaskDerived_InheritsWhenNoCandidate()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.TaskDerived };
        Assert.Null(s.ResolveWorkingDirectory(null, "/home/me"));
    }

    [Fact]
    public void ResolveWorkingDirectory_Home_UsesHome()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home };
        Assert.Equal("/home/me", s.ResolveWorkingDirectory("/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveWorkingDirectory_Home_InheritsWhenHomeBlank()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home };
        Assert.Null(s.ResolveWorkingDirectory("/repos/task", "   "));
    }

    [Fact]
    public void ResolveWorkingDirectory_Fixed_UsesTheFixedPathTrimmed()
    {
        var s = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "  /work/here  ",
        };
        Assert.Equal("/work/here", s.ResolveWorkingDirectory("/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveWorkingDirectory_Fixed_InheritsWhenFixedBlank()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Fixed, FixedWorkingDirectory = "" };
        Assert.Null(s.ResolveWorkingDirectory("/repos/task", "/home/me"));
    }

    // ── ResolveEffectiveWorkingDirectory (#101 precedence: cache → default mode) ─────

    [Fact]
    public void ResolveEffectiveWorkingDirectory_CachedDirectory_WinsOverEverything()
    {
        // Even a Fixed mode with a fixed dir is overridden by a per-task cache hit (#96).
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Fixed, FixedWorkingDirectory = "/fixed" };
        Assert.Equal("/cache/task", s.ResolveEffectiveWorkingDirectory("/cache/task", "/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveEffectiveWorkingDirectory_CachedDirectory_IsTrimmed()
    {
        var s = new AgentDispatchSettings();
        Assert.Equal("/cache/task", s.ResolveEffectiveWorkingDirectory("  /cache/task  ", "/repos/task", "/home/me"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEffectiveWorkingDirectory_BlankCache_FallsThroughToTaskDerivedMode(string? cache)
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.TaskDerived };
        Assert.Equal("/repos/task", s.ResolveEffectiveWorkingDirectory(cache, "/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveEffectiveWorkingDirectory_BlankCache_FallsThroughToHomeMode()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home };
        Assert.Equal("/home/me", s.ResolveEffectiveWorkingDirectory(null, "/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveEffectiveWorkingDirectory_BlankCache_FallsThroughToFixedMode()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Fixed, FixedWorkingDirectory = "/work" };
        Assert.Equal("/work", s.ResolveEffectiveWorkingDirectory(null, "/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveEffectiveWorkingDirectory_NoCacheAndNoCandidate_Inherits()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.TaskDerived };
        Assert.Null(s.ResolveEffectiveWorkingDirectory(null, null, "/home/me"));
    }
}
