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
    /// the caller (#91, #100). <paramref name="oneOff"/> selects a one-off
    /// <c>claude -p</c> run over the default interactive session (#94). <paramref name="postToComments"/>
    /// (the #97 toggle) appends an instruction telling the agent to post a summary comment back to the
    /// ClickUp task. <paramref name="launchLocation"/> (the #275 per-dispatch toggle) overrides where
    /// this one interactive session opens — a new window or a new tab of the current terminal — on top
    /// of the constructor <see cref="TerminalLauncherOptions.LaunchLocation"/> default; null keeps that
    /// default. It only affects interactive launches (a one-off run has no terminal).
    /// <paramref name="windowsTerminalProfile"/> (the #462 match) launches this session under that
    /// Windows Terminal profile (<c>wt … -p</c>) so it inherits the profile's appearance/environment;
    /// blank/null leaves the launch unchanged. <paramref name="providerExecutable"/> /
    /// <paramref name="providerExtraArgs"/> (the #498 per-dispatch provider pick) override the executable
    /// and extra args for this one launch — a Codex dispatch runs <c>codex</c> instead of the default
    /// <c>claude</c>; a blank executable leaves both untouched (the constructor <c>_options</c> default).
    /// </summary>
    public async Task<AgentDispatchResult> DispatchAsync(
        TaskDetail task,
        IReadOnlyList<CommentItem> comments,
        string userPrompt,
        string? workingDir = null,
        string? template = null,
        bool oneOff = false,
        bool postToComments = false,
        LaunchLocation? launchLocation = null,
        string? windowsTerminalProfile = null,
        string? providerExecutable = null,
        IReadOnlyList<string>? providerExtraArgs = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var promptFile = AgentPromptComposer.WritePromptFile(task, comments ?? [], userPrompt, _promptDirectory, template, postToComments);
        // Per-dispatch overrides on this launch's options; each null/blank leaves the settings-derived
        // _options untouched. #275: the launch location. #462: the matched Windows Terminal profile
        // (computed from the resolved directory, so it can't live on the directory-agnostic _options).
        // #498: the picked provider's executable + extra args.
        var options = _options;
        if (launchLocation is { } loc)
            options = options with { LaunchLocation = loc };
        if (!string.IsNullOrWhiteSpace(windowsTerminalProfile))
            options = options with { WindowsTerminalProfile = windowsTerminalProfile };
        options = WithProviderOverride(options, providerExecutable, providerExtraArgs);
        var result = await _launcher.LaunchAsync(promptFile, workingDir, options, oneOff, ct).ConfigureAwait(false);
        return new AgentDispatchResult(result.Success, FormatStatus(task.Name, result), promptFile, result.LaunchedWith);
    }

    /// <summary>
    /// Composes the prompt for <paramref name="task"/> exactly as <see cref="DispatchAsync"/> does, then
    /// runs it as a <b>background one-off</b> <c>claude -p</c> child process (#99) via
    /// <see cref="IBackgroundAgentRunner"/> instead of opening a terminal — capturing the output for
    /// rendering in the TUI. All the composition inputs (<paramref name="workingDir"/>,
    /// <paramref name="template"/>, <paramref name="postToComments"/>)
    /// mean the same as on <see cref="DispatchAsync"/>, so a one-off run's prompt is identical to what the
    /// interactive terminal path would have produced. The composed prompt file is fed to the child on
    /// stdin and then <b>deleted</b> once the run finishes (or is cancelled) — the background path owns the
    /// file, unlike the interactive path which retains it for the launched terminal to read.
    /// <paramref name="progress"/> (#187) receives the incremental display chunks the runner parses from
    /// the stream as it runs, so the caller can paint progress live; its concatenation equals the returned
    /// <see cref="BackgroundRunResult.Output"/>.
    /// </summary>
    public async Task<BackgroundRunResult> DispatchBackgroundAsync(
        TaskDetail task,
        IReadOnlyList<CommentItem> comments,
        string userPrompt,
        string? workingDir = null,
        string? template = null,
        bool postToComments = false,
        IProgress<string>? progress = null,
        string? providerExecutable = null,
        IReadOnlyList<string>? providerExtraArgs = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var promptFile = AgentPromptComposer.WritePromptFile(task, comments ?? [], userPrompt, _promptDirectory, template, postToComments);
        // #498: a one-off run honours the picked provider too — a Codex dispatch's -p run must run codex.
        // A blank executable leaves _options untouched, so every pre-#498 caller is unchanged.
        var options = WithProviderOverride(_options, providerExecutable, providerExtraArgs);
        try
        {
            return await _backgroundRunner.RunAsync(promptFile, workingDir, options, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeletePromptFile(promptFile);
        }
    }

    /// <summary>
    /// Applies the #498 per-dispatch provider pick to a launch's options: when
    /// <paramref name="providerExecutable"/> is non-blank, replaces the executable (trimmed) and the
    /// extra args (trimmed, blanks dropped — the same cleaning <c>AgentDispatchSettings.ToLauncherOptions</c>
    /// applies, so a value that already came through <c>Plan</c> is idempotent). A blank/null executable
    /// is a strict no-op, leaving <paramref name="options"/> as the dispatcher's constructed default so
    /// pre-#498 callers are byte-identical.
    /// </summary>
    private static TerminalLauncherOptions WithProviderOverride(
        TerminalLauncherOptions options, string? providerExecutable, IReadOnlyList<string>? providerExtraArgs)
    {
        if (string.IsNullOrWhiteSpace(providerExecutable))
            return options;
        return options with
        {
            ClaudeExecutable = providerExecutable.Trim(),
            ExtraArgs = [.. (providerExtraArgs ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
        };
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

/// <summary>The outcome of an agent dispatch: whether it launched, the status-line text, the temp
/// prompt-file path (retained for the launched session to read), and the terminal it actually launched
/// with (<see cref="LaunchResult.LaunchedWith"/>, null on failure) — so a caller can tell whether a
/// specific host (e.g. Windows Terminal, for the #462 profile note) was really used.</summary>
public sealed record AgentDispatchResult(bool Success, string StatusMessage, string PromptFilePath, string? LaunchedWith = null);
