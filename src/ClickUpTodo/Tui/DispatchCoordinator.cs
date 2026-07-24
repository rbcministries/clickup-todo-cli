using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
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
    /// needs to reconcile. Pure data — no I/O, no UI.
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
        string? ResolvedDefault);

    /// <summary>
    /// Resolves everything a dispatch needs from the settings + the pane's <paramref name="request"/> —
    /// a verbatim lift of the resolution block in <c>TodoApp.DispatchAgent</c> so both hosts behave
    /// identically. An explicit pane pick (#95) is <c>~</c>-expanded and overrides the configured mode;
    /// task-derived mode without a pick seeds a per-task <c>./{custom-id}</c> output subdir (#98). Pure:
    /// no mutation, no I/O — unit-testable.
    /// </summary>
    public static ResolvedDispatch Plan(
        AgentDispatchSettings settings,
        DispatchRequest request,
        TaskDetail detail,
        string? defaultWorkingDirectory,
        string home)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detail);

        var oneOff = request.SessionMode == AgentSessionMode.OneOff;

        var expandedPick = SettingsForm.ExpandHomePath(request.WorkingDirectory, home);
        var chosenDir = expandedPick.Length == 0 ? null : expandedPick;
        var baseDir = SettingsForm.ResolveDefaultWorkingDirectory(defaultWorkingDirectory, home);
        var workingDir = settings.ResolveEffectiveWorkingDirectory(chosenDir, taskDerivedDirectory: baseDir, homeDirectory: home);
        var useTaskDerived = settings.UsesTaskDerivedOutput(chosenDir);
        var outputSubdir = useTaskDerived ? AgentPromptComposer.OutputSubdirectoryToken(detail) : null;
        var template = settings.PromptTemplate;

        var resolvedDefaultRaw = settings.ResolveWorkingDirectory(taskDerivedDirectory: baseDir, homeDirectory: home);
        var resolvedDefault = resolvedDefaultRaw is null ? null : SettingsForm.ExpandHomePath(resolvedDefaultRaw, home);

        return new ResolvedDispatch(
            request.Prompt, workingDir, outputSubdir, template, useTaskDerived, oneOff,
            request.PostToComments, request.LaunchLocation, chosenDir, resolvedDefault);
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
                Application.Invoke(() => report(result.StatusMessage));
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
