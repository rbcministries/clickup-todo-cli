namespace ClickUpTodo.Agent;

/// <summary>
/// Runs a <b>one-off</b> <c>claude -p</c> dispatch (#94) as a background child process of the app
/// (#99), capturing its output, instead of opening a visible terminal via <see cref="ITerminalLauncher"/>.
/// This is the seam between <see cref="AgentDispatcher.DispatchBackgroundAsync"/> and the real
/// <see cref="System.Diagnostics.Process"/>; a test double returns scripted output + exit code (and can
/// observe cancellation) so the orchestration is unit-testable without spawning a real <c>claude</c>.
/// </summary>
public interface IBackgroundAgentRunner
{
    /// <summary>
    /// Runs <c>claude -p</c> (plus <see cref="TerminalLauncherOptions.ExtraArgs"/>) with the prompt read
    /// from <paramref name="promptFilePath"/> fed to the child's stdin, in <paramref name="workingDir"/>
    /// (null ⇒ inherit), capturing stdout/stderr.
    /// </summary>
    /// <param name="promptFilePath">Path to the composed prompt file; its content seeds the run.</param>
    /// <param name="workingDir">Directory to run in, or null to inherit the current one.</param>
    /// <param name="options">Launcher configuration (the <c>claude</c> executable + extra args).</param>
    /// <param name="ct">Cancels the run; the implementation kills the child and throws
    /// <see cref="OperationCanceledException"/>.</param>
    /// <returns>The captured outcome (see <see cref="BackgroundRunResult"/>).</returns>
    Task<BackgroundRunResult> RunAsync(
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// The outcome of a background one-off run: whether the child process actually started, its exit code
/// (null when it never started), the captured stdout, and any error text (a start failure message, or
/// captured stderr).
/// </summary>
/// <param name="Started">True when the child process launched (regardless of exit code).</param>
/// <param name="ExitCode">The process exit code, or null when it never started.</param>
/// <param name="Output">Captured stdout (empty if none).</param>
/// <param name="Error">A start-failure message, or captured stderr; null/empty when there was none.</param>
public sealed record BackgroundRunResult(bool Started, int? ExitCode, string Output, string? Error)
{
    /// <summary>True only when the process started and exited cleanly (exit code 0).</summary>
    public bool Success => Started && ExitCode == 0;

    /// <summary>A run that never started (e.g. the <c>claude</c> executable was not found).</summary>
    public static BackgroundRunResult NotStarted(string error) => new(false, null, string.Empty, error);

    /// <summary>A run that started and exited with <paramref name="exitCode"/>.</summary>
    public static BackgroundRunResult Exited(int exitCode, string output, string? error) =>
        new(true, exitCode, output, error);
}
