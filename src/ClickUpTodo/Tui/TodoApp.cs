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
/// The keyboard-driven terminal UI: a pinned "Current Focus" pane above the full to-do list,
/// refreshed in the background on the configured interval. Selection is preserved by task id
/// across refreshes so the list stays visually static between updates.
/// </summary>
public sealed class TodoApp
{
    private readonly TaskService _tasks;
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly HashSet<string> _pinnedIds;

    private Window _window = null!;
    private FrameView _focusFrame = null!;
    private FrameView _todoFrame = null!;
    private ListView _focusList = null!;
    private ListView _todoList = null!;
    private Label _statusLabel = null!;
    private RefreshService _refresh = null!;

    private IReadOnlyList<TaskItem> _all = [];
    private List<TaskItem> _pinned = [];
    private List<TaskItem> _todo = [];
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
        // driverName lets the user A/B Terminal.Gui drivers to work around input latency (#3).
        Application.Init(driverName);
        try
        {
            _status = $"Loading… (driver: {driverName ?? "default (ansi)"})";
            Build();

            // Workaround for #3: the screen can lag behind state changes (input/commands run
            // immediately, but the repaint is deferred). Force a periodic repaint (~20 fps) so the
            // cursor/selection stays visually in sync. Terminal.Gui only writes changed cells, so an
            // idle redraw is cheap.
            Application.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
            {
                Application.LayoutAndDraw();
                return true;
            });

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

        _focusFrame = new FrameView
        {
            Title = "★ Current Focus",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Absolute(7),
        };
        _focusList = NewList();
        _focusFrame.Add(_focusList);

        _todoFrame = new FrameView
        {
            Title = "To-Do",
            X = 0,
            Y = Pos.Bottom(_focusFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        _todoList = NewList();
        _todoFrame.Add(_todoList);

        _statusLabel = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = _status };
        var help = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
            Text = "↑/↓ move · Tab pane · Space status · Enter open · Ctrl+P pin · Ctrl+R refresh · F1 help · F2 settings · Ctrl+Q/Esc quit · type to search",
        };

        _window.Add(_focusFrame, _todoFrame, _statusLabel, help);
        _todoList.SetFocus();
    }

    private ListView NewList()
    {
        var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        list.KeyDown += OnListKey;
        return list;
    }

    // ── Key handling ─────────────────────────────────────────────────────────

    private void OnListKey(object? sender, Key key)
    {
        // Command shortcuts use modifier chords / function keys. Bare letters are intentionally left
        // unhandled so the ListView's type-ahead search (keyed on the task title) keeps working —
        // that's why these aren't plain P/R/Q/? (those get swallowed by type-ahead).
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
                ToggleFocus();
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

    private void ToggleFocus()
    {
        if (_focusList.HasFocus && _todo.Count > 0)
            _todoList.SetFocus();
        else if (_pinned.Count > 0)
            _focusList.SetFocus();
        else
            _todoList.SetFocus();
    }

    private TaskItem? CurrentTask()
    {
        if (_focusList.HasFocus)
            return Pick(_pinned, _focusList.SelectedItem);
        return Pick(_todo, _todoList.SelectedItem);

        static TaskItem? Pick(List<TaskItem> items, int? index)
            => index is int i && i >= 0 && i < items.Count ? items[i] : null;
    }

    // ── Actions ────────────────────────────────────────────────────────────

    private void TogglePin()
    {
        var task = CurrentTask();
        if (task is null)
            return;

        if (!_pinnedIds.Remove(task.Id))
            _pinnedIds.Add(task.Id);

        _config.PinnedTaskIds = [.. _pinnedIds];
        _configStore.Save(_config);
        RenderPanes();
        Flash(_pinnedIds.Contains(task.Id) ? $"Pinned: {task.Name}" : $"Unpinned: {task.Name}");
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
        Flash($"Setting '{status}'…");
        _ = Task.Run(async () =>
        {
            try
            {
                await _tasks.SetStatusAsync(task.Id, status);
                Application.Invoke(() =>
                {
                    Flash($"Set '{task.Name}' to '{status}'.");
                    _refresh.RequestRefresh();
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not set status: {Short(ex)}"));
            }
        });
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
                + "  Tab         Switch between Focus and To-Do panes\n"
                + "  Space       Set the focused task's status\n"
                + "  Enter       Open the task in your browser\n"
                + "  Ctrl+P      Pin / unpin the focused task\n"
                + "  Ctrl+R      Refresh now\n"
                + "  F1          This help\n"
                + "  F2          Settings (refresh rate, excluded statuses)\n"
                + "  Ctrl+Q/Esc  Quit\n"
                + "\n"
                + "  Esc or Enter to close this help.",
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Esc or KeyCode.Enter || char.ToLowerInvariant((char)key.AsRune.Value) == 'q')
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

        // Rebuilding both ListViews (SetSource) forces a full reset + redraw. Doing that on every
        // background refresh — even when nothing changed — causes periodic redraws that compete with
        // keyboard/mouse input and make selection feel laggy. Skip the rebuild when the visible task
        // set is unchanged and just update the (cheap) status line.
        var signature = BuildSignature(tasks);
        if (signature == _signature)
        {
            _statusLabel.Text = _status;
            return;
        }
        _signature = signature;
        RenderPanes();
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

    private void RenderPanes()
    {
        // Capture the focused selection so the cursor stays put across the rebuild.
        var focusedPinned = _focusList.HasFocus;
        var keepId = CurrentTask()?.Id;

        _pinned = _all.Where(t => _pinnedIds.Contains(t.Id)).ToList();
        _todo = _all.Where(t => !_pinnedIds.Contains(t.Id)).ToList();

        _focusList.SetSource(new ObservableCollection<string>(_pinned.Select(Format)));
        _todoList.SetSource(new ObservableCollection<string>(_todo.Select(Format)));

        _focusFrame.Title = $"★ Current Focus ({_pinned.Count})";
        _todoFrame.Title = $"To-Do ({_todo.Count})";
        Restore(_focusList, _pinned, focusedPinned ? keepId : null);
        Restore(_todoList, _todo, focusedPinned ? null : keepId);

        _statusLabel.Text = _status;

        static void Restore(ListView list, List<TaskItem> items, string? id)
        {
            if (id is null || items.Count == 0)
                return;
            var index = items.FindIndex(t => t.Id == id);
            if (index >= 0)
                list.SelectedItem = index;
        }
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
