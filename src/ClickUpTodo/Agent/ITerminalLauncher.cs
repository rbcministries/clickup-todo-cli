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
    /// <param name="oneOff">
    /// When true, launch a one-off <c>claude -p "…"</c> run (that executes and exits) instead of an
    /// interactive session (#94). Default false preserves the interactive behaviour.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="LaunchResult"/> reporting success (and which terminal was used) or a failure
    /// message suitable for the TUI status line.
    /// </returns>
    Task<LaunchResult> LaunchAsync(
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options,
        bool oneOff = false,
        CancellationToken ct = default);

    /// <summary>
    /// Launch a terminal running <b>this app</b> for a single task — <c>clickup-todo --task &lt;id&gt;</c>
    /// (#301) — in a new tab/window per <paramref name="options"/>. Unlike <see cref="LaunchAsync"/> there
    /// is no prompt file: the command is a plain executable invocation.
    /// <para>
    /// A default-throwing member (mirroring the repo's default-throwing interface members) so existing
    /// test doubles that implement only <see cref="LaunchAsync"/> keep compiling; the real
    /// <see cref="TerminalLauncher"/> overrides it.
    /// </para>
    /// </summary>
    Task<LaunchResult> LaunchAppAsync(
        AppLaunchCommand command,
        TerminalLauncherOptions options,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This ITerminalLauncher does not support launching the app in a new terminal.");
}
