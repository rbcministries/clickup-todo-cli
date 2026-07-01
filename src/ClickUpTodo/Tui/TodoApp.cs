using System.Collections.ObjectModel;
using System.Diagnostics;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui 2.4 deprecates the static `Application` facade in favour of an instance-based
// API that is not yet stable or documented. The static API remains the supported v2 pattern,
// so we intentionally use it and silence the deprecation here until the instance API settles.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// The keyboard-driven terminal UI: a single task list with a pinned "Current Focus" section at the
/// top, refreshed in the background on the configured interval. Selection is preserved by task id
/// across refreshes so the list stays visually static between updates.
/// <para>
/// This uses ONE ListView (with header rows) rather than two panes: a second focusable pane made
/// repaints visibly laggy in Terminal.Gui 2.4 (see issue #3), while a single list is snappy.
/// </para>
/// <para>
/// Secondary views (Settings, the status picker, Help) open as full-window <see cref="Screen"/>s
/// swapped into this same toplevel — not nested modal <c>Dialog</c>s on their own
/// <c>Application.Run</c> loop (see <see cref="ShowScreen"/>/#38). A nested run-loop competes with
/// the background refresh's redraws and feels laggy, the same way the second pane did in #3.
/// </para>
/// </summary>
public sealed class TodoApp
{
    private const string FocusHeaderPrefix = "★ CURRENT FOCUS";
    private static readonly string TasksHeaderPrefix = $"─ {AppBranding.TasksSectionLabel}";

    private readonly TaskService _tasks;
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly IFocusStore _focus;

    private Window _window = null!;
    private FrameView _frame = null!;
    private ListView _list = null!;
    private Label _statusLabel = null!;
    private RefreshService _refresh = null!;
    // The full-window screen currently swapped in over the list (Settings / status picker / Help),
    // or null when the list is showing. Only one screen is open at a time.
    private Screen? _activeScreen;

    private IReadOnlyList<TaskItem> _all = [];
    // Parallel to the ListView's rows: the task on each row, or null for a header/separator row.
    private readonly List<TaskItem?> _rows = [];
    // The ListView's backing collection, kept so a single row can be updated in place (without
    // SetSource, which would reset the list and the cursor).
    private ObservableCollection<string> _display = [];
    // Per-row status-badge color overlay, parallel to _display (null = header row or no/!valid color).
    private List<StatusBadgeListSource.Badge?> _badges = [];
    // Per-row nesting depth, parallel to _display, so an in-place row update keeps its indent (#46).
    private List<int> _depths = [];
    // Parents of assigned subtasks that aren't themselves in the snapshot, shown as context headers in
    // the nested subtasks view (F4). Resolved off the UI thread only while NestSubtasks is on.
    private IReadOnlyDictionary<string, TaskItem> _contextParents = EmptyParents;
    private static readonly IReadOnlyDictionary<string, TaskItem> EmptyParents = new Dictionary<string, TaskItem>();
    private string _status = "Loading…";
    private string _signature = "";

    public TodoApp(TaskService tasks, AppConfig config, ConfigStore configStore, IFocusStore focus)
    {
        _tasks = tasks;
        _config = config;
        _configStore = configStore;
        _focus = focus;
    }

    public void Run(string? driverName = null)
    {
        // driverName lets the user pick a Terminal.Gui driver (windows/dotnet/ansi); null = default.
        Application.Init(driverName);
        try
        {
            _status = $"Loading… (driver: {driverName ?? "default (ansi)"})";
            Build();
            _refresh = new RefreshService(
                fetch: FetchAsync,
                intervalSeconds: _config.RefreshSeconds,
                onUpdate: tasks => Application.Invoke(() => OnTasksLoaded(tasks)),
                onError: ex => Application.Invoke(() => Flash($"Refresh failed: {Short(ex)}")));
            _refresh.Start();
            Application.Run(_window);
        }
        finally
        {
            _refresh?.Dispose();
            _window?.Dispose();
            Application.Shutdown();
        }
    }

    /// <summary>
    /// Background fetch for the refresh loop: loads the task snapshot and, when the nested subtasks
    /// view is on, resolves any parents not in the snapshot so they can be shown as context headers.
    /// Runs off the UI thread; <see cref="_contextParents"/> is set before the result is marshalled in.
    /// </summary>
    private async Task<IReadOnlyList<TaskItem>> FetchAsync(CancellationToken ct)
    {
        var tasks = await _tasks.LoadAsync(ct);
        _contextParents = _config.View.ShowSubtasks
            ? await _tasks.ResolveContextParentsAsync(tasks, ct)
            : EmptyParents;
        return tasks;
    }

    private void Build()
    {
        _window = new Window { Title = AppBranding.WindowTitle(_config.WorkspaceName) };

        _frame = new FrameView
        {
            Title = "Tasks",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        _list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _list.KeyDown += OnListKey;
        _frame.Add(_list);

        _statusLabel = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = _status };
        var help = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
            Text = "↑/↓ move · →| next section · ␣ status · ↩ detail · Ctrl+B 🌐 · Ctrl+P 📌 · Ctrl+R ↻ · F1 help · F2 ⚙ · F3 filter/sort/group · F4 subtasks · Ctrl+Q quit · type to search",
        };

        _window.Add(_frame, _statusLabel, help);
        _list.SetFocus();
    }

    // ── Key handling ─────────────────────────────────────────────────────────

    private void OnListKey(object? sender, Key key)
    {
        // Command shortcuts use modifier chords / function keys. Bare letters are left unhandled so
        // the ListView's type-ahead search (keyed on the task title) keeps working.
        if (key.IsCtrl)
        {
            switch (key.KeyCode & ~KeyCode.CtrlMask)
            {
                case KeyCode.P:
                    key.Handled = true;
                    TogglePin();
                    break;
                case KeyCode.R:
                    key.Handled = true;
                    Flash("Refreshing…");
                    _refresh.RequestRefresh();
                    break;
                case KeyCode.B:
                    key.Handled = true;
                    OpenInBrowser();
                    break;
                case KeyCode.Q:
                case KeyCode.C: // Ctrl+C as a quit alias (the OS/terminal may intercept it first).
                    key.Handled = true;
                    Application.RequestStop();
                    break;
            }
            return;
        }

        switch (key.KeyCode)
        {
            case KeyCode.Space:
                key.Handled = true;
                OpenStatusPicker();
                break;
            case KeyCode.Enter:
                key.Handled = true;
                OpenDetail();
                break;
            case KeyCode.Tab:
                key.Handled = true;
                JumpToNextSection();
                break;
            case KeyCode.Esc:
                key.Handled = true;
                Application.RequestStop();
                break;
            case KeyCode.F1:
                key.Handled = true;
                ShowHelp();
                break;
            case KeyCode.F2:
                key.Handled = true;
                OpenSettings();
                break;
            case KeyCode.F3:
                key.Handled = true;
                OpenViewSettings();
                break;
            case KeyCode.F4:
                key.Handled = true;
                ToggleShowSubtasks();
                break;
        }
    }

    /// <summary>Toggles the subtasks view (F4, #46) — hidden vs. shown nested — and persists it.</summary>
    private void ToggleShowSubtasks()
    {
        if (_activeScreen is not null)
            return;

        var on = !_config.View.ShowSubtasks;
        _config.View.ShowSubtasks = on;
        _configStore.Save(_config);
        Flash(on ? "Subtasks shown, nested under their parent (F4)." : "Subtasks hidden (F4).");

        // Re-render immediately (in-snapshot parents nest without waiting on the network), keep the
        // stored signature in sync, then — when turning on — refresh to pull in parents not assigned
        // to me as context headers; that fetch changes the signature again and re-renders when it lands.
        if (!on)
            _contextParents = EmptyParents;
        Render(keepTaskId: CurrentTask()?.Id);
        _signature = CurrentSignature(_all);
        if (on)
            _refresh.RequestRefresh();
    }

    private void OpenViewSettings()
    {
        if (_activeScreen is not null)
            return;

        var screen = new FilterSortGroupScreen(_config.View);
        ShowScreen(screen, () => ApplyViewSettings(screen.Result));
    }

    private void ApplyViewSettings(ViewSettings? result)
    {
        if (result is null)
            return;

        _config.View = result;
        _configStore.Save(_config);
        Flash(ViewSummary(result));
        // The view changed but the underlying task set didn't, so re-render directly rather than
        // waiting on a refresh (BuildSignature would otherwise treat it as a no-op).
        Render(keepTaskId: CurrentTask()?.Id);
    }

    /// <summary>A one-line description of the active view for the status line.</summary>
    private static string ViewSummary(ViewSettings view)
    {
        if (view.IsDefault)
            return "View reset to default.";
        var parts = new List<string>();
        if (view.Filters.Count > 0)
            parts.Add($"{view.Filters.Count} filter(s)");
        if (view.SortField is { } sf)
            parts.Add($"sort {TaskFieldInfo.DisplayName(sf)} {(view.SortDirection == SortDirection.Ascending ? "↑" : "↓")}");
        if (view.GroupField is { } gf)
            parts.Add($"group by {TaskFieldInfo.DisplayName(gf)}");
        return "View: " + string.Join(" · ", parts);
    }

    private void OpenSettings()
    {
        if (_activeScreen is not null)
            return;

        var screen = new SettingsScreen(_config.RefreshSeconds, _config.ExcludedStatuses);
        ShowScreen(screen, () =>
        {
            var result = screen.Result;
            if (result is null)
                return;

            _config.RefreshSeconds = result.RefreshSeconds;
            _config.ExcludedStatuses = result.ExcludedStatuses;
            _configStore.Save(_config);

            _refresh.IntervalSeconds = result.RefreshSeconds;
            Flash($"Settings saved · refresh {result.RefreshSeconds}s · {result.ExcludedStatuses.Count} status(es) excluded");
            _refresh.RequestRefresh();
        });
    }

    // ── Screen navigation seam ─────────────────────────────────────────────────
    // Swaps a full-window screen in over the list within the single toplevel (no nested
    // Application.Run). #17's detail view builds on this. See the class header / #38.

    /// <summary>
    /// Mounts a screen over the task list: hides the list frame, adds the screen to the window, and
    /// focuses it. When the screen raises <see cref="Screen.Closed"/>, <paramref name="onClosed"/>
    /// runs (to read any result) and then the list is restored. No-ops if a screen is already open.
    /// </summary>
    private void ShowScreen(Screen screen, Action onClosed)
    {
        if (_activeScreen is not null)
            return;

        _activeScreen = screen;

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            // Guard against a double-fire (e.g. two Esc presses before teardown runs).
            if (_activeScreen != screen)
                return;
            screen.Closed -= handler;
            // Defer teardown out of the screen's own key handler: disposing the view mid-keypress
            // can leave Terminal.Gui's input/focus machinery pointing at a freed view. Running on the
            // next loop iteration lets the current input cycle finish first.
            Application.Invoke(() =>
            {
                onClosed();      // read the screen's result while it's still intact
                CloseScreen();   // then tear it down and restore the list
            });
        };
        screen.Closed += handler;

        _frame.Visible = false;
        _window.Add(screen);
        screen.OnShown();
    }

    /// <summary>Tears down the active screen and restores the list with its cursor intact.</summary>
    private void CloseScreen()
    {
        if (_activeScreen is null)
            return;

        var screen = _activeScreen;
        _activeScreen = null;
        _window.Remove(screen);
        screen.Dispose();
        _frame.Visible = true;
        _list.SetFocus();
    }

    /// <summary>The task on the selected row, or null if a header row (or nothing) is selected.</summary>
    private TaskItem? CurrentTask()
        => _list.SelectedItem is int i && i >= 0 && i < _rows.Count ? _rows[i] : null;

    /// <summary>
    /// Moves the cursor to the first task row beneath the next header (sections are delimited by the
    /// header rows tracked as null entries in <see cref="_rows"/>). Wraps to the first section.
    /// </summary>
    private void JumpToNextSection()
    {
        var headers = Enumerable.Range(0, _rows.Count).Where(i => _rows[i] is null).ToList();
        if (headers.Count == 0)
            return; // no sections (e.g. nothing pinned)

        var current = _list.SelectedItem ?? 0;
        var nextHeader = headers.FirstOrDefault(h => h > current, headers[0]);

        // First selectable (task) row at/after the header; wrap to the first section if the last is empty.
        var target = FirstTaskAtOrAfter(nextHeader + 1);
        if (target < 0)
            target = FirstTaskAtOrAfter(headers[0] + 1);
        if (target >= 0)
            _list.SelectedItem = target;

        int FirstTaskAtOrAfter(int start)
        {
            for (var i = start; i < _rows.Count; i++)
                if (_rows[i] is not null)
                    return i;
            return -1;
        }
    }

    // ── Actions ────────────────────────────────────────────────────────────

    private void TogglePin()
    {
        var task = CurrentTask();
        if (task is null)
            return;
        // The pin write goes through IFocusStore (local today, possibly network-backed later), so
        // run it off the key handler and apply the result back on the UI thread. The local store
        // completes synchronously, so this stays snappy.
        _ = TogglePinAsync(task);
    }

    private async Task TogglePinAsync(TaskItem task)
    {
        bool nowPinned;
        try
        {
            nowPinned = await _focus.ToggleAsync(task.Id);
        }
        catch (Exception ex)
        {
            Application.Invoke(() => Flash($"Could not update focus: {Short(ex)}"));
            return;
        }

        Application.Invoke(() =>
        {
            Render(keepTaskId: task.Id);
            Flash(nowPinned ? $"Pinned: {task.Name}" : $"Unpinned: {task.Name}");
        });
    }

    private void OpenInBrowser()
    {
        var task = CurrentTask();
        LaunchBrowser(task?.Url, task?.Name);
    }

    /// <summary>Opens a task URL in the system browser, or flashes why it couldn't.</summary>
    private void LaunchBrowser(string? url, string? name)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Flash("No URL for this task.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Flash($"Opened: {name}");
        }
        catch (Exception ex)
        {
            Flash($"Could not open browser: {Short(ex)}");
        }
    }

    private void OpenDetail()
    {
        var task = CurrentTask();
        if (task is null || _activeScreen is not null)
            return;

        Flash("Loading details…");
        // Fetch the detail + comments off the UI thread, then swap in the detail screen back on it.
        // The background dashboard refresh keeps running while the screen is open.
        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailAsync(task.Id);
                var comments = await _tasks.GetTaskCommentsAsync(task.Id);
                Application.Invoke(() =>
                {
                    if (_activeScreen is not null)
                        return;
                    var screen = new TaskDetailScreen(detail, comments);
                    ShowScreen(screen, () =>
                    {
                        // Use the URL we already fetched rather than re-reading the (possibly
                        // reordered) selected row after a background refresh.
                        if (screen.OpenBrowserRequested)
                            LaunchBrowser(detail.Url, detail.Name);
                    });
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load task detail: {Short(ex)}"));
            }
        });
    }

    private void OpenStatusPicker()
    {
        var task = CurrentTask();
        if (task is null)
            return;
        // A context-parent header (a parent not assigned to me, shown only so its subtask can nest
        // beneath it) is context, not my work — don't change its status. (#46)
        if (_contextParents.ContainsKey(task.Id))
        {
            Flash("This is a parent shown for context (not assigned to you) — status unchanged.");
            return;
        }
        if (string.IsNullOrWhiteSpace(task.ListId))
        {
            Flash("This task has no list, so its statuses can't be loaded.");
            return;
        }

        // Fast path: statuses were warmed by the background prefetch — open instantly, no round-trip.
        if (_tasks.TryGetCachedStatuses(task.ListId!, out var cached))
        {
            ShowStatusPicker(task, cached);
            return;
        }

        // Cold path: fetch off the UI thread with a loading indicator, then show the modal back on it.
        Flash("Loading statuses…");
        _ = Task.Run(async () =>
        {
            try
            {
                var statuses = await _tasks.GetStatusesForListAsync(task.ListId!);
                Application.Invoke(() => ShowStatusPicker(task, statuses));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load statuses: {Short(ex)}"));
            }
        });
    }

    /// <summary>Shows the status picker for a task and applies the choice. Must run on the UI thread.</summary>
    private void ShowStatusPicker(TaskItem task, IReadOnlyList<StatusOption> statuses)
    {
        if (statuses.Count == 0)
        {
            Flash("No statuses available for this list.");
            return;
        }

        if (_activeScreen is not null)
            return;

        var screen = new StatusPickerScreen(task.Name, statuses, task.StatusName);
        ShowScreen(screen, () =>
        {
            var chosen = screen.Chosen;
            if (chosen is null || string.Equals(chosen, task.StatusName, StringComparison.OrdinalIgnoreCase))
            {
                Flash("Status unchanged.");
                return;
            }

            ApplyStatus(task, chosen);
        });
    }

    private void ApplyStatus(TaskItem task, string status)
    {
        // Optimistic: show the new status immediately (no wait, no full reload). The actual write
        // happens off the UI thread; on success we confirm with the server's returned status, on
        // failure we revert this one row.
        UpdateTaskRow(task with { StatusName = status }, sending: true);
        Flash($"Setting '{status}'…");

        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await _tasks.SetStatusAsync(task.Id, status);
                Application.Invoke(() =>
                {
                    var final = confirmed ?? status;
                    UpdateTaskRow(task with { StatusName = final }, sending: false);
                    Flash($"Set '{task.Name}' to '{final}'.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    UpdateTaskRow(task, sending: false); // revert the optimistic change
                    Flash($"Could not set status: {Short(ex)}");
                });
            }
        });
    }

    /// <summary>
    /// Updates a single task's row in place — both the canonical snapshot (<see cref="_all"/>) and
    /// the visible ListView row — without rebuilding the list (no SetSource, so the cursor and
    /// scroll position stay put). Keeping <see cref="_all"/> and <see cref="_signature"/> in sync
    /// means the next periodic background refresh reconciles silently when the server agrees.
    /// </summary>
    private void UpdateTaskRow(TaskItem updated, bool sending)
    {
        _all = TaskService.ApplyStatusChange(_all, updated.Id, updated.StatusName);
        _signature = CurrentSignature(_all);

        var index = _rows.FindIndex(r => r?.Id == updated.Id);
        if (index < 0 || index >= _display.Count)
            return;
        _rows[index] = updated;
        // Rebuild at the row's existing depth so an in-place update keeps its nesting indent (#46).
        var (text, badge) = BuildRow(updated, index < _depths.Count ? _depths[index] : 0);
        _badges[index] = badge;
        // Mutating _display fires CollectionChanged (via the wrapper the source composes), which
        // redraws just this row; the parallel _badges entry is read during that redraw.
        _display[index] = sending ? $"{text}  (sending…)" : text;
    }

    private void ShowHelp()
    {
        if (_activeScreen is not null)
            return;
        ShowScreen(new HelpScreen(), static () => { });
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void OnTasksLoaded(IReadOnlyList<TaskItem> tasks)
    {
        _all = tasks;
        _status = $"Updated {DateTime.Now:HH:mm:ss} · {tasks.Count} task(s) · refresh every {_config.RefreshSeconds}s";

        // Warm the status cache for the lists currently on screen (best-effort, off the UI thread), so
        // pressing Space opens the picker from cache instead of paying a round-trip (#10).
        var visibleLists = tasks.Where(t => !string.IsNullOrWhiteSpace(t.ListId)).Select(t => t.ListId!);
        _ = _tasks.PrefetchStatusesAsync(visibleLists);

        // Rebuilding the ListView (SetSource) forces a full reset + redraw. Skip it when the visible
        // task set is unchanged and just update the (cheap) status line.
        var signature = CurrentSignature(tasks);
        if (signature == _signature)
        {
            _statusLabel.Text = _status;
            return;
        }
        _signature = signature;
        Render(keepTaskId: CurrentTask()?.Id);
    }

    /// <summary>
    /// The rendered fingerprint including the subtasks-view state, so toggling F4 or resolving new
    /// context parents is treated as a change (not a no-op refresh) even when the task set is identical.
    /// </summary>
    private string CurrentSignature(IReadOnlyList<TaskItem> tasks)
    {
        var sb = new System.Text.StringBuilder(BuildSignature(tasks));
        sb.Append("#sub=").Append(_config.View.ShowSubtasks);
        if (_config.View.ShowSubtasks)
            foreach (var id in _contextParents.Keys.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(';').Append(id);
        return sb.ToString();
    }

    /// <summary>A cheap fingerprint of what's actually rendered, so no-op refreshes skip a redraw.</summary>
    private static string BuildSignature(IReadOnlyList<TaskItem> tasks)
    {
        var sb = new System.Text.StringBuilder(tasks.Count * 28);
        foreach (var t in tasks)
            sb.Append(t.Id).Append(':').Append(t.StatusName).Append(':').Append(t.Name)
              .Append(':').Append(t.DueDateMs).Append(':').Append(t.UpdatedMs)
              .Append(':').Append(t.ParentId).Append('|');
        return sb.ToString();
    }

    /// <summary>The frame title, with a compact indicator of the active F3 view.</summary>
    private static string BuildFrameTitle(int pinnedCount, int todoCount, ViewSettings view)
    {
        var title = $"Tasks — {pinnedCount} pinned · {todoCount} to-do";
        var flags = new List<string>();
        if (view.Filters.Count > 0)
            flags.Add("filtered");
        if (view.SortField is { } sf)
            flags.Add($"sort {TaskFieldInfo.DisplayName(sf)} {(view.SortDirection == SortDirection.Ascending ? "↑" : "↓")}");
        if (view.GroupField is { } gf)
            flags.Add($"grouped by {TaskFieldInfo.DisplayName(gf)}");
        return flags.Count > 0 ? $"{title} · {string.Join(" · ", flags)}" : title;
    }

    /// <summary>Rebuilds the single list (focus section + to-do section) and restores the cursor.</summary>
    private void Render(string? keepTaskId)
    {
        // Pinned tasks are shown as today (unaffected by filters/grouping — explicit pins shouldn't
        // vanish); the filter/sort/group view (F3) applies to the non-pinned set. Sort applies to both.
        var view = _config.View;
        var pinned = TaskView.Sort(_all.Where(t => _focus.IsPinned(t.Id)), view.SortField, view.SortDirection);

        // The non-pinned set feeds the F3 view. When subtasks are hidden (the default), drop them here
        // so the main list stays a flat top-level view; pins are handled above so a pinned subtask is
        // never hidden. (#46)
        var nonPinned = _all.Where(t => !_focus.IsPinned(t.Id));
        if (!view.ShowSubtasks)
            nonPinned = nonPinned.Where(t => string.IsNullOrEmpty(t.ParentId));
        var groups = TaskView.Apply(nonPinned, view);
        var todoCount = groups.Sum(g => g.Tasks.Count);
        var grouped = view.GroupField is not null;
        // Nesting and field-grouping are two ways of grouping the same rows, so grouping wins: subtasks
        // only nest when shown and no F3 group is active (grouped → subtasks stay flat within groups). (#46)
        var nest = view.ShowSubtasks && !grouped;

        _rows.Clear();
        _display = new ObservableCollection<string>();
        _badges = new List<StatusBadgeListSource.Badge?>();
        _depths = new List<int>();

        if (pinned.Count > 0)
            AddHeader($"{FocusHeaderPrefix} ({pinned.Count})");
        foreach (var t in pinned)
            AddTask(t);

        foreach (var group in groups)
        {
            // A header per named group when grouping; otherwise keep today's behaviour — the single
            // tasks-section header only appears when there's a pinned section above it to separate from.
            if (grouped)
                AddHeader($"─ {(group.Label ?? "").ToUpperInvariant()} ({group.Tasks.Count}) ─");
            else if (pinned.Count > 0)
                AddHeader($"{TasksHeaderPrefix} ({todoCount}) ─");

            if (nest)
                foreach (var row in SubtaskArranger.Arrange(group.Tasks, _contextParents))
                    AddTask(row.Task, row.Depth, row.IsContextParent);
            else
                foreach (var t in group.Tasks)
                    AddTask(t);
        }

        // A custom source that draws text like the stock wrapper but overlays each [status] badge
        // with its ClickUp color. Assigning Source (rather than SetSource) lets us pass our source;
        // the ListView disposes the previous one.
        _list.Source = new StatusBadgeListSource(_display, _badges);
        _frame.Title = BuildFrameTitle(pinned.Count, todoCount, view);

        // Restore the cursor onto the same task, or the first task row.
        var target = keepTaskId is not null ? _rows.FindIndex(r => r?.Id == keepTaskId) : -1;
        if (target < 0)
            target = _rows.FindIndex(r => r is not null);
        if (target >= 0 && _display.Count > 0)
            _list.SelectedItem = target;

        _statusLabel.Text = _status;
    }

    private void AddHeader(string text)
    {
        _rows.Add(null);
        _display.Add(text);
        _badges.Add(null);
        _depths.Add(0);
    }

    private void AddTask(TaskItem task, int depth = 0, bool isContextParent = false)
    {
        var (text, badge) = BuildRow(task, depth, isContextParent);
        _rows.Add(task);
        _display.Add(text);
        _badges.Add(badge);
        _depths.Add(depth);
    }

    /// <summary>The display text and (optional) status-color badge overlay for a task row.</summary>
    private static (string Text, StatusBadgeListSource.Badge? Badge) BuildRow(
        TaskItem task, int depth = 0, bool isContextParent = false)
    {
        var row = TaskRowFormatter.Format(task, depth, isContextParent);
        return (row.Text, StatusBadgeListSource.TryCreate(row.BadgeStart, row.BadgeLength, task.StatusColor));
    }

    private void Flash(string message)
    {
        _status = message;
        _statusLabel.Text = message;
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
