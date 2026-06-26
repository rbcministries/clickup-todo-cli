using System.Collections.ObjectModel;
using System.Diagnostics;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
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
/// </summary>
public sealed class TodoApp
{
    private const string FocusHeaderPrefix = "★ CURRENT FOCUS";
    private const string TodoHeaderPrefix = "─ TO-DO";

    private readonly TaskService _tasks;
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly IFocusStore _focus;

    private Window _window = null!;
    private FrameView _frame = null!;
    private ListView _list = null!;
    private Label _statusLabel = null!;
    private RefreshService _refresh = null!;

    private IReadOnlyList<TaskItem> _all = [];
    // Parallel to the ListView's rows: the task on each row, or null for a header/separator row.
    private readonly List<TaskItem?> _rows = [];
    // The ListView's backing collection, kept so a single row can be updated in place (without
    // SetSource, which would reset the list and the cursor).
    private ObservableCollection<string> _display = [];
    // Per-row status-badge color overlay, parallel to _display (null = header row or no/!valid color).
    private List<StatusBadgeListSource.Badge?> _badges = [];
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
                fetch: ct => _tasks.LoadAsync(ct),
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

    private void Build()
    {
        _window = new Window { Title = $"ClickUp To-Do — {_config.WorkspaceName}" };

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
            Text = "↑/↓ move · Tab next section · Space status · Enter open · Ctrl+P pin · Ctrl+R refresh · F1 help · F2 settings · F3 filter/sort/group · Ctrl+Q quit · type to search",
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
                OpenInBrowser();
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
        }
    }

    private void OpenViewSettings()
    {
        var result = FilterSortGroupDialog.Show(_config.View);
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
        var result = SettingsDialog.Show(_config.RefreshSeconds, _config.ExcludedStatuses);
        if (result is null)
            return;

        _config.RefreshSeconds = result.RefreshSeconds;
        _config.ExcludedStatuses = result.ExcludedStatuses;
        _configStore.Save(_config);

        _refresh.IntervalSeconds = result.RefreshSeconds;
        Flash($"Settings saved · refresh {result.RefreshSeconds}s · {result.ExcludedStatuses.Count} status(es) excluded");
        _refresh.RequestRefresh();
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
        if (string.IsNullOrWhiteSpace(task?.Url))
        {
            Flash("No URL for this task.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(task.Url) { UseShellExecute = true });
            Flash($"Opened: {task.Name}");
        }
        catch (Exception ex)
        {
            Flash($"Could not open browser: {Short(ex)}");
        }
    }

    private void OpenStatusPicker()
    {
        var task = CurrentTask();
        if (task is null)
            return;
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

        var chosen = StatusPicker.Show(task.Name, statuses, task.StatusName);
        if (chosen is null || string.Equals(chosen, task.StatusName, StringComparison.OrdinalIgnoreCase))
        {
            Flash("Status unchanged.");
            return;
        }

        ApplyStatus(task, chosen);
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
        _signature = BuildSignature(_all);

        var index = _rows.FindIndex(r => r?.Id == updated.Id);
        if (index < 0 || index >= _display.Count)
            return;
        _rows[index] = updated;
        var (text, badge) = BuildRow(updated);
        _badges[index] = badge;
        // Mutating _display fires CollectionChanged (via the wrapper the source composes), which
        // redraws just this row; the parallel _badges entry is read during that redraw.
        _display[index] = sending ? $"{text}  (sending…)" : text;
    }

    private static void ShowHelp()
    {
        var dialog = new Dialog
        {
            Title = "Keyboard shortcuts",
            Width = Dim.Percent(70),
            Height = Dim.Percent(70),
        };
        var body = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            Text =
                "\n"
                + "  ↑ / ↓       Move between tasks\n"
                + "  (type)      Search tasks by title (type-ahead)\n"
                + "  Tab         Jump to the first task in the next section\n"
                + "  Space       Set the focused task's status\n"
                + "  Enter       Open the task in your browser\n"
                + "  Ctrl+P      Pin / unpin (pinned tasks group at the top)\n"
                + "  Ctrl+R      Refresh now\n"
                + "  F1          This help\n"
                + "  F2          Settings (refresh rate, excluded statuses)\n"
                + "  F3          Filter / sort / group the task list\n"
                + "  Ctrl+Q/Esc  Quit\n"
                + "\n"
                + "  Esc or Enter to close this help.",
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Esc or KeyCode.Enter)
            {
                key.Handled = true;
                Application.RequestStop();
            }
        };
        dialog.Add(body);
        Application.Run(dialog);
        dialog.Dispose();
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
        var signature = BuildSignature(tasks);
        if (signature == _signature)
        {
            _statusLabel.Text = _status;
            return;
        }
        _signature = signature;
        Render(keepTaskId: CurrentTask()?.Id);
    }

    /// <summary>A cheap fingerprint of what's actually rendered, so no-op refreshes skip a redraw.</summary>
    private static string BuildSignature(IReadOnlyList<TaskItem> tasks)
    {
        var sb = new System.Text.StringBuilder(tasks.Count * 24);
        foreach (var t in tasks)
            sb.Append(t.Id).Append(':').Append(t.StatusName).Append(':').Append(t.Name)
              .Append(':').Append(t.DueDateMs).Append(':').Append(t.UpdatedMs).Append('|');
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
        var groups = TaskView.Apply(_all.Where(t => !_focus.IsPinned(t.Id)), view);
        var todoCount = groups.Sum(g => g.Tasks.Count);
        var grouped = view.GroupField is not null;

        _rows.Clear();
        _display = new ObservableCollection<string>();
        _badges = new List<StatusBadgeListSource.Badge?>();

        if (pinned.Count > 0)
            AddHeader($"{FocusHeaderPrefix} ({pinned.Count})");
        foreach (var t in pinned)
            AddTask(t);

        foreach (var group in groups)
        {
            // A header per named group when grouping; otherwise keep today's behaviour — the single
            // "TO-DO" header only appears when there's a pinned section above it to separate from.
            if (grouped)
                AddHeader($"─ {(group.Label ?? "").ToUpperInvariant()} ({group.Tasks.Count}) ─");
            else if (pinned.Count > 0)
                AddHeader($"{TodoHeaderPrefix} ({todoCount}) ─");

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
    }

    private void AddTask(TaskItem task)
    {
        var (text, badge) = BuildRow(task);
        _rows.Add(task);
        _display.Add(text);
        _badges.Add(badge);
    }

    /// <summary>The display text and (optional) status-color badge overlay for a task row.</summary>
    private static (string Text, StatusBadgeListSource.Badge? Badge) BuildRow(TaskItem task)
    {
        var row = TaskRowFormatter.Format(task);
        return (row.Text, StatusBadgeListSource.TryCreate(row.BadgeStart, row.BadgeLength, task.StatusColor));
    }

    private void Flash(string message)
    {
        _status = message;
        _statusLabel.Text = message;
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
