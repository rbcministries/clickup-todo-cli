using System.Collections.ObjectModel;
using System.Diagnostics;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
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
    private readonly HashSet<string> _pinnedIds;

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
    private string _status = "Loading…";
    private string _signature = "";

    public TodoApp(TaskService tasks, AppConfig config, ConfigStore configStore)
    {
        _tasks = tasks;
        _config = config;
        _configStore = configStore;
        _pinnedIds = [.. config.PinnedTaskIds];
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
            Text = "↑/↓ move · Tab next section · Space status · Enter detail · Ctrl+B browser · Ctrl+P pin · Ctrl+R refresh · F1 help · F2 settings · Ctrl+Q quit · type to search",
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
        }
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

        bool nowPinned;
        if (_pinnedIds.Remove(task.Id))
            nowPinned = false;
        else
        {
            _pinnedIds.Add(task.Id);
            nowPinned = true;
        }

        _config.PinnedTaskIds = [.. _pinnedIds];
        _configStore.Save(_config);
        Render(keepTaskId: task.Id);
        Flash(nowPinned ? $"Pinned: {task.Name}" : $"Unpinned: {task.Name}");
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

    private void OpenDetail()
    {
        var task = CurrentTask();
        if (task is null)
            return;

        Flash("Loading details…");
        // Fetch the detail + comments off the UI thread, then show the modal back on it. The
        // background dashboard refresh keeps running while the modal is open.
        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailAsync(task.Id);
                var comments = await _tasks.GetTaskCommentsAsync(task.Id);
                Application.Invoke(() =>
                {
                    var openBrowser = TaskDetailView.Show(detail, comments);
                    Flash($"Closed detail: {task.Name}");
                    if (openBrowser)
                        OpenInBrowser();
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
        if (string.IsNullOrWhiteSpace(task.ListId))
        {
            Flash("This task has no list, so its statuses can't be loaded.");
            return;
        }

        Flash("Loading statuses…");
        // Fetch statuses off the UI thread, then show the modal back on it.
        _ = Task.Run(async () =>
        {
            try
            {
                var statuses = await _tasks.GetStatusesForListAsync(task.ListId!);
                Application.Invoke(() =>
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
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load statuses: {Short(ex)}"));
            }
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
        _signature = BuildSignature(_all);

        var index = _rows.FindIndex(r => r?.Id == updated.Id);
        if (index < 0 || index >= _display.Count)
            return;
        _rows[index] = updated;
        _display[index] = sending ? $"{Format(updated)}  (sending…)" : Format(updated);
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
                + "  Enter       Open the task detail view (description, comments, attributes)\n"
                + "  Ctrl+B      Open the task in your browser\n"
                + "  Ctrl+P      Pin / unpin (pinned tasks group at the top)\n"
                + "  Ctrl+R      Refresh now\n"
                + "  F1          This help\n"
                + "  F2          Settings (refresh rate, excluded statuses)\n"
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
              .Append(':').Append(t.DueDateMs).Append('|');
        return sb.ToString();
    }

    /// <summary>Rebuilds the single list (focus section + to-do section) and restores the cursor.</summary>
    private void Render(string? keepTaskId)
    {
        var pinned = _all.Where(t => _pinnedIds.Contains(t.Id)).ToList();
        var todo = _all.Where(t => !_pinnedIds.Contains(t.Id)).ToList();

        _rows.Clear();
        _display = new ObservableCollection<string>();

        if (pinned.Count > 0)
        {
            AddHeader(_display, $"{FocusHeaderPrefix} ({pinned.Count})");
            foreach (var t in pinned)
                AddTask(_display, t);
            AddHeader(_display, $"{TodoHeaderPrefix} ({todo.Count}) ─");
        }
        foreach (var t in todo)
            AddTask(_display, t);

        _list.SetSource(_display);
        _frame.Title = $"Tasks — {pinned.Count} pinned · {todo.Count} to-do";

        // Restore the cursor onto the same task, or the first task row.
        var target = keepTaskId is not null ? _rows.FindIndex(r => r?.Id == keepTaskId) : -1;
        if (target < 0)
            target = _rows.FindIndex(r => r is not null);
        if (target >= 0 && _display.Count > 0)
            _list.SelectedItem = target;

        _statusLabel.Text = _status;
    }

    private void AddHeader(ObservableCollection<string> display, string text)
    {
        _rows.Add(null);
        display.Add(text);
    }

    private void AddTask(ObservableCollection<string> display, TaskItem task)
    {
        _rows.Add(task);
        display.Add(Format(task));
    }

    private void Flash(string message)
    {
        _status = message;
        _statusLabel.Text = message;
    }

    private static string Format(TaskItem task)
    {
        // Title leads so the ListView's type-ahead search matches on the task title.
        var status = string.IsNullOrWhiteSpace(task.StatusName) ? "" : $"  [{task.StatusName}]";
        var list = string.IsNullOrWhiteSpace(task.ListName) ? "" : $"  · {task.ListName}";
        var due = task.DueDateMs is { } ms
            ? $"  · due {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MMM d}"
            : "";
        return $"{task.Name}{status}{list}{due}";
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
