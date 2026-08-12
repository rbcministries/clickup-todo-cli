using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the agent-dispatch settings model (#27): the pure config→launcher-options
/// mapping and the working-directory resolver. No API / no Terminal.Gui.
/// </summary>
public sealed class AgentDispatchSettingsTests
{
    /// <summary>A single-provider settings object, the #497 stand-in for the pre-provider exe/args pair.</summary>
    private static AgentDispatchSettings WithProvider(string? exe = null, List<string>? args = null) =>
        new()
        {
            Providers = [new DispatchProvider { Name = "P", Executable = exe ?? "claude", ExtraArgs = args ?? [] }],
            DefaultProviderName = "P",
        };

    [Fact]
    public void Defaults_AreZeroConfig()
    {
        var s = new AgentDispatchSettings();

        Assert.True(s.IsDefault);
        Assert.Equal(PreferredTerminal.Auto, s.PreferredTerminal);
        // A hand-new'd object carries no providers; the resolver synthesizes the built-in claude default.
        Assert.Empty(s.Providers);
        Assert.Equal("", s.DefaultProviderName);
        var resolved = s.ResolveDefaultProvider();
        Assert.Equal("claude", resolved.Executable);
        Assert.Empty(resolved.ExtraArgs);
        Assert.Equal(DispatchProviderKind.LocalCli, resolved.Kind);
        Assert.Equal(AgentWorkingDirectory.BaseWithTaskPrefill, s.WorkingDirectory);
        Assert.Equal(AgentSessionMode.Interactive, s.DefaultSessionMode);
        Assert.False(s.DefaultPostResultsToComments);
        Assert.Equal(LaunchLocation.NewWindow, s.LaunchLocation);
        Assert.Equal("", s.CustomTerminalCommand);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDefault_TreatsBlankOrDefaultSingleProviderAsDefault(string exe)
        => Assert.True(WithProvider(exe).IsDefault);

    [Fact]
    public void IsDefault_TrueForNoProviders()
        => Assert.True(new AgentDispatchSettings().IsDefault);

    [Fact]
    public void IsDefault_FalseOnceAnythingIsCustomised()
    {
        Assert.False(new AgentDispatchSettings { PreferredTerminal = PreferredTerminal.Pwsh }.IsDefault);
        Assert.False(WithProvider("/opt/claude").IsDefault);
        Assert.False(WithProvider(args: ["--model", "opus"]).IsDefault);
        // More than one configured provider is a customisation even if each is otherwise default.
        Assert.False(new AgentDispatchSettings { Providers = [new DispatchProvider(), new DispatchProvider()] }.IsDefault);
        Assert.False(new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home }.IsDefault);
        Assert.False(new AgentDispatchSettings { FixedWorkingDirectory = "/work" }.IsDefault);
        Assert.False(new AgentDispatchSettings { DefaultSessionMode = AgentSessionMode.OneOff }.IsDefault);
        Assert.False(new AgentDispatchSettings { DefaultPostResultsToComments = true }.IsDefault);
        Assert.False(new AgentDispatchSettings { PromptTemplate = "Custom {userPrompt}" }.IsDefault);
        Assert.False(new AgentDispatchSettings { LaunchLocation = LaunchLocation.NewTab }.IsDefault);
        Assert.False(new AgentDispatchSettings { CustomTerminalCommand = "alacritty -e {}" }.IsDefault);
        Assert.False(new AgentDispatchSettings { TryUseWindowsTerminalProfiles = true }.IsDefault);
    }

    // ── ResolveDefaultProvider ─────────────────────────────────────────────────────

    [Fact]
    public void ResolveDefaultProvider_EmptyList_SynthesizesClaudeDefault()
    {
        var p = new AgentDispatchSettings().ResolveDefaultProvider();
        Assert.Equal(AgentDispatchSettings.DefaultProviderDisplayName, p.Name);
        Assert.Equal("claude", p.Executable);
        Assert.Empty(p.ExtraArgs);
    }

    [Fact]
    public void ResolveDefaultProvider_PicksByDefaultName()
    {
        var s = new AgentDispatchSettings
        {
            Providers =
            [
                new DispatchProvider { Name = "A", Executable = "a" },
                new DispatchProvider { Name = "B", Executable = "b" },
            ],
            DefaultProviderName = "B",
        };
        Assert.Equal("b", s.ResolveDefaultProvider().Executable);
    }

    [Fact]
    public void ResolveDefaultProvider_FallsBackToFirst_WhenNameUnmatched()
    {
        var s = new AgentDispatchSettings
        {
            Providers =
            [
                new DispatchProvider { Name = "A", Executable = "a" },
                new DispatchProvider { Name = "B", Executable = "b" },
            ],
            DefaultProviderName = "missing",
        };
        Assert.Equal("a", s.ResolveDefaultProvider().Executable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDefault_TreatsBlankCustomTerminalCommandAsDefault(string cmd)
        => Assert.True(new AgentDispatchSettings { CustomTerminalCommand = cmd }.IsDefault);

    // ── ResolveProvider (per-dispatch pick, #498) ──────────────────────────────────

    private static AgentDispatchSettings TwoProviders() => new()
    {
        Providers =
        [
            new DispatchProvider { Name = "A", Executable = "a" },
            new DispatchProvider { Name = "B", Executable = "b" },
        ],
        DefaultProviderName = "A",
    };

    [Fact]
    public void ResolveProvider_PicksTheNamedProvider()
        => Assert.Equal("b", TwoProviders().ResolveProvider("B").Executable);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveProvider_BlankName_ResolvesTheConfiguredDefault(string? name)
        // A dispatch that never touched the provider control (blank pick) launches the default — "A".
        => Assert.Equal("a", TwoProviders().ResolveProvider(name).Executable);

    [Fact]
    public void ResolveProvider_UnknownName_FallsBackToDefault()
        // A provider the user deleted between opening the pane and submitting ⇒ the default, not a throw.
        => Assert.Equal("a", TwoProviders().ResolveProvider("deleted").Executable);

    [Fact]
    public void ResolveProvider_EmptyList_SynthesizesClaudeDefault()
        => Assert.Equal("claude", new AgentDispatchSettings().ResolveProvider("anything").Executable);

    // ── LastDispatchProviderName ───────────────────────────────────────────────────

    [Fact]
    public void LastDispatchProviderName_DefaultsBlank_AndDoesNotDisturbIsDefault()
    {
        var s = new AgentDispatchSettings();
        Assert.Equal("", s.LastDispatchProviderName);
        Assert.True(s.IsDefault);
        // A remembered pick is a UI-continuity hint, not a launch-affecting knob: it doesn't flip IsDefault.
        s.LastDispatchProviderName = "B";
        Assert.True(s.IsDefault);
    }

    // ── ToLauncherOptions ──────────────────────────────────────────────────────────

    [Fact]
    public void ToLauncherOptions_CopiesExecutableArgsAndPreference()
    {
        var s = WithProvider("/opt/claude", ["--model", "opus"]);
        s.PreferredTerminal = PreferredTerminal.Pwsh;
        s.LaunchLocation = LaunchLocation.NewTab;
        var opts = s.ToLauncherOptions();

        Assert.Equal("/opt/claude", opts.ClaudeExecutable);
        Assert.Equal(["--model", "opus"], opts.ExtraArgs);
        Assert.Equal(PreferredTerminal.Pwsh, opts.Preferred);
        Assert.Equal(LaunchLocation.NewTab, opts.LaunchLocation);
    }

    [Fact]
    public void ToLauncherOptions_ProjectsFromTheSelectedDefaultProvider()
    {
        var opts = new AgentDispatchSettings
        {
            Providers =
            [
                new DispatchProvider { Name = "A", Executable = "a", ExtraArgs = ["--a"] },
                new DispatchProvider { Name = "B", Executable = "b", ExtraArgs = ["--b"] },
            ],
            DefaultProviderName = "B",
        }.ToLauncherOptions();

        Assert.Equal("b", opts.ClaudeExecutable);
        Assert.Equal(["--b"], opts.ExtraArgs);
    }

    [Fact]
    public void ToLauncherOptions_DefaultsLaunchLocationToNewWindow()
        => Assert.Equal(LaunchLocation.NewWindow, new AgentDispatchSettings().ToLauncherOptions().LaunchLocation);

    [Fact]
    public void ToLauncherOptions_Provider_ProjectsTheGivenProvider_NotTheDefault()
    {
        // The per-dispatch overload (#498) projects the *passed* provider even when it isn't the default,
        // while still copying the provider-agnostic terminal/preference/launch-location fields.
        var s = new AgentDispatchSettings
        {
            Providers =
            [
                new DispatchProvider { Name = "A", Executable = "a", ExtraArgs = ["--a"] },
                new DispatchProvider { Name = "B", Executable = "  b  ", ExtraArgs = ["  --b ", "", "x"] },
            ],
            DefaultProviderName = "A",
            PreferredTerminal = PreferredTerminal.Pwsh,
            LaunchLocation = LaunchLocation.NewTab,
        };

        var opts = s.ToLauncherOptions(s.ResolveProvider("B"));

        Assert.Equal("b", opts.ClaudeExecutable);          // trimmed
        Assert.Equal(["--b", "x"], opts.ExtraArgs);        // trimmed, blanks dropped
        Assert.Equal(PreferredTerminal.Pwsh, opts.Preferred);
        Assert.Equal(LaunchLocation.NewTab, opts.LaunchLocation);
    }

    [Fact]
    public void ToLauncherOptions_Provider_BlankExecutable_CoalescesToClaude()
        => Assert.Equal(
            "claude",
            new AgentDispatchSettings().ToLauncherOptions(new DispatchProvider { Executable = "  " }).ClaudeExecutable);

    [Fact]
    public void ToLauncherOptions_ParsesCustomTerminalCommandIntoTokens()
    {
        var opts = new AgentDispatchSettings { CustomTerminalCommand = "'my term' -e {}" }.ToLauncherOptions();
        Assert.Equal(["my term", "-e", "{}"], opts.CustomTerminalCommand);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToLauncherOptions_BlankCustomTerminalCommand_IsEmpty(string cmd)
        => Assert.Empty(new AgentDispatchSettings { CustomTerminalCommand = cmd }.ToLauncherOptions().CustomTerminalCommand);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToLauncherOptions_CoalescesBlankExecutableToClaude(string exe)
        => Assert.Equal("claude", WithProvider(exe).ToLauncherOptions().ClaudeExecutable);

    [Fact]
    public void ToLauncherOptions_TrimsExecutable()
        => Assert.Equal("claude-x", WithProvider("  claude-x  ").ToLauncherOptions().ClaudeExecutable);

    [Fact]
    public void ToLauncherOptions_TrimsAndDropsBlankExtraArgs()
    {
        var opts = WithProvider(args: ["  --model ", "", "  ", "opus"]).ToLauncherOptions();
        Assert.Equal(["--model", "opus"], opts.ExtraArgs);
    }

    [Fact]
    public void ToLauncherOptions_ExtraArgsIsADistinctList()
    {
        var settings = WithProvider(args: ["--model"]);
        var opts = settings.ToLauncherOptions();

        settings.Providers[0].ExtraArgs.Add("mutated");

        Assert.Equal(["--model"], opts.ExtraArgs); // isolated from later mutation of the source list
    }

    // ── ResolveWorkingDirectory ────────────────────────────────────────────────────

    [Fact]
    public void ResolveWorkingDirectory_TaskDerived_UsesTheCandidate()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.BaseWithTaskPrefill };
        Assert.Equal("/repos/task", s.ResolveWorkingDirectory("/repos/task", "/home/me"));
    }

    [Fact]
    public void ResolveWorkingDirectory_TaskDerived_InheritsWhenNoCandidate()
    {
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.BaseWithTaskPrefill };
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
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.BaseWithTaskPrefill };
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
        var s = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.BaseWithTaskPrefill };
        Assert.Null(s.ResolveEffectiveWorkingDirectory(null, null, "/home/me"));
    }
}
