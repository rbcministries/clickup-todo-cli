namespace ClickUpTodo.Agent;

/// <summary>
/// The shared, UI-agnostic pieces of the "open this app's task in its own terminal" gesture —
/// the dashboard's main-list / Task Detail <c>Ctrl+Enter</c> (#301/#384), single-task launch mode's
/// <c>Ctrl+Enter</c> (#435), and the feed's Enter. Both the launcher options and the status messages are
/// built here, so the hosts can't drift; the async launch, the re-entrancy guard and the flash/clipboard
/// sinks stay with each host (UI-thread concerns that can't be unit-tested).
///
/// Generalised from the tab-only <c>AppTabLaunch</c> (#504): each helper takes the
/// <see cref="LaunchLocation"/> destination rather than hard-coding <see cref="LaunchLocation.NewTab"/>,
/// and the status wording follows the destination (new tab / new window / split pane). The current callers
/// still launch a tab; the split-pane gesture (#502 E/F) will pass <see cref="LaunchLocation.SplitPane"/>.
/// Pure (primitives in, values out) so every branch is covered without a terminal or a UI host.
/// </summary>
public static class AppHostLaunch
{
    /// <summary>
    /// The launcher options for an app-host launch to <paramref name="destination"/> (a new tab / window
    /// of the current terminal, or a split pane beside it — falling back down the split → tab → window
    /// ladder per emulator support), honouring the Windows preferred terminal and the custom terminal
    /// command (#385). Deliberately <b>not</b>
    /// <see cref="Configuration.AgentDispatchSettings.ToLauncherOptions"/> — <c>ClaudeExecutable</c> and
    /// <c>ExtraArgs</c> are an agent-dispatch concern and don't apply to relaunching this app.
    /// </summary>
    public static TerminalLauncherOptions Options(
        LaunchLocation destination, PreferredTerminal preferred, string? customTerminalCommand) => new()
        {
            LaunchLocation = destination,
            Preferred = preferred,
            CustomTerminalCommand = TerminalCommandParser.Parse(customTerminalCommand),
        };

    /// <summary>
    /// Applies the split-pane viability floor (#505/#515, slice C) to an app-host launch: a
    /// <see cref="LaunchLocation.SplitPane"/> request degrades to a <see cref="LaunchLocation.NewTab"/>
    /// <b>before</b> planning when the live terminal is too narrow to read a split, because the planner
    /// (<see cref="TerminalCommandPlanner"/>) has no notion of terminal width — the caller decides, exactly
    /// as <c>DispatchCoordinator</c> does for a dispatch. Shared by the dashboard and single-task hosts so
    /// the two can't drift (the same reason <see cref="Options"/> / <see cref="Opened"/> live here).
    /// <para>
    /// Returns the (possibly re-targeted) <paramref name="options"/> and a ready-to-flash degrade reason —
    /// non-null <b>only</b> when it degraded, so a caller can append it to the success status. Only an
    /// explicit <see cref="LaunchLocation.SplitPane"/> request with a live width
    /// (<paramref name="terminalColumns"/> non-null) is evaluated; a new tab / window request, or a headless
    /// caller with no driver width, returns <paramref name="options"/> unchanged and a null reason — so
    /// those launches stay byte-identical. The split geometry judged is read from <paramref name="options"/>
    /// (the shape the planner will draw), matching <c>DispatchCoordinator</c>.
    /// </para>
    /// </summary>
    public static (TerminalLauncherOptions Options, string? DegradeReason) ApplyViabilityFloor(
        TerminalLauncherOptions options, int? terminalColumns)
    {
        if (options.LaunchLocation != LaunchLocation.SplitPane || terminalColumns is not { } cols)
            return (options, null);

        var decision = SplitViability.Evaluate(cols, options.SplitDirection, options.SplitSizePercent);
        return decision.Degraded
            ? (options with { LaunchLocation = decision.Location }, decision.Reason)
            : (options, null);
    }

    /// <summary>The status flashed while the host is opening, worded for the destination.</summary>
    public static string Opening(string name, LaunchLocation destination)
        => $"Opening '{name}' in {OpeningPhrase(destination)}…";

    /// <summary>
    /// The success status once the launcher reports which terminal it used, appending any non-fatal
    /// <see cref="LaunchResult.Note"/> (e.g. a fell-back-to-window notice).
    /// </summary>
    public static string Opened(string name, LaunchLocation destination, LaunchResult result)
    {
        var message = $"Opened '{name}' in {OpenedPhrase(destination)} ({result.LaunchedWith}).";
        return string.IsNullOrWhiteSpace(result.Note) ? message : $"{message} {result.Note}";
    }

    /// <summary>
    /// The no-terminal fallback status (#301): the exact relaunch command, said to be on the clipboard
    /// when the copy succeeded (<paramref name="copied"/>), else asked to be run by hand.
    /// <paramref name="reason"/> names the failure when the launch threw (vs. simply finding no
    /// emulator to launch).
    /// </summary>
    public static string Fallback(
        AppLaunchCommand command, LaunchLocation destination, bool copied, string? reason = null)
    {
        var cmd = command.ToDisplayCommand();
        var what = FallbackPhrase(destination);
        var lead = reason is null ? $"Couldn't open {what}." : $"Couldn't open {what} ({reason}).";
        return copied
            ? $"{lead} Command copied to clipboard: {cmd}"
            : $"{lead} Run: {cmd}";
    }

    // Destination-aware noun phrases. The NewTab wording used to be pinned byte-identical to the retired
    // AppTabLaunch strings, but #589 gave WezTerm/kitty/Zellij a real NewTab ladder (split → tab → window),
    // so a NewTab request is no longer always a literal tab: a Zellij-only session opens an in-session pane,
    // and where the tab rung isn't reachable it falls through to a window. The phrases are therefore
    // deliberately de-pinned to a host-neutral "… where supported" (#591) so the status line never asserts a
    // tab the host didn't open. Opened's parenthetical still names the actual surface via result.LaunchedWith
    // (e.g. "Zellij (new pane)"), which the softened lead no longer contradicts.
    private static string OpeningPhrase(LaunchLocation destination) => destination switch
    {
        LaunchLocation.NewWindow => "a new terminal window",
        LaunchLocation.SplitPane => "a split pane",
        _ => "a new terminal tab where supported",
    };

    private static string OpenedPhrase(LaunchLocation destination) => destination switch
    {
        LaunchLocation.NewWindow => "a new window",
        LaunchLocation.SplitPane => "a split pane",
        _ => "a new tab where supported",
    };

    private static string FallbackPhrase(LaunchLocation destination) => destination switch
    {
        LaunchLocation.NewWindow => "a terminal window",
        LaunchLocation.SplitPane => "a split pane",
        _ => "a terminal tab where supported",
    };
}
