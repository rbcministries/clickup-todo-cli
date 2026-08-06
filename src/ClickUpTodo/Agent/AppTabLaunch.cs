namespace ClickUpTodo.Agent;

/// <summary>
/// The shared, UI-agnostic pieces of the "open this app's task in its own terminal tab" gesture —
/// the dashboard's main-list / Task Detail <c>Ctrl+Enter</c> (#301/#384) and single-task launch
/// mode's <c>Ctrl+Enter</c> (#435). Both hosts build the launcher options and compose the status
/// messages here, so the two paths can't drift; the async launch, the re-entrancy guard and the
/// flash/clipboard sinks stay with each host (they are UI-thread concerns and can't be unit-tested).
/// Pure (primitives in, values out) so every branch is covered without a terminal or a UI host.
/// </summary>
public static class AppTabLaunch
{
    /// <summary>
    /// The launcher options for an app-tab launch: a new tab of the current terminal (falling back to
    /// a new window per emulator support), honouring the Windows preferred terminal and the custom
    /// terminal command (#385). Deliberately <b>not</b>
    /// <see cref="Configuration.AgentDispatchSettings.ToLauncherOptions"/> — <c>ClaudeExecutable</c> and
    /// <c>ExtraArgs</c> are an agent-dispatch concern and don't apply to relaunching this app.
    /// </summary>
    public static TerminalLauncherOptions Options(PreferredTerminal preferred, string? customTerminalCommand) => new()
    {
        LaunchLocation = LaunchLocation.NewTab,
        Preferred = preferred,
        CustomTerminalCommand = TerminalCommandParser.Parse(customTerminalCommand),
    };

    /// <summary>The status flashed while the tab is opening.</summary>
    public static string Opening(string name) => $"Opening '{name}' in a new terminal tab…";

    /// <summary>
    /// The success status once the launcher reports which terminal it used, appending any non-fatal
    /// <see cref="LaunchResult.Note"/> (e.g. a fell-back-to-window notice).
    /// </summary>
    public static string Opened(string name, LaunchResult result)
    {
        var message = $"Opened '{name}' in a new tab ({result.LaunchedWith}).";
        return string.IsNullOrWhiteSpace(result.Note) ? message : $"{message} {result.Note}";
    }

    /// <summary>
    /// The no-terminal fallback status (#301): the exact relaunch command, said to be on the clipboard
    /// when the copy succeeded (<paramref name="copied"/>), else asked to be run by hand.
    /// <paramref name="reason"/> names the failure when the launch threw (vs. simply finding no
    /// emulator to launch).
    /// </summary>
    public static string Fallback(AppLaunchCommand command, bool copied, string? reason = null)
    {
        var cmd = command.ToDisplayCommand();
        var lead = reason is null ? "Couldn't open a terminal tab." : $"Couldn't open a terminal tab ({reason}).";
        return copied
            ? $"{lead} Command copied to clipboard: {cmd}"
            : $"{lead} Run: {cmd}";
    }
}
