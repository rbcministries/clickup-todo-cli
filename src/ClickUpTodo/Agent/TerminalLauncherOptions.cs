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
}
