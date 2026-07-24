using System.Diagnostics;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
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
    private readonly IBrowserLauncher _browser;
    private readonly string _taskId;

    // The task/comments last shown. _task is read on the UI thread when launching the browser (Ctrl+B),
    // so it reflects any refresh since launch; both are seeded from the initial fetch and replaced by
    // UpdateData on each refresh.
    private TaskDetail _task;
    private IReadOnlyList<CommentItem> _comments;

    private Window _window = null!;
    private Label _statusLabel = null!;
    private Label _helpLabel = null!;
    private TaskDetailScreen _detail = null!;

    // Browser-style navigation history (#298): the launch task's detail is the immutable root, and the
    // only thing that navigates over it today is the F1 Help overlay. Back (Esc/Alt+←) at the root hands
    // off to RequestExit (the exit-confirmation seam, #299); forward (Alt+→) re-opens a backed-out overlay.
    private NavigationHistory<NavEntry> _history = null!;
    // The overlay currently mounted over the base detail (Help), or null when the detail root is showing.
    private Screen? _overlay;

    /// <summary>One node in the navigation history: a <paramref name="Label"/> for the footer/logs and a
    /// <paramref name="Open"/> factory that (re)creates the overlay screen. The root's factory is
    /// <c>null</c> — the root is the always-mounted base detail, not an overlay to build.</summary>
    private sealed record NavEntry(string Label, Func<Screen>? Open);

    // Coalesces overlapping refreshes (F5/Ctrl+R racing the 30s tick): skip a tick while one is in flight
    // so an earlier fetch can't land after a later one with stale data. UI-thread-only, like TodoApp's.
    private bool _refreshing;

    private string _status;

    public SingleTaskApp(TaskService tasks, AppConfig config, TaskDetail task, IReadOnlyList<CommentItem> comments,
        IBrowserLauncher? browserLauncher = null)
    {
        _tasks = tasks;
        _config = config;
        _browser = browserLauncher ?? new SystemBrowserLauncher();
        _task = task;
        _comments = comments;
        _taskId = task.Id;
        _status = $"Loaded: {task.Name}";
    }

    private Screen ActiveScreen => _overlay ?? _detail;

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
            // Mirror TodoApp.Run's teardown: Shutdown restores the terminal no matter how Dispose fares,
            // and Terminal.Gui 2.4.10 can throw ArgumentOutOfRange from a tabbed view's teardown (the same
            // bug CloseScreen guards), so swallow that known bug rather than crash while quitting.
            try
            {
                try
                {
                    _window?.Dispose();
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    Debug.WriteLine($"Window dispose threw (Terminal.Gui teardown bug), ignoring: {ex}");
                }
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
        // root (#298), so its close is back-at-root: Ctrl+B opens the browser then quits, and a plain Esc
        // hands off to the exit seam (RequestExit, #299) — which today quits the tab (there's no list to
        // return to). Only fires while the detail is front-most; with Help up, Esc goes to Help.
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
        // Deferred in single-task mode (see the plan / PR): Quick Updates needs sub-issue (5) #297 to
        // decouple its write path from the dashboard's working-set snapshot; agent dispatch needs the
        // host-coupled DispatchAgent extracted. Flash rather than silently no-op so the gap is legible.
        _detail.QuickUpdatesRequested += (_, _) =>
            Flash("Quick Updates isn't available in single-task mode yet (tracked on #297).");
        _detail.AgentDispatchRequested += (_, _) =>
            Flash("Agent dispatch isn't available in single-task mode yet.");
        _detail.FlashRequested += (_, message) => Flash(message);
        _detail.HelpRequested += (_, _) => OpenHelp();

        _statusLabel = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = _status };
        _helpLabel = new Label { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1) };

        // The launch task seeds the navigation history as its immutable root (#298). Its factory is null:
        // the root is the always-mounted base detail, not an overlay the history rebuilds.
        _history = new NavigationHistory<NavEntry>(new NavEntry("detail", Open: null));

        _window.Add(_detail, _statusLabel, _helpLabel);
        // Alt+←/Alt+→ drive the navigation history from anywhere in the window: keys the focused pane and
        // the detail screen leave unhandled bubble up to here (they claim only Ctrl chords, Esc and F-keys).
        _window.KeyDown += OnWindowKey;
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
                var comments = await _tasks.GetTaskCommentsAsync(_taskId);
                Application.Invoke(() =>
                {
                    _task = detail;
                    _comments = comments;
                    _detail.UpdateData(detail, comments);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh task: {Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() => _refreshing = false);
            }
        });
    }

    // ── Navigation history (Esc/Alt+←/Alt+→) ─────────────────────────────────

    /// <summary>Alt+← = back, Alt+→ = forward (#298). Bubbles up here from the focused pane / detail
    /// screen, which claim only Ctrl chords, Esc and F-keys, so the bare Alt+arrow chords reach the host.</summary>
    private void OnWindowKey(object? sender, Key key)
    {
        if (!key.IsAlt || key.IsCtrl)
            return;

        switch (key.KeyCode & ~KeyCode.AltMask)
        {
            case KeyCode.CursorLeft:
                key.Handled = true;
                GoBack();
                break;
            case KeyCode.CursorRight:
                key.Handled = true;
                GoForward();
                break;
        }
    }

    /// <summary>F1 opens Help as an overlay over the detail — a forward navigation onto the history.</summary>
    private void OpenHelp()
    {
        if (_overlay is HelpScreen)
            return;
        Navigate(new NavEntry("help", () => new HelpScreen()));
    }

    /// <summary>Pushes <paramref name="entry"/> onto the history and mounts it (truncating any forward
    /// entries — browser semantics, handled by the model).</summary>
    private void Navigate(NavEntry entry)
    {
        _history.Push(entry);
        ShowCurrent();
    }

    /// <summary>Back (Esc/Alt+←): steps the history back and re-shows, or — at the launch-task root —
    /// hands off to the exit seam (#299), which today quits the tab.</summary>
    private void GoBack()
    {
        if (_history.TryBack(out _))
            ShowCurrent();
        else
            RequestExit();
    }

    /// <summary>Forward (Alt+→): re-opens an overlay a prior back stepped out of, or flashes when there's
    /// nothing ahead.</summary>
    private void GoForward()
    {
        if (_history.TryForward(out _))
            ShowCurrent();
        else
            Flash("Nothing to go forward to.");
    }

    /// <summary>
    /// Mounts the overlay for the current history entry (root ⇒ the base detail), tearing down whatever
    /// overlay was showing first. One visible/focusable screen at a time (the #3 invariant). Teardown is
    /// deferred through <c>Application.Invoke</c> when triggered by a screen's own key handler so disposing
    /// a view mid-keypress can't leave Terminal.Gui's focus machinery pointing at a freed view.
    /// </summary>
    private void ShowCurrent()
    {
        // Hide the outgoing overlay now (no visual gap) but defer its Remove/Dispose out of the current
        // keypress: disposing a view mid-key can leave Terminal.Gui's focus machinery pointing at a freed
        // view (the bug TodoApp.CloseScreen guards). Focus moves to the incoming view below first.
        var previous = _overlay;
        _overlay = null;
        if (previous is not null)
            previous.Visible = false;

        var factory = _history.Current.Open;
        if (factory is null)
        {
            // Root: the base detail is front-most again.
            _detail.Visible = true;
            _detail.SetFocus();
        }
        else
        {
            _detail.Visible = false;
            var screen = factory();
            _overlay = screen;
            // The overlay's own Esc/Enter (it raises Closed) is a back navigation, like Alt+←.
            screen.Closed += (_, _) => GoBack();
            screen.FlashRequested += (_, message) => Flash(message);
            _window.Add(screen);
            screen.OnShown();
        }

        if (previous is not null)
        {
            Application.Invoke(() =>
            {
                _window.Remove(previous);
                try
                {
                    previous.Dispose();
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    Debug.WriteLine($"Screen dispose threw (Terminal.Gui teardown bug), ignoring: {ex}");
                }
            });
        }

        UpdateHelpLine();
    }

    /// <summary>
    /// The single "quit from the launch-task root" chokepoint — the exit-confirmation seam (#298, #299
    /// sub-issue 7). Today it stops the app (quits the tab, since single-task mode has no list to return
    /// to); when #299 lands, its confirmation modal plugs in here instead of quitting directly.
    /// </summary>
    private void RequestExit() => Application.RequestStop();

    // ── Footer / status ──────────────────────────────────────────────────────

    private void UpdateHelpLine()
    {
        var items = ActiveScreen.HelpItems;
        var width = _helpLabel.Frame.Width;
        var text = width > 0
            ? HelpLine.Format(HelpLine.Fit(items, width, static s => s.GetColumns()))
            : HelpLine.Format(items);
        if (_helpLabel.Text != text)
            _helpLabel.Text = text;
    }

    private void Flash(string message)
    {
        _status = message;
        _statusLabel.Text = message;
    }

    // Ctrl+B launches the browser and immediately closes the tab (the detail screen sets
    // OpenBrowserRequested then Close()s), so — unlike TodoApp's LaunchBrowser — there is no live view
    // left to flash success/failure onto; a launch failure is only debug-logged. Routes through the same
    // IBrowserLauncher seam + app.clickup.com → workspace-subdomain rewrite the dashboard uses (#304).
    private void LaunchBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        var target = ClickUpUrl.RewriteHost(url, _config.WorkspaceSubdomain);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            Debug.WriteLine($"Not a valid URL to open: {target}");
            return;
        }

        if (!_browser.TryOpen(uri))
            Debug.WriteLine($"Could not open a browser for: {target}");
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
