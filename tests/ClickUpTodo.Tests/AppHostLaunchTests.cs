using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure "open this app's task in its own terminal" helper (#435/#504) — the launcher
/// options and status strings shared by the dashboard's Ctrl+Enter (#301/#384), single-task mode's
/// Ctrl+Enter and the feed's Enter, so the hosts can't drift. Generalised from the tab-only
/// <c>AppTabLaunch</c>: each helper takes a <see cref="LaunchLocation"/> destination and words its status
/// accordingly (new tab / new window / split pane). The <c>NewTab</c> wording is pinned byte-identical to
/// the retired helper's strings. Every branch runs without a terminal or a UI host.
/// </summary>
public sealed class AppHostLaunchTests
{
    private static AppLaunchCommand SampleCommand => new("clickup-todo", ["--task", "abc123"]);

    // ── Options ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LaunchLocation.NewTab)]
    [InlineData(LaunchLocation.NewWindow)]
    [InlineData(LaunchLocation.SplitPane)]
    public void Options_carries_the_requested_destination(LaunchLocation destination)
        => Assert.Equal(destination, AppHostLaunch.Options(destination, PreferredTerminal.Auto, null).LaunchLocation);

    [Theory]
    [InlineData(PreferredTerminal.Auto)]
    [InlineData(PreferredTerminal.WindowsTerminal)]
    [InlineData(PreferredTerminal.Pwsh)]
    public void Options_carries_the_windows_preferred_terminal(PreferredTerminal preferred)
        => Assert.Equal(preferred, AppHostLaunch.Options(LaunchLocation.NewTab, preferred, null).Preferred);

    [Fact]
    public void Options_parses_the_custom_terminal_command_through_the_shared_parser()
    {
        const string custom = "kitty {} --title tab";
        var options = AppHostLaunch.Options(LaunchLocation.NewTab, PreferredTerminal.Auto, custom);
        // Delegates to TerminalCommandParser (not a bespoke split), so it stays in step with #385.
        Assert.Equal(TerminalCommandParser.Parse(custom), options.CustomTerminalCommand);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_with_no_custom_command_yields_an_empty_argv(string? custom)
        => Assert.Empty(AppHostLaunch.Options(LaunchLocation.NewTab, PreferredTerminal.Auto, custom).CustomTerminalCommand);

    [Fact]
    public void Options_does_not_carry_claude_dispatch_settings()
    {
        // ClaudeExecutable/ExtraArgs are an agent-dispatch concern — an app relaunch must leave them at
        // their defaults (i.e. not use AgentDispatchSettings.ToLauncherOptions).
        var options = AppHostLaunch.Options(LaunchLocation.NewTab, PreferredTerminal.Auto, null);
        Assert.Equal("claude", options.ClaudeExecutable);
        Assert.Empty(options.ExtraArgs);
    }

    // ── Opening / Opened — NewTab wording pinned to the retired AppTabLaunch strings ──

    [Fact]
    public void Opening_names_the_task_for_a_new_tab()
        => Assert.Equal(
            "Opening 'My Task' in a new terminal tab…", AppHostLaunch.Opening("My Task", LaunchLocation.NewTab));

    [Fact]
    public void Opened_names_the_task_and_the_terminal_for_a_new_tab()
        => Assert.Equal(
            "Opened 'My Task' in a new tab (gnome-terminal).",
            AppHostLaunch.Opened("My Task", LaunchLocation.NewTab, new LaunchResult(true, "gnome-terminal", Error: null)));

    [Fact]
    public void Opened_appends_a_non_fatal_note_when_present()
        => Assert.Equal(
            "Opened 'My Task' in a new tab (xterm). Opened a new window (no tab support).",
            AppHostLaunch.Opened("My Task", LaunchLocation.NewTab,
                new LaunchResult(true, "xterm", Error: null, Note: "Opened a new window (no tab support).")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Opened_omits_a_blank_note(string? note)
        => Assert.Equal(
            "Opened 'My Task' in a new tab (kitty).",
            AppHostLaunch.Opened("My Task", LaunchLocation.NewTab, new LaunchResult(true, "kitty", Error: null, Note: note)));

    // ── Destination-aware wording (#504) ──────────────────────────────────────

    [Theory]
    [InlineData(LaunchLocation.NewTab, "Opening 'My Task' in a new terminal tab…")]
    [InlineData(LaunchLocation.NewWindow, "Opening 'My Task' in a new terminal window…")]
    [InlineData(LaunchLocation.SplitPane, "Opening 'My Task' in a split pane…")]
    public void Opening_words_the_destination(LaunchLocation destination, string expected)
        => Assert.Equal(expected, AppHostLaunch.Opening("My Task", destination));

    [Theory]
    [InlineData(LaunchLocation.NewTab, "Opened 'My Task' in a new tab (wezterm).")]
    [InlineData(LaunchLocation.NewWindow, "Opened 'My Task' in a new window (wezterm).")]
    [InlineData(LaunchLocation.SplitPane, "Opened 'My Task' in a split pane (wezterm).")]
    public void Opened_words_the_destination(LaunchLocation destination, string expected)
        => Assert.Equal(
            expected, AppHostLaunch.Opened("My Task", destination, new LaunchResult(true, "wezterm", Error: null)));

    [Theory]
    [InlineData(LaunchLocation.NewTab, "Couldn't open a terminal tab.")]
    [InlineData(LaunchLocation.NewWindow, "Couldn't open a terminal window.")]
    [InlineData(LaunchLocation.SplitPane, "Couldn't open a split pane.")]
    public void Fallback_words_the_destination(LaunchLocation destination, string lead)
        => Assert.Equal(
            $"{lead} Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, destination, copied: true));

    // ── Fallback — NewTab wording pinned to the retired AppTabLaunch strings ──

    [Fact]
    public void Fallback_when_copied_points_at_the_clipboard()
        => Assert.Equal(
            $"Couldn't open a terminal tab. Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.NewTab, copied: true));

    [Fact]
    public void Fallback_when_not_copied_asks_the_user_to_run_it()
        => Assert.Equal(
            $"Couldn't open a terminal tab. Run: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.NewTab, copied: false));

    [Fact]
    public void Fallback_names_the_failure_reason_when_the_launch_threw()
        => Assert.Equal(
            $"Couldn't open a terminal tab (spawn denied). Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.NewTab, copied: true, reason: "spawn denied"));

    [Fact]
    public void Fallback_words_the_reason_for_a_split_pane()
        => Assert.Equal(
            $"Couldn't open a split pane (spawn denied). Run: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.SplitPane, copied: false, reason: "spawn denied"));
}
