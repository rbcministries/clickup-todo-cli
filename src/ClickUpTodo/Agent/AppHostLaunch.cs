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

    // Destination-aware noun phrases. NewTab wording is preserved byte-identical to the retired
    // AppTabLaunch strings so today's tab-launching hosts show exactly the same status text.
    private static string OpeningPhrase(LaunchLocation destination) => destination switch
    {
        LaunchLocation.NewWindow => "a new terminal window",
        LaunchLocation.SplitPane => "a split pane",
        _ => "a new terminal tab",
    };

    private static string OpenedPhrase(LaunchLocation destination) => destination switch
    {
        LaunchLocation.NewWindow => "a new window",
        LaunchLocation.SplitPane => "a split pane",
        _ => "a new tab",
    };

    private static string FallbackPhrase(LaunchLocation destination) => destination switch
    {
        LaunchLocation.NewWindow => "a terminal window",
        LaunchLocation.SplitPane => "a split pane",
        _ => "a terminal tab",
    };
}
