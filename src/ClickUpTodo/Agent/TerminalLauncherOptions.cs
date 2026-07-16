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
/// Where an interactive dispatch opens the <c>claude</c> session (#255): a
/// <see cref="NewWindow"/> (default, today's behaviour) or a <see cref="NewTab"/> of the terminal the
/// app is already running in, where the host emulator supports it. This only affects the interactive
/// terminal path — one-off <c>claude -p</c> runs go through the background runner with no terminal.
/// </summary>
public enum TerminalLaunchLocation
{
    /// <summary>Open the session in a brand-new terminal window (default; current behaviour).</summary>
    NewWindow,

    /// <summary>
    /// Open the session in a new tab of the running terminal, where the detected host emulator
    /// supports it (Windows Terminal, gnome-terminal, konsole, iTerm2); falls back to a new window
    /// on unsupported hosts (Terminal.app, generic <c>$TERMINAL</c>) or when detection fails.
    /// </summary>
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
    /// Whether an interactive dispatch opens a new window (default) or a new tab of the running
    /// terminal where supported (#255). <see cref="TerminalLaunchLocation.NewTab"/> makes the planner
    /// try a host-specific tab candidate first, keeping the new-window candidate(s) as the fallback.
    /// </summary>
    public TerminalLaunchLocation LaunchLocation { get; init; } = TerminalLaunchLocation.NewWindow;
}
