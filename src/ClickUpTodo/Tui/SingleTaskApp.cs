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
    // The nudge-channel consumer (#377): `_changeMarkers` is the shared cross-process channel (the Null
    // store has no channel), `_markerConsumer` the pure cursor scan over it, `_nudgePolicy` the
    // single-task view predicates. UI-thread-only, mirroring TodoApp.
    private static readonly TimeSpan MarkerPollInterval = TimeSpan.FromSeconds(4);

    private readonly TaskService _tasks;
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly IBrowserLauncher _browser;
    private readonly IChangeMarkerStore _changeMarkers;
    private readonly ChangeMarkerConsumer _markerConsumer;
    private readonly SingleTaskNudgePolicy _nudgePolicy;

    // The launch task's initially-fetched detail/comments, used once in Build() to construct the root
    // detail tab. After boot the live per-tab state lives on DetailTab (_root and any stacked child).
    private readonly TaskDetail _seedTask;
    private readonly IReadOnlyList<CommentItem> _seedComments;

    private Window _window = null!;
    // The shared status + contextual help footer (#346). Built in Build.
    private ContextualFooter _footer = null!;

    // The launch task's detail tab — the stack root. Tasks opened by walking the Task Tree tab (#374) are
    // stacked over it on _stack, so a single Esc walks back one task at a time (uniform with the dashboard
    // #291/#401), and Esc at this root falls through to the exit-confirmation seam (#298/#299).
    private DetailTab _root = null!;

    // Screens stacked over the root detail: child task details from the tree tab (#374), Help (F1), a
    // one-off agent run (#345), and the exit confirmation (#299). Empty ⇒ the root detail is front-most.
    private readonly List<Screen> _stack = [];

    // Composes the seed prompt + launches a `claude` session for the detail view's Ctrl+A dispatch
    // (#345). Built once in Build() from the persisted AgentDispatch settings (#91) — single-task mode
    // has no F2 settings dialog, so unlike TodoApp it never needs rebuilding.
    private AgentDispatcher _agent = null!;
    // True while a dispatch is in flight, so a rapid second submit doesn't launch a duplicate session.
    // UI-thread-only (set in DispatchAgent, cleared via Application.Invoke) — mirrors TodoApp.
    private bool _dispatching;

    // One-in-flight guard for the marker poll so two scans can't overlap (#377). UI-thread-only.
    private bool _pollingMarkers;

    private string _status;

    public SingleTaskApp(TaskService tasks, AppConfig config, ConfigStore configStore, TaskDetail task,
        IReadOnlyList<CommentItem> comments, IBrowserLauncher? browserLauncher = null,
        IChangeMarkerStore? changeMarkers = null)
    {
        _tasks = tasks;
        _config = config;
        _configStore = configStore;
        _browser = browserLauncher ?? new SystemBrowserLauncher();
        _seedTask = task;
        _seedComments = comments;
        _status = $"Loaded: {task.Name}";

        // The nudge channel (#377). A no-op store (the file-backed state store's Null channel, or an app
        // built without a channel) has an empty InstanceId, which disarms the poll (see ArmMarkerPoll) —
        // so cross-tab freshness only kicks in where a real cross-process channel exists. The policy reads
        // the *current* held version through a closure over the live root task so a refresh since launch
        // counts; the launch task's id is fixed for the tab's lifetime.
        _changeMarkers = changeMarkers ?? NullChangeMarkerStore.Instance;
        _markerConsumer = new ChangeMarkerConsumer(_changeMarkers.InstanceId);
        _nudgePolicy = new SingleTaskNudgePolicy(_seedTask.Id, () => _root.Task.UpdatedMs);
    }

    /// <summary>A single Task Detail screen plus the live per-task state that follows it — its task id, the
    /// last-shown <see cref="TaskDetail"/>/comments (replaced by <c>UpdateData</c> on each refresh), and an
    /// in-flight-refresh guard. The launch task is the root tab; walking the Task Tree tab (#374) stacks a
    /// child tab per visited task over it, so agent dispatch / refresh / browser follow the front-most tab
    /// rather than a single root field.</summary>
    private sealed class DetailTab(TaskDetailScreen screen, string taskId, TaskDetail task,
        IReadOnlyList<CommentItem> comments)
    {
        public TaskDetailScreen Screen { get; } = screen;
        public string TaskId { get; } = taskId;
        public TaskDetail Task { get; set; } = task;
        public IReadOnlyList<CommentItem> Comments { get; set; } = comments;

        // Coalesces overlapping refreshes (F5/Ctrl+R racing the 30s tick): skip a tick while one is in
        // flight so an earlier fetch can't land after a later one with stale data. UI-thread-only.
        public bool Refreshing { get; set; }
    }

    private Screen ActiveScreen => _stack.Count > 0 ? _stack[^1] : _root.Screen;

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
            ArmMarkerPoll();
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
        // Title the window with this task, not the product branding (#418). Terminal.Gui propagates the
        // top-level window Title to the host terminal's window/tab title, so a `--task` tab identifies
        // itself as "{id}: {name}" (custom id preferred, ≤40 chars) on the tab strip — where the identical
        // "ClickUp Simple CLI — <workspace>" the dashboard uses would not distinguish tabs. The frame
        // naming the task is also apt here: the whole tab *is* that one task.
        _window = new Window { Title = TerminalTitle.ForTask(_seedTask.Id, _seedTask.CustomId, _seedTask.Name) };

        // Build the agent dispatcher from the persisted settings (#91), same as the dashboard's
        // BuildAgentDispatcher — the preferred terminal / claude path / launch-location default apply.
        _agent = new AgentDispatcher(new TerminalLauncher(), _config.AgentDispatch.ToLauncherOptions());

        // Build the launch task's detail tab (the stack root) with the shared wiring, then add its
        // root-only Closed behaviour. Ctrl+B sets OpenBrowserRequested then closes; Esc closes directly.
        // The root detail is the launch-task root (#298), so its close is back-at-root: a plain Esc hands
        // off to the exit seam (RequestExit), which asks for confirmation (#299) before quitting the tab.
        // Ctrl+B is deliberately *not* confirmed: "open this task in the browser and close the tab" is an
        // explicit, unambiguous request, not the ambiguous Esc that #290 flagged and #299 guards. Only
        // fires while the root detail is front-most; with an overlay up (incl. that confirmation, or a
        // stacked child detail from the tree tab), Esc goes to the top layer instead. Child tabs opened
        // from the Task Tree tab (#374) pop back on close rather than quit — wired in OpenTaskDetail.
        _root = BuildDetailTab(_seedTask, _seedComments);
        _root.Screen.Closed += (_, _) =>
        {
            if (_root.Screen.OpenBrowserRequested)
            {
                LaunchBrowser(_root.Task.Url);
                Application.RequestStop();
                return;
            }

            RequestExit();
        };
        // The root detail is added straight to the window (not through ShowScreen), so — unlike a stacked
        // child, whose flashes ShowScreen routes — it wires its own flash relay to the shared footer.
        _root.Screen.FlashRequested += (_, message) => Flash(message);

        _footer = new ContextualFooter(_status);

        _window.Add(_root.Screen);
        _footer.AddTo(_window);
        // Re-fit the contextual footer whenever the window re-lays out (terminal resize); the text is
        // only reassigned when it changes, so this can't loop (mirrors TodoApp). The first laid-out frame
        // also drives the root detail's OnShown (see below).
        var shown = false;
        _window.SubViewsLaidOut += (_, _) =>
        {
            UpdateHelpLine();
            // Run the root detail's OnShown on the first laid-out frame — the way a dashboard detail is
            // shown (mid-run by ShowScreen, after layout) — rather than in Build() before the window has
            // laid out. OnShown selects the default tab and focuses its pane (FocusCurrentPane); running it
            // pre-layout targets un-laid-out views, so the front-most pane never becomes the focused view
            // and the Task Tree tab's ListView can't take ↑/↓ selection (#374). Deferring one frame makes
            // single-task mode's focus path identical to the dashboard's. One-shot.
            if (!shown)
            {
                shown = true;
                _root.Screen.OnShown();
            }
        };
        UpdateHelpLine();
    }

    /// <summary>Constructs a fully-wired <see cref="TaskDetailScreen"/> for one task — the launch task
    /// (root) or a task opened from the Task Tree tab (#374) — and bundles it with its live per-task state
    /// in a <see cref="DetailTab"/>. Wires the events common to every detail (refresh, agent dispatch,
    /// Quick Updates, help, link activation, tree-row open, and the F6 badge cycle); the caller adds the
    /// Closed behaviour, which differs between the root (exit the tab) and a stacked child (pop back), and
    /// the <see cref="Screen.FlashRequested"/> wiring (children get it from <see cref="ShowScreen"/>; the
    /// root, which is not shown through <see cref="ShowScreen"/>, has it wired in <see cref="Build"/>).</summary>
    private DetailTab BuildDetailTab(TaskDetail task, IReadOnlyList<CommentItem> comments)
    {
        // Root the Dispatch pane's working-dir browser at the saved base dir (#92), falling back to home
        // if it doesn't exist yet — same resolution the dashboard uses when opening Task Detail.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDir = SettingsForm.ResolveDefaultWorkingDirectory(_config.DefaultWorkingDirectory, home);
        var browserRoot = Directory.Exists(baseDir) ? baseDir : home;
        var id = task.Id;

        var screen = new TaskDetailScreen(
            task, comments, browserRoot,
            settings: _config.DetailView,
            defaultSessionMode: _config.AgentDispatch.DefaultSessionMode,
            defaultPostToComments: _config.AgentDispatch.DefaultPostResultsToComments,
            defaultLaunchLocation: _config.AgentDispatch.LaunchLocation,
            workingDirectoryPreFill: () => DispatchWorkingDirectoryCache.PreFill(_config.TaskWorkingDirectories, id),
            // Ctrl+N posts a plain-text comment; Ctrl+E edits the description — same injected-async seam
            // the dashboard wires, keyed to *this* tab's task so a stacked child writes to its own task.
            postCommentAsync: (text, ct) => _tasks.CreateTaskCommentAsync(id, text, ct),
            // Ctrl+T (#330) replies into a comment's thread — same injected-async seam, keyed to this tab's task.
            postReplyAsync: (commentId, text, ct) => _tasks.CreateThreadedCommentAsync(commentId, text, ct),
            setDescriptionAsync: (text, ct) => _tasks.SetTaskDescriptionAsync(id, text, ct),
            // The Task Tree tab (#374): identical wiring to the dashboard (#291/#415). The tree needs the
            // signed-in user's id for the trailing Assignees badge (#161), seeds its badge mode from the
            // persisted BadgeDisplay so it opens in the same state as the main list, and lazy-loads off the
            // UI thread on first cycle to the tab, keyed to this tab's task id. No ancestry snapshot to
            // seed here (#419): single-task (--task) mode holds no broader working set, so this stays a
            // plain fetch.
            currentUserId: _tasks.UserId,
            treeBadgeDisplay: _config.BadgeDisplay,
            loadTaskTreeAsync: ct => _tasks.GetTaskTreeAsync(id, snapshotLookup: null, ct));

        var tab = new DetailTab(screen, id, task, comments);

        // F5 / Ctrl+R and the screen's own 30s tick ask for fresh data — refetch just this tab's task.
        screen.RefreshRequested += (_, _) => RefreshTab(tab);
        // Quick Updates stays deferred in single-task mode: it needs sub-issue (5) #297 to decouple its
        // write path from the dashboard's working-set snapshot. Flash rather than silently no-op so the
        // gap is legible.
        screen.QuickUpdatesRequested += (_, _) =>
            Flash("Quick Updates isn't available in single-task mode yet (tracked on #297).");
        // Agent dispatch (Ctrl+A) runs through the shared DispatchCoordinator (#345), so a single-task tab
        // composes + launches a session with the dashboard's exact working-dir / post-to-Comments /
        // launch-location semantics — against this tab's task.
        screen.AgentDispatchRequested += (_, request) => DispatchAgent(tab, request);
        screen.HelpRequested += (_, _) => OpenHelp();
        // Clicking/activating a link in a text pane (#318). A web link opens in the browser; a task link
        // now navigates in-app — #374 wired the stacking OpenTaskDetail, so a task link no longer degrades
        // to the browser as it did before #374. ActivateLink routes by the link's resolved action.
        screen.LinkActivationRequested += (_, request) => ActivateLink(request);
        // Task Tree tab (#374): Enter/double-click a tree row opens that task's detail stacked over this
        // one, so a single Esc walks back one task at a time — uniform with the canonical "Esc = Back"
        // model (#401/#298) and the dashboard's tree navigation (#291).
        screen.OpenTaskRequested += (_, targetId) => OpenTaskDetail(targetId);
        // F6 on the Task Tree tab (#415) cycles the tree's badge display; the host owns the flip/persist
        // and reflects it across the root and every stacked child so the visited-task chain stays in step.
        screen.CycleBadgeDisplayRequested += (_, _) => CycleTreeBadgeDisplay(tab.Screen);

        return tab;
    }

    /// <summary>Re-fetches a tab's task detail + comments off the UI thread and feeds them back in.</summary>
    private void RefreshTab(DetailTab tab)
    {
        // Only refresh while this tab is front-most (e.g. not while Help or a stacked child detail is over
        // it): no point spending a round-trip on a hidden view. The next tick refreshes once it's on top.
        if (!ReferenceEquals(ActiveScreen, tab.Screen))
            return;
        if (tab.Refreshing)
            return;
        tab.Refreshing = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailAsync(tab.TaskId);
                var comments = await _tasks.GetTaskCommentsWithRepliesAsync(tab.TaskId);
                Application.Invoke(() =>
                {
                    tab.Task = detail;
                    tab.Comments = comments;
                    tab.Screen.UpdateData(detail, comments);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh task: {ErrorText.Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() => tab.Refreshing = false);
            }
        });
    }

    /// <summary>
    /// Opens a task in-app, stacked over the current detail — the destination for both a Task Tree row
    /// (Enter/double-click, #374) and a clicked task link (#318). Loads the target's detail + comments off
    /// the UI thread and mounts a fresh detail tab via <see cref="ShowScreen"/>, so a single Esc walks back
    /// to the task we came from (and Esc at the launch-task root still hands off to the exit confirmation) —
    /// the walkable-back model (#401/#298) the dashboard uses for the same gestures (#291/#303).
    /// <para>
    /// A bare id may actually be a custom id (#353); <paramref name="customIdFallbackTeamId"/> (when set,
    /// from a link's team id or the configured workspace) lets the load retry as a custom id on a 404.
    /// Ctrl+B on a stacked child opens the browser and pops back to the previous task, rather than quitting
    /// the whole tab as it does at the root: browsing a task reached in-app shouldn't tear down the launch
    /// session. The open is dropped if the requesting layer is no longer front-most when the fetch lands
    /// (e.g. the user Esc'd away first), mirroring TodoApp.OpenTaskDetail.
    /// </para>
    /// </summary>
    private void OpenTaskDetail(string taskId, string? customIdFallbackTeamId = null)
    {
        var requester = ActiveScreen;
        Flash("Loading details…");
        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailWithCustomIdFallbackAsync(taskId, customIdFallbackTeamId);
                // Load comments by the RESOLVED id — identical to taskId for a real id, and correct when a
                // custom-id fallback resolved to the real task id.
                var resolvedId = detail.Id;
                var comments = await _tasks.GetTaskCommentsWithRepliesAsync(resolvedId);
                Application.Invoke(() =>
                {
                    if (!ReferenceEquals(ActiveScreen, requester))
                        return;
                    var tab = BuildDetailTab(detail, comments);
                    ShowScreen(tab.Screen, () =>
                    {
                        if (tab.Screen.OpenBrowserRequested)
                            LaunchBrowser(tab.Task.Url);
                    });
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load task detail: {ErrorText.Short(ex)}"));
            }
        });
    }

    // ── Cross-process nudge channel — consumer (#377) ─────────────────────────

    /// <summary>
    /// Arms the nudge-channel consumer for the single-task tab (#377), mirroring
    /// <c>TodoApp.ArmMarkerPoll</c>: seed the cursor to the current max marker seq so a fresh tab never
    /// replays history (#295 edge case 1), then start a repeating marker poll on
    /// <see cref="MarkerPollInterval"/> — its own short cadence, decoupled from the detail's 30s
    /// auto-refresh. A no-op store (the file-backed state store's Null channel) has an empty InstanceId, so
    /// nothing is armed and the tab keeps only its 30s freshness. Runs on the UI thread during
    /// <see cref="Run"/>, before the run loop pumps.
    /// </summary>
    private void ArmMarkerPoll()
    {
        if (string.IsNullOrEmpty(_changeMarkers.InstanceId))
            return; // no cross-process channel (e.g. the JSON file store) — nothing to consume.

        _markerConsumer.Initialize(_changeMarkers.ReadAll());
        // The timeout callback fires on the UI thread; returning true keeps it repeating. It's torn down
        // by Application.Shutdown at quit along with the run loop.
        Application.AddTimeout(MarkerPollInterval, () =>
        {
            PollMarkers();
            return true;
        });
    }

    /// <summary>
    /// One marker-poll tick (#377): read the markers <b>off</b> the UI thread (a <c>changes</c> ReadAll
    /// briefly takes LiteDB's shared-mode cross-process lock), then run the pure cursor scan and reconcile
    /// <b>on</b> the UI thread (the policy reads the live root task). The single-task tab holds exactly one
    /// launch task, so the scan can only ever surface that one id — reconcile reuses <see cref="RefreshTab"/>
    /// on the root tab (its own in-flight / front-most guards apply), which is the per-task re-fetch, never
    /// a full resync and never a self-echo (own-instance markers are filtered by the consumer). A single
    /// in-flight guard keeps two scans from overlapping; best-effort throughout — a read failure is
    /// swallowed, since a nudge rides on an edit that already succeeded elsewhere.
    /// </summary>
    private void PollMarkers()
    {
        if (_pollingMarkers)
            return;
        _pollingMarkers = true;

        _ = Task.Run(() =>
        {
            IReadOnlyList<ChangeMarker> markers;
            try { markers = _changeMarkers.ReadAll(); }
            catch { markers = []; }

            Application.Invoke(() =>
            {
                try
                {
                    foreach (var _ in _markerConsumer.Advance(markers, _nudgePolicy.IsInView, _nudgePolicy.HeldVersion))
                        RefreshTab(_root);
                }
                finally
                {
                    _pollingMarkers = false;
                }
            });
        });
    }

    // ── Agent dispatch (Ctrl+A) ───────────────────────────────────────────────

    /// <summary>
    /// Ctrl+A from a single-task tab: dispatches an agent for the front-most tab's task through the shared
    /// <see cref="DispatchCoordinator"/> (#345) — an interactive terminal session or, in one-off mode
    /// (#94), a background <c>claude -p</c> run rendered in an <see cref="Screens.AgentRunScreen"/> — with
    /// the same working-dir (#91/#95/#96/#98), post-to-Comments (#97), and launch-location (#275)
    /// semantics as the dashboard. Runs on the UI thread (invoked from the detail screen's key handler).
    /// </summary>
    private void DispatchAgent(DetailTab tab, DispatchRequest request)
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
        var plan = DispatchCoordinator.Plan(_config.AgentDispatch, request, tab.Task, _config.DefaultWorkingDirectory, home);

        // Persist an explicit non-default working-dir pick for this task (#96) so the next dispatch
        // pre-fills it; reverting to the default clears the entry. Save only when the cache changed.
        if (DispatchCoordinator.ReconcileCache(_config.TaskWorkingDirectories, tab.TaskId, plan))
            _configStore.Save(_config);

        // One-off mode runs as a background child with output in a screen (#99); interactive opens a
        // real terminal below (an interactive session needs a live TTY). The run screen mounts through
        // this host's own ShowScreen (Esc cancels/closes it back to the detail root).
        if (plan.OneOff)
        {
            DispatchCoordinator.RunBackground(
                _agent, tab.Task, tab.Comments, plan,
                mount: (screen, onClosed) => ShowScreen(screen, onClosed),
                clearDispatching: () => _dispatching = false);
            return;
        }

        Flash($"Launching Claude for '{tab.Task.Name}'…");
        DispatchCoordinator.RunInteractive(
            _agent, tab.Task, tab.Comments, plan,
            report: message => { _dispatching = false; Flash(message); });
    }

    /// <summary>F6 on a Task Tree tab (#415): cycles + persists the shared <see cref="AppConfig.BadgeDisplay"/>
    /// (Icons → Text → Hidden) and reflects the new mode into the root and every stacked child detail's
    /// tree — a pure in-place re-render, no re-fetch — so Esc-ing back through the visited-task chain (#374)
    /// shows the same badge mode everywhere. Runs on the UI thread from the screen's key handler; a no-op
    /// if the raising screen isn't front-most, and a no-op per detail whose tree hasn't loaded yet.</summary>
    private void CycleTreeBadgeDisplay(TaskDetailScreen screen)
    {
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        var mode = _config.BadgeDisplay.Next();
        _config.BadgeDisplay = mode;
        _configStore.Save(_config);
        _root.Screen.SetTreeBadgeDisplay(mode);
        foreach (var stacked in _stack)
            if (stacked is TaskDetailScreen detail)
                detail.SetTreeBadgeDisplay(mode);
        Flash(mode.Describe());
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
    /// stack two questions. #407: when the user has turned confirmation off, Esc/close quits directly.
    /// </para>
    /// <para>
    /// Per #298 the planned Alt+←/→ chord was dropped (it collides with terminal-emulator split-pane
    /// navigation) and there is no Forward key; forward/back across visited tasks rides #291/#374.
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

    // ── Link activation (#318) ─────────────────────────────────────────────────

    /// <summary>
    /// Acts on a link the user activated in a Task Detail pane (#318). A task link now opens the linked
    /// task's detail <em>in-app</em>, stacked over the current one — the in-app destination #374 wired
    /// (<see cref="OpenTaskDetail"/>). Before #374 single-task mode had no such destination, so a task link
    /// degraded to the browser; it no longer does. The URL is resolved the same way the dashboard's
    /// quick-open resolver does (<c>QuickOpenParser</c>, #303/#353): a plain-id task URL opens straight
    /// through the detail load (with the URL's/workspace's team id as a custom-id fallback), a custom-id URL
    /// resolves first. Any web link — or a task URL we can't parse/resolve — opens in the browser via
    /// <see cref="OpenLink"/>, which flashes the outcome on the live footer.
    /// </summary>
    private void ActivateLink(LinkActivationRequest request)
    {
        if (request.Action == LinkAction.OpenTaskDetail)
        {
            var reference = QuickOpenParser.Parse(request.Url);
            // A custom-id URL carries its own team id (#353); prefer it over the configured workspace so a
            // link pasted from a different workspace resolves against that workspace, not this one.
            var teamId = string.IsNullOrWhiteSpace(reference.TeamId) ? _config.WorkspaceId : reference.TeamId;
            switch (reference.Kind)
            {
                case QuickOpenKind.TaskId:
                    OpenTaskDetail(reference.Value, customIdFallbackTeamId: teamId);
                    return;
                case QuickOpenKind.CustomId when !string.IsNullOrWhiteSpace(teamId):
                    ResolveCustomIdAndOpen(reference.Value, teamId);
                    return;
            }
            // Unparseable task URL (or a custom id with no workspace to resolve against) → fall through to
            // the browser rather than silently doing nothing.
        }

        OpenLink(request.Url);
    }

    /// <summary>Resolves a custom-id task link to its real id off the UI thread (#353), then opens it in-app
    /// (stacked). "Fetching task…" covers the resolve; the subsequent <see cref="OpenTaskDetail"/> owns its
    /// own "Loading details…". A failure flashes and leaves the current tab untouched. Mirrors the custom-id
    /// arm of TodoApp.ResolveAndOpen.</summary>
    private void ResolveCustomIdAndOpen(string customId, string teamId)
    {
        Flash("Fetching task…");
        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailByCustomIdAsync(customId, teamId);
                Application.Invoke(() => OpenTaskDetail(detail.Id));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Couldn't find task \"{customId}\": {ErrorText.Short(ex)}"));
            }
        });
    }

    /// <summary>
    /// Opens a link activated in a detail pane (#318) in the browser. Unlike <see cref="LaunchBrowser"/>'s
    /// Ctrl+B path this doesn't close the tab, so there <em>is</em> a live footer to report onto — the
    /// dashboard flashes the same three outcomes. Shares the rewrite/parse/launch core (#304/#346).
    /// </summary>
    private void OpenLink(string url)
    {
        var (result, target) = ClickUpTaskBrowser.Open(_browser, url, _config.WorkspaceSubdomain);
        switch (result)
        {
            case ClickUpTaskBrowser.Result.Opened:
                Flash($"Opened: {target}");
                break;
            case ClickUpTaskBrowser.Result.LaunchFailed:
                var hint = BrowserLaunchPlanner.OpenerHint(BrowserLaunchPlanner.CurrentOS());
                Flash(hint is null
                    ? $"Couldn't open a browser — copy the URL: {target}"
                    : $"Couldn't open a browser ({hint}) — copy the URL: {target}");
                break;
            default:
                Flash($"Not a valid URL: {target}");
                break;
        }
    }

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
