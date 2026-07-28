namespace ClickUpTodo.Agent;

/// <summary>Which terminal to prefer on Windows; <see cref="Auto"/> uses the full fallback chain.</summary>
public enum PreferredTerminal
{
    Auto,
    WindowsTerminal,
    Pwsh,
    PowerShell,

    /// <summary>
    /// Open a new window via <c>cmd /c start</c> hosting a PowerShell process (the last-resort
    /// fallback from #45). Requires a PowerShell host to be present, since the payload runs there.
    /// </summary>
    Cmd,
}

/// <summary>
/// Where an interactive dispatch's <c>claude</c> session opens (issue #255): a
/// <see cref="NewWindow"/> (the historical default) or a <see cref="NewTab"/> of the terminal the
/// app is already running in, where the host supports it. New-tab is best-effort and detection-gated
/// per emulator; when the host isn't a supported/detected one it falls back to a new window. It only
/// applies to interactive sessions — a one-off <c>claude -p</c> runs through the background runner
/// with no terminal, so "new tab" is meaningless there.
/// </summary>
public enum LaunchLocation
{
    /// <summary>Open the session in a new terminal window (default; today's behavior).</summary>
    NewWindow,

    /// <summary>Open the session in a new tab of the current terminal where supported, else a window.</summary>
    NewTab,
}

/// <summary>
/// Configuration for <see cref="ITerminalLauncher"/>. Intentionally lean for this slice (issue #25):
/// it must work with zero config (all defaults). The full settings surface — preferred terminal,
/// custom <c>claude</c> path/args, working directory, prompt-template — is wired to
/// <c>AppConfig</c> and the F2 dialog in #27 (S4); this record is the seam it will populate.
/// </summary>
public sealed record TerminalLauncherOptions
{
    /// <summary>The <c>claude</c> executable to invoke in the new terminal (looked up on its PATH).</summary>
    public string ClaudeExecutable { get; init; } = "claude";

    /// <summary>Extra arguments inserted before the prompt argument (e.g. a model flag).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Preferred terminal on Windows; ignored on other platforms.</summary>
    public PreferredTerminal Preferred { get; init; } = PreferredTerminal.Auto;

    /// <summary>
    /// A user-configured terminal launch command as a tokenised argv (#385): the emulator executable
    /// followed by its flags, with a <see cref="TerminalCommandParser.Placeholder"/> token marking
    /// where the OS host invocation of the command is spliced in (appended if the token is absent).
    /// Empty ⇒ no custom command (auto-detection only). When set and its executable is on PATH, the
    /// planner emits it as the first launch candidate on every platform, ahead of the built-in chain;
    /// otherwise it is skipped, so an unset or unavailable command is a strict no-op.
    /// </summary>
    public IReadOnlyList<string> CustomTerminalCommand { get; init; } = [];

    /// <summary>
    /// Where an interactive session opens (#255): a new window (default) or a new tab of the current
    /// terminal where the host supports it. Ignored for one-off runs (which have no terminal).
    /// </summary>
    public LaunchLocation LaunchLocation { get; init; } = LaunchLocation.NewWindow;
}
