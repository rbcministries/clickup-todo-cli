namespace ClickUpTodo.Agent;

/// <summary>The host OS family the launcher targets, resolved once from the runtime.</summary>
public enum OSPlatformKind
{
    Windows,
    MacOS,
    Linux,
    Unknown,
}

/// <summary>
/// A single concrete process to start: an executable plus its argument vector (built as an
/// array, never a concatenated shell string) and the directory to run it in. The launcher tries
/// a planner-ordered list of these until one starts.
/// </summary>
/// <param name="FileName">Executable to run (resolved via <c>PATH</c>).</param>
/// <param name="Arguments">Argument vector, passed verbatim to <c>ProcessStartInfo.ArgumentList</c>.</param>
/// <param name="WorkingDirectory">Directory to start in, or null to inherit the current one.</param>
/// <param name="DisplayName">Human-readable name of the terminal, for status messages.</param>
public sealed record LaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    string DisplayName);

/// <summary>
/// Outcome of a launch attempt, shaped for display in the TUI status line. On success,
/// <see cref="LaunchedWith"/> is the terminal name only; any non-fatal warning (e.g. the configured
/// <c>claude</c> executable wasn't on the current PATH) is carried separately in <see cref="Note"/>.
/// </summary>
public sealed record LaunchResult(bool Success, string? LaunchedWith, string? Error, string? Note = null)
{
    public static LaunchResult Ok(string launchedWith, string? note = null) => new(true, launchedWith, null, note);

    public static LaunchResult Fail(string error) => new(false, null, error);
}
