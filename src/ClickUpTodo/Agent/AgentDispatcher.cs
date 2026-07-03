using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Agent;

/// <summary>
/// Orchestrates agent dispatch (issue #26, S3 of the #23 epic): composes the seed prompt to a temp
/// file (S1 / <see cref="AgentPromptComposer"/>) and launches an interactive <c>claude</c> session in
/// a new terminal from it (S2 / <see cref="ITerminalLauncher"/>), returning a status message shaped
/// for the TUI status line.
/// <para>
/// This is the (pure-ish, unit-testable) seam between the detail-view input and the two already-built
/// halves, keeping <c>TodoApp</c> thin. The launcher is injected; a test double avoids spawning a real
/// process, and <paramref name="promptDirectory"/> lets tests write the prompt file to a scratch dir.
/// </para>
/// </summary>
public sealed class AgentDispatcher
{
    private readonly ITerminalLauncher _launcher;
    private readonly TerminalLauncherOptions _options;
    private readonly string? _promptDirectory;

    public AgentDispatcher(
        ITerminalLauncher launcher,
        TerminalLauncherOptions? options = null,
        string? promptDirectory = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _options = options ?? new TerminalLauncherOptions();
        _promptDirectory = promptDirectory;
    }

    /// <summary>
    /// Writes the composed prompt for <paramref name="task"/> to a temp file, then launches a terminal
    /// running <c>claude</c> seeded from it. The prompt content stays in the file (only its path enters
    /// the command), which is what keeps the launch safe (#23).
    /// </summary>
    public async Task<AgentDispatchResult> DispatchAsync(
        TaskDetail task,
        IReadOnlyList<CommentItem> comments,
        string userPrompt,
        string? workingDir = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var promptFile = AgentPromptComposer.WritePromptFile(task, comments ?? [], userPrompt, _promptDirectory);
        var result = await _launcher.LaunchAsync(promptFile, workingDir, _options, ct).ConfigureAwait(false);
        return new AgentDispatchResult(result.Success, FormatStatus(task.Name, result), promptFile);
    }

    /// <summary>
    /// The status-line text for a launch outcome: success names the terminal and task (with any
    /// non-fatal warning, e.g. <c>claude</c> not on PATH, appended); failure surfaces the error.
    /// </summary>
    public static string FormatStatus(string taskName, LaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success)
            return $"Could not launch Claude: {result.Error}";

        var message = $"Launched Claude ({result.LaunchedWith}) for '{taskName}'.";
        return string.IsNullOrWhiteSpace(result.Note) ? message : $"{message} {result.Note}";
    }
}

/// <summary>The outcome of an agent dispatch: whether it launched, the status-line text, and the temp
/// prompt-file path (retained for the launched session to read).</summary>
public sealed record AgentDispatchResult(bool Success, string StatusMessage, string PromptFilePath);
