using System.Diagnostics;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui 2.4 deprecates the static `Application` facade in favour of an instance-based
// API that is not yet stable or documented. The static API remains the supported v2 pattern,
// so we intentionally use it and silence the deprecation here until the instance API settles
// (mirrors TodoApp).
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// Minimal host that boots straight into one task's <see cref="TaskDetailScreen"/> — the single-task
/// launch mode (<c>--task &lt;id&gt;</c>, #296; multi-tab epic #292, sub-issue 4). It builds only the
/// service graph a single-task tab needs (the injected <see cref="TaskService"/> for the one task) and
/// never constructs the dashboard's working-set list or its refresh loop.
/// <para>
/// It is intentionally <b>not</b> the dashboard's <see cref="TodoApp"/> in a "no-list" mode: the
/// dashboard's <c>ShowScreen</c>/<c>CloseScreen</c> seam is load-bearing behind the single-sectioned
/// <c>ListView</c> invariant (#3/#38), and bending it to tolerate an absent list root would risk
/// regressing the main list. This host mounts the already-decoupled detail screen (plain data +
/// injected async write callbacks + events — it never touches a list/selection) as its own root, so it
/// has zero blast radius on the dashboard.
/// </para>
/// <para>
/// Polling comes for free: <see cref="TaskDetailScreen"/> owns its own 30s auto-refresh timer
/// (<c>OnShown</c> → <c>Application.AddTimeout</c> → <see cref="TaskDetailScreen.RefreshRequested"/>),
/// so wiring that event to a refetch of the one task satisfies "poll only the launch task on cadence"
/// without a second <c>RefreshService</c> or any working-set fetch.
/// </para>
/// </summary>
public sealed class SingleTaskApp
{
    private readonly TaskService _tasks;
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly IBrowserLauncher _browser;
    private readonly string _taskId;

    // The task/comments last shown. _task is read on the UI thread when launching the browser (Ctrl+B),
    // so it reflects any refresh since launch; both are seeded from the initial fetch and replaced by
    // UpdateData on each refresh.
    private TaskDetail _task;
    private IReadOnlyList<CommentItem> _comments;

    private Window _window = null!;
    // The shared status + contextual help footer (#346). Built in Build.
    private ContextualFooter _footer = null!;
    private TaskDetailScreen _detail = null!;

    // Screens stacked over the root detail: Help (F1), a one-off agent run (#345), and the exit
    // confirmation (#299). Empty ⇒ the detail is front-most.
    private readonly List<Screen> _stack = [];

    // Coalesces overlapping refreshes (F5/Ctrl+R racing the 30s tick): skip a tick while one is in flight
    // so an earlier fetch can't land after a later one with stale data. UI-thread-only, like TodoApp's.
    private bool _refreshing;

    // Composes the seed prompt + launches a `claude` session for the detail view's Ctrl+A dispatch
    // (#345). Built once in Build() from the persisted AgentDispatch settings (#91) — single-task mode
    // has no F2 settings dialog, so unlike TodoApp it never needs rebuilding.
    private AgentDispatcher _agent = null!;
    // True while a dispatch is in flight, so a rapid second submit doesn't launch a duplicate session.
    // UI-thread-only (set in DispatchAgent, cleared via Application.Invoke) — mirrors TodoApp.
    private bool _dispatching;

    private string _status;

    public SingleTaskApp(TaskService tasks, AppConfig config, ConfigStore configStore, TaskDetail task,
        IReadOnlyList<CommentItem> comments, IBrowserLauncher? browserLauncher = null)
    {
        _tasks = tasks;
        _config = config;
        _configStore = configStore;
        _browser = browserLauncher ?? new SystemBrowserLauncher();
        _task = task;
        _comments = comments;
        _taskId = task.Id;
        _status = $"Loaded: {task.Name}";
    }

    private Screen ActiveScreen => _stack.Count > 0 ? _stack[^1] : _detail;

    public void Run(string? driverName = null)
    {
        // Install the frame-diffing ANSI output for the default/ansi driver, exactly as the dashboard
        // does (#H ~0.9 KB per keypress instead of a full repaint on slow terminals/links). Best-effort;
        // CLICKUP_TODO_NO_DIFF=1 opts out.
        var diffing = (driverName is null or "ansi")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLICKUP_TODO_NO_DIFF"))
            && DiffFlushAnsiBackend.TryInstall();
        Application.Init(driverName);
        try
        {
            _status = $"{_status} (driver: {driverName ?? "default (ansi)"}{(diffing ? ", diffed output" : "")})";
            Build();
            Application.Run(_window);
        }
        finally
        {
            // Shutdown restores the terminal no matter how Dispose fares, so it must run even if the
            // shared teardown guard swallows Terminal.Gui 2.4.10's tabbed-view dispose bug (#346).
            try
            {
                TuiTeardown.DisposeSwallowingTeardownBug(_window, "Window");
            }
            finally
            {
                Application.Shutdown();
            }
        }
    }

    private void Build()
    {
        _window = new Window { Title = AppBranding.WindowTitle(_config.WorkspaceName) };

        // Build the agent dispatcher from the persisted settings (#91), same as the dashboard's
        // BuildAgentDispatcher — the preferred terminal / claude path / launch-location default apply.
        _agent = new AgentDispatcher(new TerminalLauncher(), _config.AgentDispatch.ToLauncherOptions());

        // Root the Dispatch pane's working-dir browser at the saved base dir (#92), falling back to home
        // if it doesn't exist yet — same resolution the dashboard uses when opening Task Detail.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDir = SettingsForm.ResolveDefaultWorkingDirectory(_config.DefaultWorkingDirectory, home);
        var browserRoot = Directory.Exists(baseDir) ? baseDir : home;

        _detail = new TaskDetailScreen(
            _task, _comments, browserRoot,
            settings: _config.DetailView,
            defaultSessionMode: _config.AgentDispatch.DefaultSessionMode,
            defaultPostToComments: _config.AgentDispatch.DefaultPostResultsToComments,
            defaultLaunchLocation: _config.AgentDispatch.LaunchLocation,
            workingDirectoryPreFill: () => DispatchWorkingDirectoryCache.PreFill(_config.TaskWorkingDirectories, _taskId),
            // Ctrl+N posts a plain-text comment; Ctrl+E edits the description — same injected-async seam
            // the dashboard wires, so both work identically in a single-task tab.
            postCommentAsync: (text, ct) => _tasks.CreateTaskCommentAsync(_taskId, text, ct),
            setDescriptionAsync: (text, ct) => _tasks.SetTaskDescriptionAsync(_taskId, text, ct));

        // F5 / Ctrl+R and the screen's own 30s tick ask for fresh data — refetch just this one task.
        _detail.RefreshRequested += (_, _) => RefreshTask();
        // Ctrl+B sets OpenBrowserRequested then closes; Esc closes directly. The detail is the launch-task
        // root (#298), so its close is back-at-root: a plain Esc hands off to the exit seam (RequestExit),
        // which asks for confirmation (#299) before quitting the tab. Ctrl+B is deliberately *not*
        // confirmed: "open this task in the browser and close the tab" is an explicit, unambiguous
        // request, not the ambiguous Esc that #290 flagged and #299 guards. Only fires while the detail is
        // front-most; with an overlay up (incl. that confirmation), Esc goes to the overlay.
        _detail.Closed += (_, _) =>
        {
            if (_detail.OpenBrowserRequested)
            {
                LaunchBrowser(_task.Url);
                Application.RequestStop();
                return;
            }

            RequestExit();
        };
        // Quick Updates stays deferred in single-task mode: it needs sub-issue (5) #297 to decouple its
        // write path from the dashboard's working-set snapshot. Flash rather than silently no-op so the
        // gap is legible.
        _detail.QuickUpdatesRequested += (_, _) =>
            Flash("Quick Updates isn't available in single-task mode yet (tracked on #297).");
        // Agent dispatch (Ctrl+A) now runs through the shared DispatchCoordinator (#345), so a
        // single-task tab composes + launches a session with the dashboard's exact working-dir /
        // post-to-Comments / launch-location semantics.
        _detail.AgentDispatchRequested += (_, request) => DispatchAgent(request);
        _detail.FlashRequested += (_, message) => Flash(message);
        _detail.HelpRequested += (_, _) => OpenHelp();

        _footer = new ContextualFooter(_status);

        _window.Add(_detail);
        _footer.AddTo(_window);
        // Re-fit the contextual footer whenever the window re-lays out (terminal resize); the text is
        // only reassigned when it changes, so this can't loop (mirrors TodoApp).
        _window.SubViewsLaidOut += (_, _) => UpdateHelpLine();
        UpdateHelpLine();
        _detail.OnShown();
    }

    /// <summary>Re-fetches this task's detail + comments off the UI thread and feeds them back in.</summary>
    private void RefreshTask()
    {
        // Only refresh while the detail is front-most (e.g. not while Help is stacked over it): no point
        // spending a round-trip on a hidden view. The next tick refreshes once it's back on top.
        if (!ReferenceEquals(ActiveScreen, _detail))
            return;
        if (_refreshing)
            return;
        _refreshing = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailAsync(_taskId);
                var comments = await _tasks.GetTaskCommentsWithRepliesAsync(_taskId);
                Application.Invoke(() =>
                {
                    _task = detail;
                    _comments = comments;
                    _detail.UpdateData(detail, comments);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh task: {ErrorText.Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() => _refreshing = false);
            }
        });
    }

    // ── Agent dispatch (Ctrl+A) ───────────────────────────────────────────────

    /// <summary>
    /// Ctrl+A from a single-task tab: dispatches an agent for the launch task through the shared
    /// <see cref="DispatchCoordinator"/> (#345) — an interactive terminal session or, in one-off mode
    /// (#94), a background <c>claude -p</c> run rendered in an <see cref="Screens.AgentRunScreen"/> — with
    /// the same working-dir (#91/#95/#96/#98), post-to-Comments (#97), and launch-location (#275)
    /// semantics as the dashboard. Runs on the UI thread (invoked from the detail screen's key handler).
    /// </summary>
    private void DispatchAgent(DispatchRequest request)
    {
        // Re-entrancy guard: a second submit before the first launch finishes would spawn a duplicate
        // session. UI-thread-only, cleared back on the UI thread — no locking needed (mirrors TodoApp).
        if (_dispatching)
        {
            Flash("A Claude session is already launching…");
            return;
        }
        _dispatching = true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plan = DispatchCoordinator.Plan(_config.AgentDispatch, request, _task, _config.DefaultWorkingDirectory, home);

        // Persist an explicit non-default working-dir pick for this task (#96) so the next dispatch
        // pre-fills it; reverting to the default clears the entry. Save only when the cache changed.
        if (DispatchCoordinator.ReconcileCache(_config.TaskWorkingDirectories, _taskId, plan))
            _configStore.Save(_config);

        // One-off mode runs as a background child with output in a screen (#99); interactive opens a
        // real terminal below (an interactive session needs a live TTY). The run screen mounts through
        // this host's own ShowScreen (Esc cancels/closes it back to the detail root).
        if (plan.OneOff)
        {
            DispatchCoordinator.RunBackground(
                _agent, _task, _comments, plan,
                mount: (screen, onClosed) => ShowScreen(screen, onClosed),
                clearDispatching: () => _dispatching = false);
            return;
        }

        Flash($"Launching Claude for '{_task.Name}'…");
        DispatchCoordinator.RunInteractive(
            _agent, _task, _comments, plan,
            report: message => { _dispatching = false; Flash(message); });
    }

    // ── Help overlay (F1) ────────────────────────────────────────────────────

    /// <summary>F1 stacks a <see cref="HelpScreen"/> over the detail; Esc pops back to it.</summary>
    private void OpenHelp()
    {
        if (ActiveScreen is HelpScreen)
            return;
        ShowScreen(new HelpScreen());
    }

    private void ShowScreen(Screen screen, Action? onClosed = null)
    {
        // Hide the currently-visible layer so only the new top draws/focuses (one visible screen — #3).
        (ActiveScreen as View).Visible = false;
        _stack.Add(screen);

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!_stack.Contains(screen))
                return;
            screen.Closed -= handler;
            // Defer teardown out of the screen's own key handler (disposing mid-keypress can leave
            // Terminal.Gui's focus machinery pointing at a freed view), like TodoApp.CloseScreen. Run the
            // caller's onClosed first (e.g. #345's cancel-the-run cleanup) while the screen is intact.
            Application.Invoke(() =>
            {
                onClosed?.Invoke();
                CloseScreen(screen);
            });
        };
        screen.Closed += handler;
        screen.FlashRequested += (_, message) => Flash(message);

        _window.Add(screen);
        UpdateHelpLine();
        screen.OnShown();
    }

    private void CloseScreen(Screen screen)
    {
        if (!_stack.Remove(screen))
            return;

        _window.Remove(screen);
        try
        {
            screen.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            Debug.WriteLine($"Screen dispose threw (Terminal.Gui teardown bug), ignoring: {ex}");
        }

        var below = ActiveScreen as View;
        below.Visible = true;
        below.SetFocus();
        UpdateHelpLine();
    }

    /// <summary>
    /// The single "quit from the launch-task root" chokepoint — the exit-confirmation seam (#298, #299
    /// sub-issue 7). <c>Esc</c> is the canonical Back key; the launch task is this mode's root, so Back
    /// <em>at the root</em> is a quit (there is no list to return to).
    /// <para>
    /// #299: it asks first, via the same <see cref="ExitConfirmScreen"/> and the same footer text the
    /// dashboard uses — mounted through this host's own <see cref="ShowScreen"/> over the hidden detail.
    /// Answering yes stops the app (closing the tab); answering no restores the launch-task detail on the
    /// tab it was on — the detail is only hidden by the overlay, never torn down, since its
    /// <c>Closed</c> event is what routed here. Re-entrancy is guarded so repeated Esc presses can't
    /// stack two questions.
    /// </para>
    /// <para>
    /// Per #298 the planned Alt+←/→ chord was dropped (it collides with terminal-emulator split-pane
    /// navigation) and there is no Forward key; forward/back across visited tasks rides #291.
    /// </para>
    /// </summary>
    private void RequestExit()
    {
        if (ActiveScreen is ExitConfirmScreen)
            return;

        // #407: same opt-out as the dashboard host — when confirmation is off, Esc/close quits the tab
        // directly. Read live from the shared config; the re-entrancy guard above still runs first.
        if (!_config.ConfirmOnExit)
        {
            Application.RequestStop();
            return;
        }

        var confirm = new ExitConfirmScreen();
        ShowScreen(confirm, () =>
        {
            if (confirm.Confirmed)
                Application.RequestStop();
        });
    }

    // ── Footer / status ──────────────────────────────────────────────────────

    private void UpdateHelpLine() => _footer.RenderHelp(ActiveScreen.HelpItems);

    private void Flash(string message) => _footer.Flash(message);

    // Ctrl+B launches the browser and immediately closes the tab (the detail screen sets
    // OpenBrowserRequested then Close()s), so — unlike TodoApp's LaunchBrowser — there is no live view
    // left to flash success/failure onto; a launch failure is only debug-logged. Shares the
    // IBrowserLauncher seam + app.clickup.com → workspace-subdomain rewrite the dashboard uses (#304/#346).
    private void LaunchBrowser(string? url)
    {
        var (result, target) = ClickUpTaskBrowser.Open(_browser, url, _config.WorkspaceSubdomain);
        switch (result)
        {
            case ClickUpTaskBrowser.Result.InvalidUrl:
                Debug.WriteLine($"Not a valid URL to open: {target}");
                break;
            case ClickUpTaskBrowser.Result.LaunchFailed:
                Debug.WriteLine($"Could not open a browser for: {target}");
                break;
        }
    }
}
