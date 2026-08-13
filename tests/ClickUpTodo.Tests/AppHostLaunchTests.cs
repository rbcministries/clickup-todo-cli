using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure "open this app's task in its own terminal" helper (#435/#504) — the launcher
/// options and status strings shared by the dashboard's Ctrl+Enter (#301/#384), single-task mode's
/// Ctrl+Enter and the feed's Enter, so the hosts can't drift. Generalised from the tab-only
/// <c>AppTabLaunch</c>: each helper takes a <see cref="LaunchLocation"/> destination and words its status
/// accordingly (new tab / new window / split pane). The <c>NewTab</c> wording was once pinned byte-identical
/// to the retired helper's strings, but #591 softened it to a host-neutral "… where supported" so the status
/// line never asserts a literal tab a host didn't open (a Zellij pane or a window fallback down the #589
/// split → tab → window ladder). Every branch runs without a terminal or a UI host.
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

    // ── Opening / Opened — NewTab wording softened to host-neutral "… where supported" (#591) ──

    [Fact]
    public void Opening_names_the_task_for_a_new_tab()
        => Assert.Equal(
            "Opening 'My Task' in a new terminal tab where supported…",
            AppHostLaunch.Opening("My Task", LaunchLocation.NewTab));

    [Fact]
    public void Opened_names_the_task_and_the_terminal_for_a_new_tab()
        => Assert.Equal(
            "Opened 'My Task' in a new tab where supported (gnome-terminal).",
            AppHostLaunch.Opened("My Task", LaunchLocation.NewTab, new LaunchResult(true, "gnome-terminal", Error: null)));

    [Fact]
    public void Opened_appends_a_non_fatal_note_when_present()
        => Assert.Equal(
            "Opened 'My Task' in a new tab where supported (xterm). Opened a new window (no tab support).",
            AppHostLaunch.Opened("My Task", LaunchLocation.NewTab,
                new LaunchResult(true, "xterm", Error: null, Note: "Opened a new window (no tab support).")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Opened_omits_a_blank_note(string? note)
        => Assert.Equal(
            "Opened 'My Task' in a new tab where supported (kitty).",
            AppHostLaunch.Opened("My Task", LaunchLocation.NewTab, new LaunchResult(true, "kitty", Error: null, Note: note)));

    // #591 regression: the NewTab lead must not contradict a non-tab surface the #589 ladder resolved to.
    // Opened's parenthetical carries the true LaunchSpec.Description (e.g. a Zellij pane or a window
    // fallback), so the softened "where supported" lead reads honestly alongside it.
    [Theory]
    [InlineData("Zellij (new pane)")]
    [InlineData("WezTerm (new window)")]
    public void Opened_for_a_new_tab_does_not_claim_a_literal_tab_for_a_non_tab_surface(string launchedWith)
    {
        var message = AppHostLaunch.Opened(
            "My Task", LaunchLocation.NewTab, new LaunchResult(true, launchedWith, Error: null));

        // The actual surface is named verbatim, and the lead hedges rather than asserting a bare "a new tab".
        Assert.Equal($"Opened 'My Task' in a new tab where supported ({launchedWith}).", message);
        Assert.Contains("where supported", message);
        Assert.DoesNotContain("in a new tab (", message);
    }

    // ── Destination-aware wording (#504) ──────────────────────────────────────

    [Theory]
    [InlineData(LaunchLocation.NewTab, "Opening 'My Task' in a new terminal tab where supported…")]
    [InlineData(LaunchLocation.NewWindow, "Opening 'My Task' in a new terminal window…")]
    [InlineData(LaunchLocation.SplitPane, "Opening 'My Task' in a split pane…")]
    public void Opening_words_the_destination(LaunchLocation destination, string expected)
        => Assert.Equal(expected, AppHostLaunch.Opening("My Task", destination));

    [Theory]
    [InlineData(LaunchLocation.NewTab, "Opened 'My Task' in a new tab where supported (wezterm).")]
    [InlineData(LaunchLocation.NewWindow, "Opened 'My Task' in a new window (wezterm).")]
    [InlineData(LaunchLocation.SplitPane, "Opened 'My Task' in a split pane (wezterm).")]
    public void Opened_words_the_destination(LaunchLocation destination, string expected)
        => Assert.Equal(
            expected, AppHostLaunch.Opened("My Task", destination, new LaunchResult(true, "wezterm", Error: null)));

    [Theory]
    [InlineData(LaunchLocation.NewTab, "Couldn't open a terminal tab where supported.")]
    [InlineData(LaunchLocation.NewWindow, "Couldn't open a terminal window.")]
    [InlineData(LaunchLocation.SplitPane, "Couldn't open a split pane.")]
    public void Fallback_words_the_destination(LaunchLocation destination, string lead)
        => Assert.Equal(
            $"{lead} Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, destination, copied: true));

    // ── Fallback — NewTab wording softened to host-neutral "… where supported" (#591) ──

    [Fact]
    public void Fallback_when_copied_points_at_the_clipboard()
        => Assert.Equal(
            $"Couldn't open a terminal tab where supported. Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.NewTab, copied: true));

    [Fact]
    public void Fallback_when_not_copied_asks_the_user_to_run_it()
        => Assert.Equal(
            $"Couldn't open a terminal tab where supported. Run: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.NewTab, copied: false));

    [Fact]
    public void Fallback_names_the_failure_reason_when_the_launch_threw()
        => Assert.Equal(
            $"Couldn't open a terminal tab where supported (spawn denied). Command copied to clipboard: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.NewTab, copied: true, reason: "spawn denied"));

    [Fact]
    public void Fallback_words_the_reason_for_a_split_pane()
        => Assert.Equal(
            $"Couldn't open a split pane (spawn denied). Run: {SampleCommand.ToDisplayCommand()}",
            AppHostLaunch.Fallback(SampleCommand, LaunchLocation.SplitPane, copied: false, reason: "spawn denied"));

    // ── Viability floor (#505/#515, slice C — the shared seam #507 E's hosts call) ──────────────

    private static TerminalLauncherOptions SplitOptions =>
        AppHostLaunch.Options(LaunchLocation.SplitPane, PreferredTerminal.Auto, null);

    // An even side-by-side split of a narrow terminal gives two sub-60-col panes, below the readable floor
    // (SplitViability.DefaultMinPaneColumns), so the request degrades to a tab and carries a flashable reason.
    [Fact]
    public void ApplyViabilityFloor_degrades_a_too_narrow_split_to_a_tab_with_a_reason()
    {
        var (result, reason) = AppHostLaunch.ApplyViabilityFloor(SplitOptions, terminalColumns: 100);

        Assert.Equal(LaunchLocation.NewTab, result.LaunchLocation);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // A wide terminal splits into two comfortably-readable panes, so the request stays a split with no reason.
    [Fact]
    public void ApplyViabilityFloor_keeps_a_wide_enough_split_and_reports_no_reason()
    {
        var (result, reason) = AppHostLaunch.ApplyViabilityFloor(SplitOptions, terminalColumns: 200);

        Assert.Equal(LaunchLocation.SplitPane, result.LaunchLocation);
        Assert.Null(reason);
    }

    // The floor only judges an explicit split: a new-tab / new-window request is returned untouched (same
    // record instance) even at a hostile width — it isn't a split, so there is nothing to degrade.
    [Theory]
    [InlineData(LaunchLocation.NewTab)]
    [InlineData(LaunchLocation.NewWindow)]
    public void ApplyViabilityFloor_leaves_a_non_split_request_untouched(LaunchLocation destination)
    {
        var options = AppHostLaunch.Options(destination, PreferredTerminal.Auto, null);

        var (result, reason) = AppHostLaunch.ApplyViabilityFloor(options, terminalColumns: 1);

        Assert.Same(options, result);
        Assert.Null(reason);
    }

    // A headless caller (no live driver) supplies a null width: the floor self-disables and the split
    // passes through byte-identical, leaving the planner's own host-capability ladder to resolve it.
    [Fact]
    public void ApplyViabilityFloor_passes_a_split_through_untouched_when_the_width_is_unknown()
    {
        var options = SplitOptions;

        var (result, reason) = AppHostLaunch.ApplyViabilityFloor(options, terminalColumns: null);

        Assert.Same(options, result);
        Assert.Null(reason);
    }
}
