using System.Diagnostics;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
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

    // Screens stacked over the root detail (only Help, via F1). Empty ⇒ the detail is front-most.
    private readonly List<Screen> _stack = [];

    // Coalesces overlapping refreshes (F5/Ctrl+R racing the 30s tick): skip a tick while one is in flight
    // so an earlier fetch can't land after a later one with stale data. UI-thread-only, like TodoApp's.
    private bool _refreshing;

    private string _status;

    public SingleTaskApp(TaskService tasks, AppConfig config, TaskDetail task, IReadOnlyList<CommentItem> comments)
    {
        _tasks = tasks;
        _config = config;
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
        // Ctrl+B sets OpenBrowserRequested then closes; Esc closes directly. Either way the root closing
        // means "quit the tab" (there's no list to return to), launching the browser first if asked.
        _detail.Closed += (_, _) =>
        {
            if (_detail.OpenBrowserRequested)
                LaunchBrowser(_task.Url);
            Application.RequestStop();
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

        _window.Add(_detail, _statusLabel, _helpLabel);
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

    // ── Help overlay (F1) ────────────────────────────────────────────────────

    /// <summary>F1 stacks a <see cref="HelpScreen"/> over the detail; Esc pops back to it.</summary>
    private void OpenHelp()
    {
        if (ActiveScreen is HelpScreen)
            return;
        ShowScreen(new HelpScreen());
    }

    private void ShowScreen(Screen screen)
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
            // Terminal.Gui's focus machinery pointing at a freed view), like TodoApp.CloseScreen.
            Application.Invoke(() => CloseScreen(screen));
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
    // left to flash success/failure onto; a launch failure is only debug-logged.
    private void LaunchBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open browser: {ex}");
        }
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
