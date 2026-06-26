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
            Text = "↑/↓ move · Tab next section · Space status · Enter open · Ctrl+P pin · Ctrl+R refresh · F1 help · F2 settings · Ctrl+Q quit · type to search",
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
        }
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
        var pinned = _all.Where(t => _focus.IsPinned(t.Id)).ToList();
        var todo = _all.Where(t => !_focus.IsPinned(t.Id)).ToList();

        _rows.Clear();
        _display = new ObservableCollection<string>();
        _badges = new List<StatusBadgeListSource.Badge?>();

        if (pinned.Count > 0)
        {
            AddHeader($"{FocusHeaderPrefix} ({pinned.Count})");
            foreach (var t in pinned)
                AddTask(t);
            AddHeader($"{TodoHeaderPrefix} ({todo.Count}) ─");
        }
        foreach (var t in todo)
            AddTask(t);

        // A custom source that draws text like the stock wrapper but overlays each [status] badge
        // with its ClickUp color. Assigning Source (rather than SetSource) lets us pass our source;
        // the ListView disposes the previous one.
        _list.Source = new StatusBadgeListSource(_display, _badges);
        _frame.Title = $"Tasks — {pinned.Count} pinned · {todo.Count} to-do";

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
