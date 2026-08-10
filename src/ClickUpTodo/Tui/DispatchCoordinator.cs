using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.App;

// The static `Application` facade is deprecated in Terminal.Gui 2.4 but remains the supported v2
// pattern; silence the deprecation until the instance-based API stabilizes (mirrors TodoApp).
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// Host-agnostic agent-dispatch orchestration (#345), factored out of <see cref="TodoApp"/> so the
/// dashboard and the single-task launch host (<see cref="SingleTaskApp"/>, #296) drive one code path
/// instead of duplicating ~130 lines of glue. It sequences the already-pure, already-tested resolution
/// helpers — <see cref="AgentDispatchSettings"/>'s working-dir precedence (#91/#95/#96/#98),
/// <see cref="AgentPromptComposer.OutputSubdirectoryToken"/> (#98), and the <see cref="AgentDispatcher"/>
/// launch seam — into the two flows a host runs: an interactive terminal session (#26) and a one-off
/// background <c>claude -p</c> run rendered in an <see cref="AgentRunScreen"/> (#99).
/// <para>
/// <see cref="Plan"/> is a pure lift of the resolution block from <c>TodoApp.DispatchAgent</c> and is
/// unit-tested. The execution methods (<see cref="RunInteractive"/> / <see cref="RunBackground"/>) are
/// the same <see cref="System.Threading.Tasks.Task.Run"/> → launch → <see cref="Application.Invoke"/>
/// glue the inline code used; they aren't CI-testable (Terminal.Gui + real process launch), exactly as
/// before, and stay covered by <c>tui-validate</c> + manual verification. The host-specific bits are
/// three small delegates — <c>report</c>, <c>mount</c>, <c>clearDispatching</c> — over each host's
/// Flash / ShowScreen / re-entrancy guard.
/// </para>
/// </summary>
public static class DispatchCoordinator
{
    /// <summary>
    /// The fully-resolved inputs for one dispatch, plus <see cref="ChosenDir"/> — the tilde-expanded
    /// working-dir pick the per-task cache (#96) reconciles against its
    /// <see cref="DispatchWorkingDirectoryPreFill.AutoDerivedDefault"/> baseline (the host supplies that
    /// baseline to <see cref="ReconcileCache"/>). Pure data — no I/O, no UI.
    /// <para>
    /// <see cref="CreateWorkingDir"/> is the directory-creation flag (#533): true when
    /// <see cref="WorkingDir"/> lies inside the base working-directory tree (inclusive), so the app
    /// creates it before launch — covering the base dir, the <c>{custom-id}</c> subdir, a matched
    /// checkout, and any browsed-to subdir, while never creating a Home/Fixed dir outside the tree nor an
    /// out-of-tree typo. <see cref="WindowsTerminalProfile"/> is the Windows Terminal profile (#462)
    /// whose <c>startingDirectory</c> matched <see cref="WorkingDir"/> when the "Try to use WT profiles"
    /// toggle is on — non-null only for an interactive launch that matched; surfaced via
    /// <see cref="WindowsTerminalProfileNote"/>.
    /// </para>
    /// </summary>
    public readonly record struct ResolvedDispatch(
        string Prompt,
        string? WorkingDir,
        string? Template,
        bool CreateWorkingDir,
        bool OneOff,
        bool PostToComments,
        LaunchLocation LaunchLocation,
        string? ChosenDir,
        string? WindowsTerminalProfile = null);

    /// <summary>
    /// Resolves everything a dispatch needs from the settings + the pane's <paramref name="request"/> —
    /// so both hosts behave identically. An explicit pane pick (#95, now the pre-filled working-dir field
    /// #533) is <c>~</c>-expanded and overrides the configured mode; a blank/cleared field in task-derived
    /// mode resolves to the plain base dir.
    /// <para>
    /// Since #533 this is <b>pure</b> apart from the injected #462 Windows Terminal profile lookup: all
    /// task-directory derivation (the #461 <c>{base}/{Repository}</c> match and the #98
    /// <c>{base}/{custom-id}</c> directory) moved into <see cref="DispatchWorkingDirectoryPreFill"/>, which
    /// pre-fills the field. Plan no longer probes the filesystem for the working directory and no longer
    /// emits an output-subdir instruction. Directory creation is decided by the pure
    /// <see cref="ResolvedDispatch.CreateWorkingDir"/> containment flag (create when inside the base tree),
    /// not by the mode.
    /// </para>
    /// </summary>
    public static ResolvedDispatch Plan(
        AgentDispatchSettings settings,
        DispatchRequest request,
        TaskDetail detail,
        string? defaultWorkingDirectory,
        string home,
        Func<string?>? loadWindowsTerminalSettings = null,
        Func<string, string>? expandEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detail);
        loadWindowsTerminalSettings ??= SystemLoadWindowsTerminalSettings;
        expandEnvironment ??= Environment.ExpandEnvironmentVariables;

        var oneOff = request.SessionMode == AgentSessionMode.OneOff;

        var expandedPick = SettingsForm.ExpandHomePath(request.WorkingDirectory, home);
        var chosenDir = expandedPick.Length == 0 ? null : expandedPick;
        var baseDir = SettingsForm.ResolveDefaultWorkingDirectory(defaultWorkingDirectory, home);

        // A cleared task-derived field resolves to the plain base dir (#533 decision 1): all derivation
        // now lives in the pre-fill, so Plan never touches the filesystem for the working directory.
        var workingDir = settings.ResolveEffectiveWorkingDirectory(chosenDir, taskDerivedDirectory: baseDir, homeDirectory: home);
        var template = settings.PromptTemplate;

        // Create the resolved working dir when it lies inside the base working-directory tree (inclusive):
        // the base dir itself, the {custom-id} subdir, a matched checkout, or a browsed-to subdir — but
        // never a Home/Fixed dir outside the tree, nor a typo outside the tree we own. Pure path
        // containment; no filesystem probe.
        var createWorkingDir = !string.IsNullOrWhiteSpace(workingDir) && IsWithinBaseTree(baseDir, workingDir!);

        // Windows Terminal profile match (#462): only when the toggle is on and this is an interactive
        // launch (a one-off has no terminal) with a resolved directory. The settings.json read happens
        // here (the one I/O seam, via the injected loader) and only under the guard, so every other
        // dispatch reads nothing and behaves byte-identically. Off Windows the default loader finds no
        // settings.json (no %LOCALAPPDATA%) ⇒ null, so the feature is inert without a platform check.
        var wtProfile = settings.TryUseWindowsTerminalProfiles && !oneOff && !string.IsNullOrWhiteSpace(workingDir)
            && loadWindowsTerminalSettings() is { } wtJson
            ? WindowsTerminalProfileMatcher.Match(wtJson, workingDir, expandEnvironment)
            : null;

        return new ResolvedDispatch(
            request.Prompt, workingDir, template, createWorkingDir, oneOff,
            request.PostToComments, request.LaunchLocation, chosenDir, wtProfile);
    }

    /// <summary>True when <paramref name="workingDir"/> is the base working directory or a descendant of
    /// it (the #533 directory-creation rule). Pure path containment via <see cref="Path.GetFullPath(string)"/>;
    /// compared ordinally, matching the per-task cache's case-sensitive convention
    /// (<see cref="DispatchWorkingDirectoryCache"/>). Any malformed path degrades to <c>false</c> (don't
    /// create).</summary>
    private static bool IsWithinBaseTree(string baseDirectory, string workingDir)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return false;
        try
        {
            var fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDir));
            return string.Equals(fullDir, fullBase, StringComparison.Ordinal)
                || fullDir.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    /// <summary>
    /// A status-line suffix naming the Windows Terminal profile (#462) a dispatch launched under, or
    /// <c>null</c> when none matched — so a session that opened under a different profile (font, colours,
    /// tab title) than the user's default isn't unexplained. Gated on <paramref name="launchedWith"/>
    /// actually being a Windows Terminal host: a profile matches on directory alone, but the launch may
    /// have used a non-WT terminal (an explicit <c>PreferredTerminal</c>, a <c>CustomTerminalCommand</c>,
    /// or <c>wt</c> absent) or failed outright (<paramref name="launchedWith"/> null) — in which case
    /// the profile never applied and claiming it would mislead. Pure/testable; the interactive host
    /// passes <see cref="AgentDispatchResult.LaunchedWith"/>.
    /// </summary>
    public static string? WindowsTerminalProfileNote(ResolvedDispatch plan, string? launchedWith)
        => plan.WindowsTerminalProfile is { } profile
            && launchedWith is { } host
            && host.StartsWith("Windows Terminal", StringComparison.Ordinal)
            ? $" (Windows Terminal profile '{profile}'.)"
            : null;

    /// <summary>The real-filesystem default for <see cref="Plan"/>'s #462 WT-profile match: reads the
    /// first existing Windows Terminal <c>settings.json</c>, or <c>null</c> (no file, off Windows, or an
    /// unreadable file) so a missing/broken settings degrades to "no profile", never a thrown dispatch.</summary>
    private static string? SystemLoadWindowsTerminalSettings()
        => WindowsTerminalSettings.Load(Environment.GetEnvironmentVariable, File.Exists, File.ReadAllText);

    /// <summary>
    /// Reconciles the per-task working-dir cache (#96) after a dispatch, mutating
    /// <paramref name="cache"/> in place and returning <c>true</c> only when it changed (so the host
    /// persists <c>config.json</c> exactly when needed). Thin wrapper over the already-tested
    /// <see cref="DispatchWorkingDirectoryCache.Update"/>. <paramref name="chosenDir"/> is
    /// <see cref="ResolvedDispatch.ChosenDir"/>; <paramref name="resolvedDefault"/> is the host's
    /// <see cref="DispatchWorkingDirectoryPreFill.AutoDerivedDefault"/> baseline — the value an
    /// accepted-unchanged pre-fill produces, so accepting it clears rather than stores a #96 entry. It is
    /// passed in rather than computed here because it may require the filesystem (the #461 repo match),
    /// which Plan no longer touches (#533).
    /// </summary>
    public static bool ReconcileCache(
        IDictionary<string, string> cache, string taskId, string? chosenDir, string? resolvedDefault)
        => DispatchWorkingDirectoryCache.Update(cache, taskId, chosenDir, resolvedDefault);

    /// <summary>
    /// Launches an interactive terminal session for <paramref name="detail"/> off the UI thread, then
    /// reports the outcome status text through <paramref name="report"/> (invoked on the UI thread). The
    /// host's <paramref name="report"/> clears its re-entrancy guard and flashes the message. A launch
    /// into the base working-directory tree (the base dir, the {custom-id} subdir, a matched checkout)
    /// creates that directory on first use (#533) before launching. Must be called on the UI thread (the
    /// resolution already happened in <see cref="Plan"/>).
    /// </summary>
    public static void RunInteractive(
        AgentDispatcher agent,
        TaskDetail detail,
        IReadOnlyList<CommentItem> comments,
        ResolvedDispatch plan,
        Action<string> report)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Create the working dir on first use when it lies inside the base tree (#533) so
                // Process.Start doesn't fail on a not-yet-existing path (e.g. the {custom-id} subdir). A
                // Home/Fixed dir or an out-of-tree pick is the user's own and isn't created here.
                if (plan.CreateWorkingDir && !string.IsNullOrWhiteSpace(plan.WorkingDir))
                    Directory.CreateDirectory(plan.WorkingDir);

                var result = await agent.DispatchAsync(
                    detail, comments, plan.Prompt, plan.WorkingDir, plan.Template,
                    plan.OneOff, plan.PostToComments, launchLocation: plan.LaunchLocation,
                    windowsTerminalProfile: plan.WindowsTerminalProfile);
                // Tell the user when a #462 WT profile match launched the session under a non-default
                // profile — otherwise a launch that looked different than expected is unexplained. The
                // #461 Repository match no longer needs a note: the directory it chose is now visible in
                // the pre-filled working-dir field, which explains itself (#533 decision 5).
                var status = result.StatusMessage + (WindowsTerminalProfileNote(plan, result.LaunchedWith) ?? "");
                Application.Invoke(() => report(status));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => report($"Could not launch Claude: {Short(ex)}"));
            }
        });
    }

    /// <summary>
    /// Runs a one-off <c>claude -p</c> dispatch (#99) as a background child process: creates the
    /// <see cref="AgentRunScreen"/> + its <see cref="CancellationTokenSource"/>, hands the screen to the
    /// host via <paramref name="mount"/> (the host's <c>ShowScreen(screen, onClosed)</c> — the
    /// <c>onClosed</c> cancels/releases the token source), then runs the dispatch off the UI thread and
    /// marshals streamed output / result / cancellation back to the screen. <paramref name="clearDispatching"/>
    /// resets the host's re-entrancy guard. Must be called on the UI thread.
    /// </summary>
    public static void RunBackground(
        AgentDispatcher agent,
        TaskDetail detail,
        IReadOnlyList<CommentItem> comments,
        ResolvedDispatch plan,
        Action<AgentRunScreen, Action> mount,
        Action clearDispatching)
    {
        var cts = new CancellationTokenSource();
        var screen = new AgentRunScreen(detail.Name);
        screen.CancelRequested += (_, _) => cts.Cancel();
        // Closing the screen (Esc after it finished) cancels any straggler and releases the token source.
        mount(screen, () =>
        {
            cts.Cancel();
            cts.Dispose();
        });

        // Stream the parsed output into the run screen as it arrives (#187): the runner reports display
        // chunks off the UI thread, marshalled onto the UI thread before appending.
        var progress = new DelegateProgress<string>(chunk => Application.Invoke(() => screen.AppendOutput(chunk)));

        _ = Task.Run(async () =>
        {
            try
            {
                // Create the working dir on first use when it lies inside the base tree (#533), same as
                // the interactive path, so the child process doesn't fail on a not-yet-existing path.
                if (plan.CreateWorkingDir && !string.IsNullOrWhiteSpace(plan.WorkingDir))
                    Directory.CreateDirectory(plan.WorkingDir);

                var run = await agent.DispatchBackgroundAsync(
                    detail, comments, plan.Prompt, plan.WorkingDir, plan.Template,
                    plan.PostToComments, progress, cts.Token);
                Application.Invoke(() => { clearDispatching(); screen.ShowResult(AgentRunModel.FormatOutput(run), run.Success); });
            }
            catch (OperationCanceledException)
            {
                Application.Invoke(() => { clearDispatching(); screen.ShowCancelled("Run cancelled — the Claude process was stopped."); });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => { clearDispatching(); screen.ShowResult($"Could not run Claude: {Short(ex)}", success: false); });
            }
        });
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
