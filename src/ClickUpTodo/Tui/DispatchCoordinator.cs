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
    /// The fully-resolved inputs for one dispatch, plus the two values
    /// (<see cref="ChosenDir"/>/<see cref="ResolvedDefault"/>) the per-task working-dir cache (#96)
    /// needs to reconcile. Pure data — no I/O, no UI. <see cref="RepositoryDir"/> is the
    /// <c>{base}/{Repository}</c> checkout sub-dir a task-derived launch matched into (#461), non-null
    /// only when the match actually drove <see cref="WorkingDir"/> (task-derived mode, no explicit pick);
    /// it is surfaced to the user via <see cref="RepositoryMatchNote"/>.
    /// </summary>
    public readonly record struct ResolvedDispatch(
        string Prompt,
        string? WorkingDir,
        string? OutputSubdir,
        string? Template,
        bool UseTaskDerived,
        bool OneOff,
        bool PostToComments,
        LaunchLocation LaunchLocation,
        string? ChosenDir,
        string? ResolvedDefault,
        string? RepositoryDir = null);

    /// <summary>
    /// Resolves everything a dispatch needs from the settings + the pane's <paramref name="request"/> —
    /// a verbatim lift of the resolution block in <c>TodoApp.DispatchAgent</c> so both hosts behave
    /// identically. An explicit pane pick (#95) is <c>~</c>-expanded and overrides the configured mode;
    /// task-derived mode without a pick seeds a per-task <c>./{custom-id}</c> output subdir (#98).
    /// <para>
    /// In task-derived mode it also looks for a <c>{base}/{Repository}</c> checkout sub-dir (#461): when
    /// the task carries a <c>Repository</c> custom field naming a direct child of the base dir, the launch
    /// starts <i>inside that checkout</i> and the <c>./{custom-id}</c> output-subdir instruction is
    /// <b>suppressed</b> (owner decision — work belongs in the project, not a scratch folder in its tree).
    /// The base-dir-creation flag (<see cref="ResolvedDispatch.UseTaskDerived"/>) is unchanged by a match;
    /// only the output-subdir emission and the resolved directory are. This is the one place the resolution
    /// consults the filesystem, via the injected <paramref name="directoryExists"/> /
    /// <paramref name="childDirectoryNames"/> probes (default: the real filesystem), and only when a
    /// <c>Repository</c> value is present — so a task without one stays pure and byte-identical to before.
    /// </para>
    /// </summary>
    public static ResolvedDispatch Plan(
        AgentDispatchSettings settings,
        DispatchRequest request,
        TaskDetail detail,
        string? defaultWorkingDirectory,
        string home,
        Func<string, bool>? directoryExists = null,
        Func<string, IReadOnlyList<string>>? childDirectoryNames = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detail);
        directoryExists ??= Directory.Exists;
        childDirectoryNames ??= SystemChildDirectoryNames;

        var oneOff = request.SessionMode == AgentSessionMode.OneOff;

        var expandedPick = SettingsForm.ExpandHomePath(request.WorkingDirectory, home);
        var chosenDir = expandedPick.Length == 0 ? null : expandedPick;
        var baseDir = SettingsForm.ResolveDefaultWorkingDirectory(defaultWorkingDirectory, home);

        // A `Repository`-field match refines the task-derived candidate to a checkout sub-dir (#461); it
        // only applies in task-derived mode (Home/Fixed ignore the candidate). Computed even when a pick
        // is present so `resolvedDefault` reflects it — an explicit pick equal to the matched dir must
        // still count as "reverted to default" for the #96 cache.
        var repoMatch = settings.WorkingDirectory == AgentWorkingDirectory.TaskDerived
            ? RepositoryWorkingDirectory.Resolve(detail, baseDir, directoryExists, childDirectoryNames)
            : null;
        var taskDerived = repoMatch?.Directory ?? baseDir;

        var workingDir = settings.ResolveEffectiveWorkingDirectory(chosenDir, taskDerivedDirectory: taskDerived, homeDirectory: home);
        var useTaskDerived = settings.UsesTaskDerivedOutput(chosenDir);
        // Emit the per-task output subdir only in task-derived mode with no pick AND no repo match: on a
        // match the session is already in the checkout, so `./{custom-id}` would litter the working tree.
        var outputSubdir = useTaskDerived && repoMatch is null ? AgentPromptComposer.OutputSubdirectoryToken(detail) : null;
        var template = settings.PromptTemplate;

        var resolvedDefaultRaw = settings.ResolveWorkingDirectory(taskDerivedDirectory: taskDerived, homeDirectory: home);
        var resolvedDefault = resolvedDefaultRaw is null ? null : SettingsForm.ExpandHomePath(resolvedDefaultRaw, home);

        // The match "applied" (drove the working dir, so it's worth reporting) only when it exists and no
        // explicit pick overrode it.
        var repositoryDir = repoMatch is { } m && chosenDir is null ? m.Directory : null;

        return new ResolvedDispatch(
            request.Prompt, workingDir, outputSubdir, template, useTaskDerived, oneOff,
            request.PostToComments, request.LaunchLocation, chosenDir, resolvedDefault, repositoryDir);
    }

    /// <summary>
    /// A status-line suffix naming the <c>{base}/{Repository}</c> checkout a task-derived launch matched
    /// into (#461), or <c>null</c> when no repo match drove the working directory — so the user can tell
    /// why the session opened where it did (a silent directory change is unexplainable). Pure/testable;
    /// the interactive host appends it to <see cref="AgentDispatchResult.StatusMessage"/>.
    /// </summary>
    public static string? RepositoryMatchNote(ResolvedDispatch plan)
        => plan.RepositoryDir is { } dir
            ? $" (Working in {dir} — matched by the task's {RepositoryWorkingDirectory.FieldName} field.)"
            : null;

    /// <summary>The immediate child <b>directory</b> names of <paramref name="dir"/> (the real-filesystem
    /// default for <see cref="Plan"/>'s case-insensitive repo scan); empty when the dir is missing or
    /// unreadable, so a filesystem hiccup degrades to "no match", never a thrown dispatch.</summary>
    private static IReadOnlyList<string> SystemChildDirectoryNames(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return [];
            var names = new List<string>();
            foreach (var path in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
            return names;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Reconciles the per-task working-dir cache (#96) after a dispatch, mutating
    /// <paramref name="cache"/> in place and returning <c>true</c> only when it changed (so the host
    /// persists <c>config.json</c> exactly when needed). Thin wrapper over the already-tested
    /// <see cref="DispatchWorkingDirectoryCache.Update"/>, sourced from the plan.
    /// </summary>
    public static bool ReconcileCache(IDictionary<string, string> cache, string taskId, ResolvedDispatch plan)
        => DispatchWorkingDirectoryCache.Update(cache, taskId, plan.ChosenDir, plan.ResolvedDefault);

    /// <summary>
    /// Launches an interactive terminal session for <paramref name="detail"/> off the UI thread, then
    /// reports the outcome status text through <paramref name="report"/> (invoked on the UI thread). The
    /// host's <paramref name="report"/> clears its re-entrancy guard and flashes the message. A
    /// task-derived launch creates its base dir on first use (#98) before launching. Must be called on
    /// the UI thread (the resolution already happened in <see cref="Plan"/>).
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
                // A task-derived launch starts in the base dir; create it on first use (#98) so
                // Process.Start doesn't fail on a not-yet-existing path. Home/Fixed dirs and an explicit
                // pane pick are the user's own and aren't created here.
                if (plan.UseTaskDerived && !string.IsNullOrWhiteSpace(plan.WorkingDir))
                    Directory.CreateDirectory(plan.WorkingDir);

                var result = await agent.DispatchAsync(
                    detail, comments, plan.Prompt, plan.WorkingDir, plan.Template, plan.OutputSubdir,
                    plan.OneOff, plan.PostToComments, launchLocation: plan.LaunchLocation);
                // Tell the user when a #461 Repository match opened the session in a checkout sub-dir —
                // otherwise a launch that landed somewhere other than the base dir looks unexplained.
                var status = result.StatusMessage + (RepositoryMatchNote(plan) ?? "");
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
                // A task-derived launch starts in the base dir; create it on first use (#98), same as the
                // interactive path, so the child process doesn't fail on a not-yet-existing path.
                if (plan.UseTaskDerived && !string.IsNullOrWhiteSpace(plan.WorkingDir))
                    Directory.CreateDirectory(plan.WorkingDir);

                var run = await agent.DispatchBackgroundAsync(
                    detail, comments, plan.Prompt, plan.WorkingDir, plan.Template, plan.OutputSubdir,
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
