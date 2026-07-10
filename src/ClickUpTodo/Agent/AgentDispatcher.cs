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
    private readonly IBackgroundAgentRunner _backgroundRunner;
    private readonly TerminalLauncherOptions _options;
    private readonly string? _promptDirectory;

    public AgentDispatcher(
        ITerminalLauncher launcher,
        TerminalLauncherOptions? options = null,
        string? promptDirectory = null,
        IBackgroundAgentRunner? backgroundRunner = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _backgroundRunner = backgroundRunner ?? new BackgroundAgentRunner();
        _options = options ?? new TerminalLauncherOptions();
        _promptDirectory = promptDirectory;
    }

    /// <summary>
    /// Writes the composed prompt for <paramref name="task"/> to a temp file, then launches a terminal
    /// running <c>claude</c> seeded from it. The prompt content stays in the file (only its path enters
    /// the command), which is what keeps the launch safe (#23). <paramref name="workingDir"/> (the
    /// resolved start directory) and <paramref name="template"/> (a blank value keeps the composer's
    /// <see cref="AgentPromptComposer.DefaultTemplate"/>) are the dispatch-time settings threaded in by
    /// the caller (#91, #100). <paramref name="outputSubdirectory"/> (blank unless the task-derived
    /// working-dir mode is active, #98) fills the template's <c>{outputDirInstruction}</c> with a
    /// "write outputs to <c>./{subdir}</c>" instruction. <paramref name="oneOff"/> selects a one-off
    /// <c>claude -p</c> run over the default interactive session (#94). <paramref name="postToComments"/>
    /// (the #97 toggle) appends an instruction telling the agent to post a summary comment back to the
    /// ClickUp task.
    /// </summary>
    public async Task<AgentDispatchResult> DispatchAsync(
        TaskDetail task,
        IReadOnlyList<CommentItem> comments,
        string userPrompt,
        string? workingDir = null,
        string? template = null,
        string? outputSubdirectory = null,
        bool oneOff = false,
        bool postToComments = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var promptFile = AgentPromptComposer.WritePromptFile(task, comments ?? [], userPrompt, _promptDirectory, template, outputSubdirectory, postToComments);
        var result = await _launcher.LaunchAsync(promptFile, workingDir, _options, oneOff, ct).ConfigureAwait(false);
        return new AgentDispatchResult(result.Success, FormatStatus(task.Name, result), promptFile);
    }

    /// <summary>
    /// Composes the prompt for <paramref name="task"/> exactly as <see cref="DispatchAsync"/> does, then
    /// runs it as a <b>background one-off</b> <c>claude -p</c> child process (#99) via
    /// <see cref="IBackgroundAgentRunner"/> instead of opening a terminal — capturing the output for
    /// rendering in the TUI. All the composition inputs (<paramref name="workingDir"/>,
    /// <paramref name="template"/>, <paramref name="outputSubdirectory"/>, <paramref name="postToComments"/>)
    /// mean the same as on <see cref="DispatchAsync"/>, so a one-off run's prompt is identical to what the
    /// interactive terminal path would have produced. The composed prompt file is fed to the child on
    /// stdin and then <b>deleted</b> once the run finishes (or is cancelled) — the background path owns the
    /// file, unlike the interactive path which retains it for the launched terminal to read.
    /// </summary>
    public async Task<BackgroundRunResult> DispatchBackgroundAsync(
        TaskDetail task,
        IReadOnlyList<CommentItem> comments,
        string userPrompt,
        string? workingDir = null,
        string? template = null,
        string? outputSubdirectory = null,
        bool postToComments = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var promptFile = AgentPromptComposer.WritePromptFile(task, comments ?? [], userPrompt, _promptDirectory, template, outputSubdirectory, postToComments);
        try
        {
            return await _backgroundRunner.RunAsync(promptFile, workingDir, _options, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeletePromptFile(promptFile);
        }
    }

    /// <summary>Best-effort delete of the background path's temp prompt file (it has been read into the
    /// child via stdin); a leftover temp file is harmless, so IO failures are swallowed.</summary>
    private static void TryDeletePromptFile(string promptFile)
    {
        try
        {
            if (File.Exists(promptFile))
                File.Delete(promptFile);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
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
