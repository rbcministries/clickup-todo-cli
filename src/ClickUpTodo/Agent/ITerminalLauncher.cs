namespace ClickUpTodo.Agent;

/// <summary>
/// Opens a new terminal tab/window running an interactive <c>claude</c> session seeded from a
/// prompt <b>file</b> (written by the composer, #24). The prompt content stays in the file and only
/// the file path enters the command, which is what keeps launching safe across platforms (#23).
/// </summary>
public interface ITerminalLauncher
{
    /// <summary>
    /// Launch a terminal running <c>claude</c> seeded from <paramref name="promptFilePath"/>.
    /// </summary>
    /// <param name="promptFilePath">Path to the temp file holding the composed prompt.</param>
    /// <param name="workingDir">Directory to start the session in, or null to inherit.</param>
    /// <param name="options">Launcher configuration (zero-config defaults are valid).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="LaunchResult"/> reporting success (and which terminal was used) or a failure
    /// message suitable for the TUI status line.
    /// </returns>
    Task<LaunchResult> LaunchAsync(
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options,
        CancellationToken ct = default);
}
