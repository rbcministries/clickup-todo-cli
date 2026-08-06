using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure "open this app's task in a new terminal tab" helper (#435) — the launcher
/// options and status strings shared by the dashboard's Ctrl+Enter (#301/#384) and single-task mode's
/// Ctrl+Enter, so the two hosts can't drift. Every branch runs without a terminal or a UI host.
/// </summary>
public sealed class AppTabLaunchTests
{
    private static AppLaunchCommand SampleCommand => new("clickup-todo", ["--task", "abc123"]);

    // ── Options ───────────────────────────────────────────────────────────────

    [Fact]
    public void Options_launches_in_a_new_tab_of_the_current_terminal()
        => Assert.Equal(LaunchLocation.NewTab, AppTabLaunch.Options(PreferredTerminal.Auto, null).LaunchLocation);

    [Theory]
    [InlineData(PreferredTerminal.Auto)]
    [InlineData(PreferredTerminal.WindowsTerminal)]
    [InlineData(PreferredTerminal.Pwsh)]
    public void Options_carries_the_windows_preferred_terminal(PreferredTerminal preferred)
        => Assert.Equal(preferred, AppTabLaunch.Options(preferred, null).Preferred);

    [Fact]
    public void Options_parses_the_custom_terminal_command_through_the_shared_parser()
    {
        const string custom = "kitty {} --title tab";
        var options = AppTabLaunch.Options(PreferredTerminal.Auto, custom);
        // Delegates to TerminalCommandParser (not a bespoke split), so it stays in step with #385.
        Assert.Equal(TerminalCommandParser.Parse(custom), options.CustomTerminalCommand);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_with_no_custom_command_yields_an_empty_argv(string? custom)
        => Assert.Empty(AppTabLaunch.Options(PreferredTerminal.Auto, custom).CustomTerminalCommand);

    [Fact]
    public void Options_does_not_carry_claude_dispatch_settings()
    {
        // ClaudeExecutable/ExtraArgs are an agent-dispatch concern — an app relaunch must leave them at
        // their defaults (i.e. not use AgentDispatchSettings.ToLauncherOptions).
        var options = AppTabLaunch.Options(PreferredTerminal.Auto, null);
        Assert.Equal("claude", options.ClaudeExecutable);
        Assert.Empty(options.ExtraArgs);
    }

    // ── Opening / Opened ──────────────────────────────────────────────────────

    [Fact]
    public void Opening_names_the_task()
        => Assert.Equal("Opening 'My Task' in a new terminal tab…", AppTabLaunch.Opening("My Task"));

    [Fact]
    public void Opened_names_the_task_and_the_terminal()
        => Assert.Equal(
            "Opened 'My Task' in a new tab (gnome-terminal).",
            AppTabLaunch.Opened("My Task", new LaunchResult(true, "gnome-terminal", Error: null)));

    [Fact]
    public void Opened_appends_a_non_fatal_note_when_present()
        => Assert.Equal(
            "Opened 'My Task' in a new tab (xterm). Opened a new window (no tab support).",
            AppTabLaunch.Opened("My Task",
                new LaunchResult(true, "xterm", Error: null, Note: "Opened a new window (no tab support).")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Opened_omits_a_blank_note(string? note)
        => Assert.Equal(
            "Opened 'My Task' in a new tab (kitty).",
            AppTabLaunch.Opened("My Task", new LaunchResult(true, "kitty", Error: null, Note: note)));

    // ── Fallback ──────────────────────────────────────────────────────────────

    [Fact]
    public void Fallback_when_copied_points_at_the_clipboard()
        => Assert.Equal(
            $"Couldn't open a terminal tab. Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppTabLaunch.Fallback(SampleCommand, copied: true));

    [Fact]
    public void Fallback_when_not_copied_asks_the_user_to_run_it()
        => Assert.Equal(
            $"Couldn't open a terminal tab. Run: {SampleCommand.ToDisplayCommand()}",
            AppTabLaunch.Fallback(SampleCommand, copied: false));

    [Fact]
    public void Fallback_names_the_failure_reason_when_the_launch_threw()
        => Assert.Equal(
            $"Couldn't open a terminal tab (spawn denied). Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppTabLaunch.Fallback(SampleCommand, copied: true, reason: "spawn denied"));
}
