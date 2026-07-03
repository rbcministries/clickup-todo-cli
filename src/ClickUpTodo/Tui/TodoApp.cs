using System.Collections.ObjectModel;
using System.Diagnostics;
using ClickUpTodo.Agent;
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
using Attribute = Terminal.Gui.Drawing.Attribute;

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
    // Composes the seed prompt + launches an interactive `claude` session for the detail view's A
    // keybinding (#26). Zero-config defaults today; #27 (S4) will populate its options from AppConfig.
    private readonly AgentDispatcher _agent = new(new TerminalLauncher());
    // True while a dispatch is in flight, so a rapid second submit doesn't launch a duplicate session.
    // Only touched on the UI thread (set in DispatchAgent, cleared via Application.Invoke).
    private bool _dispatching;

    private Window _window = null!;
    private FrameView _frame = null!;
    private ListView _list = null!;
    private Label _statusLabel = null!;
    private RefreshService _refresh = null!;
    // The full-window screen currently swapped in over the list (Settings / status picker / Help),
    // or null when the list is showing. Only one screen is open at a time.
    private Screen? _activeScreen;

    private IReadOnlyList<TaskItem> _all = [];
    // Parallel to the ListView's rows: the task on each row, or null for a header/spacer row.
    private readonly List<TaskItem?> _rows = [];
    // Parallel to _rows: what kind of row it is, so navigation can tell headers from blank spacers
    // (both carry a null _rows entry) and skip to real sections. (#61)
    private readonly List<RowKind> _kinds = [];
    // The ListView's backing collection, kept so a single row can be updated in place (without
    // SetSource, which would reset the list and the cursor).
    private ObservableCollection<string> _display = [];
    // Per-row badge color overlays (status + priority), parallel to _display (empty = header row or
    // no/invalid colors).
    private List<IReadOnlyList<StatusBadgeListSource.Badge>> _badges = [];
    // Per-row full-width header-bar attribute, parallel to _display (non-null only on header rows). (#61)
    private List<Attribute?> _headerAttrs = [];
    // Per-row nesting depth, parallel to _display, so an in-place row update keeps its indent (#46).
    private List<int> _depths = [];
    // Per-list color chips for List-grouped headers, resolved off the UI thread in FetchAsync and read
    // during Render; volatile to publish the reference safely across threads. (#61)
    private volatile IReadOnlyDictionary<string, string?> _listColors = EmptyListColors;
    private static readonly IReadOnlyDictionary<string, string?> EmptyListColors = new Dictionary<string, string?>();

    /// <summary>The kind of a rendered row: an actionable task, a section header, or a blank spacer.</summary>
    private enum RowKind { Task, Header, Spacer }
    // Parents of assigned subtasks that aren't themselves in the snapshot, shown as context headers in
    // the subtasks view (F4). Resolved off the UI thread (FetchAsync) while ShowSubtasks is on and read
    // on the UI thread during Render, so it's volatile to publish the reference safely across threads.
    private volatile IReadOnlyDictionary<string, TaskItem> _contextParents = EmptyParents;
    private static readonly IReadOnlyDictionary<string, TaskItem> EmptyParents = new Dictionary<string, TaskItem>();
    // Ids of the non-pinned subtasks pulled into the Current Focus section (nested under a pinned
    // parent, #75). Set during Render; read by UpdateTaskRow so an in-place status update treats a
    // pulled-in Focus row like a Focus row (keeps every segment) rather than a to-do row.
    private IReadOnlySet<string> _focusNestedIds = new HashSet<string>(StringComparer.Ordinal);
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
        // Resolve context parents whenever the subtasks view is on: they're rendered as headers whether
        // or not an F3 group is active now that grouping and nesting compose (#57). Off → skip the
        // extra round-trips.
        _contextParents = _config.View.ShowSubtasks
            ? await _tasks.ResolveContextParentsAsync(tasks, ct)
            : EmptyParents;
        // List colors are only needed to tint headers when grouping by List; skip the fetches otherwise.
        _listColors = _config.View.GroupField == TaskField.List
            ? await _tasks.ResolveListColorsAsync(tasks.Select(t => t.ListId ?? ""), ct)
            : EmptyListColors;
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

        var previous = _config.View;
        _config.View = result;
        _configStore.Save(_config);
        Flash(ViewSummary(result));

        // An Assignee rule scopes the server-side fetch (#68), so a change to the resolved assignee set
        // needs a reload — a client-side re-render can't surface tasks that were never fetched. Every
        // other rule change (status/list/due/priority/sort/group) is a pure client-side re-filter, so
        // re-render directly (BuildSignature would otherwise treat it as a no-op).
        var before = _tasks.ResolveAssigneeIds(previous);
        var after = _tasks.ResolveAssigneeIds(result);
        if (!TaskService.SameAssigneeSet(before, after))
        {
            if (after.Count == 0)
                Flash("Fetching tasks for all assignees — this may be slow.");
            _refresh.RequestRefresh();
        }
        else
        {
            Render(keepTaskId: CurrentTask()?.Id);
        }
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

        var screen = new SettingsScreen(_config.RefreshSeconds, _config.AgentDispatch);
        ShowScreen(screen, () =>
        {
            var result = screen.Result;
            if (result is null)
                return;

            _config.RefreshSeconds = result.RefreshSeconds;
            _config.AgentDispatch = result.AgentDispatch;
            _configStore.Save(_config);

            _refresh.IntervalSeconds = result.RefreshSeconds;
            Flash($"Settings saved · refresh {result.RefreshSeconds}s");
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
        // Terminal.Gui 2.4.10 can throw from View/Tabs.Dispose while tearing down a view's subviews
        // (disposing a child mutates the parent's subview list mid-iteration → IndexOutOfRange; hit
        // when Esc closes the tabbed detail view). The screen is already detached from the window
        // above, so a failed Dispose is at worst a minor leak of that screen's views — never a reason
        // to crash the app. Guard it so closing a screen can't take the process down.
        try
        {
            screen.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            Debug.WriteLine($"Screen dispose threw (Terminal.Gui teardown bug), ignoring: {ex}");
        }
        _frame.Visible = true;
        _list.SetFocus();
    }

    /// <summary>The task on the selected row, or null if a header row (or nothing) is selected.</summary>
    private TaskItem? CurrentTask()
        => _list.SelectedItem is int i && i >= 0 && i < _rows.Count ? _rows[i] : null;

    /// <summary>
    /// Moves the cursor to the first task row beneath the next header (sections are delimited by
    /// <see cref="RowKind.Header"/> rows). Wraps to the first section.
    /// </summary>
    private void JumpToNextSection()
    {
        var headers = Enumerable.Range(0, _kinds.Count).Where(i => _kinds[i] == RowKind.Header).ToList();
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
                    // A (in the detail view) → compose + launch an interactive claude session (#26).
                    // The detail view stays open; dispatch runs off the UI thread so the TUI stays live.
                    screen.AgentDispatchRequested += (_, prompt) => DispatchAgent(detail, comments, prompt);
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

    /// <summary>
    /// Composes the seed prompt for <paramref name="detail"/> and launches an interactive
    /// <c>claude</c> session in a new terminal (#26). Runs off the UI thread (file write + process
    /// launch), then reports the outcome on the status line; the detail view and background refresh
    /// keep running. Working directory / claude path / preferred terminal become configurable in #27.
    /// </summary>
    private void DispatchAgent(TaskDetail detail, IReadOnlyList<CommentItem> comments, string prompt)
    {
        // Re-entrancy guard: a second Enter before the first launch finishes would spawn a duplicate
        // claude session. This runs on the UI thread (invoked from the screen's key handler) and is
        // cleared back on the UI thread via Application.Invoke, so the plain bool needs no locking.
        if (_dispatching)
        {
            Flash("A Claude session is already launching…");
            return;
        }
        _dispatching = true;

        Flash($"Launching Claude for '{detail.Name}'…");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _agent.DispatchAsync(detail, comments, prompt);
                Application.Invoke(() => { _dispatching = false; Flash(result.StatusMessage); });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => { _dispatching = false; Flash($"Could not launch Claude: {Short(ex)}"); });
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
        // A task lives in exactly one section (nonPinned excludes pinned and Focus-nested subtasks), so
        // only a to-do row omits the grouped field; a Focus row — a pin or a subtask nested under one
        // (#75) — keeps every segment (no group header above it) (#67).
        var inFocus = _focus.IsPinned(updated.Id) || _focusNestedIds.Contains(updated.Id);
        var groupedBy = inFocus ? (TaskField?)null : _config.View.GroupField;
        var (text, badges) = BuildRow(updated, index < _depths.Count ? _depths[index] : 0, groupedBy: groupedBy);
        _badges[index] = badges;
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
        var nest = view.ShowSubtasks;

        // The pinned "Current Focus" section. When the subtasks view (F4) is on, a pinned parent's
        // in-snapshot subtasks nest indented beneath it (reusing SubtaskArranger) instead of falling
        // through to the to-do set un-indented; those pulled-in subtask ids are excluded from the
        // non-pinned set below so they don't render twice. Pins ignore F3 filters/grouping. (#75)
        var pinnedIds = _all.Where(t => _focus.IsPinned(t.Id)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var focus = FocusSectionLayout.Build(_all, pinnedIds, nest, view.SortField, view.SortDirection);
        _focusNestedIds = focus.NestedSubtaskIds;

        // The non-pinned set feeds the F3 view. Drop pinned tasks and (when nesting) any subtask pulled
        // into the Focus section above, so it renders only once. When subtasks are hidden (the default),
        // also drop them here so the main list stays a flat top-level view. (#46, #75)
        var nonPinned = _all.Where(t => !pinnedIds.Contains(t.Id) && !focus.NestedSubtaskIds.Contains(t.Id));
        if (!nest)
            nonPinned = nonPinned.Where(t => string.IsNullOrEmpty(t.ParentId));
        var groups = TaskView.Apply(nonPinned, view);
        var todoCount = groups.Sum(g => g.Tasks.Count);
        var grouped = view.GroupField is not null;
        // Grouping and nesting compose: within each F3 group, subtasks nest under their parent when
        // both fall in the same group; a subtask whose parent lands in a different group renders flat
        // within its own group (SubtaskArranger, run per-group, yields exactly this). (#46, #57)

        _rows.Clear();
        _kinds.Clear();
        _display = new ObservableCollection<string>();
        _badges = new List<IReadOnlyList<StatusBadgeListSource.Badge>>();
        _headerAttrs = new List<Attribute?>();
        _depths = new List<int>();

        // A background color per group header, by the grouped field (status/list/priority/date). Null
        // entries (and the non-field pinned/tasks headers) fall back to the neutral bar. (#61)
        var headerColors = GroupHeaderPalette.Resolve(view.GroupField, groups, _listColors);

        // The Focus header count is the number of pinned tasks (the anchors); pulled-in subtasks are
        // nested child rows, not pins. Focus rows keep every segment (no group header sits above them,
        // #67), so AddTask is called with groupedBy null.
        if (pinnedIds.Count > 0)
            AddHeader($"{FocusHeaderPrefix} ({pinnedIds.Count})");
        foreach (var row in focus.Rows)
            AddTask(row.Task, row.Depth, row.IsContextParent, groupedBy: null);

        // The single tasks-section header only appears (when ungrouped) to separate the to-do rows
        // from a pinned section above them.
        var ungroupedTasksHeader = pinnedIds.Count > 0 ? $"{TasksHeaderPrefix} ({todoCount}) ─" : null;
        foreach (var row in SectionLayout.BuildTodoSection(groups, _contextParents, grouped, nest, ungroupedTasksHeader, headerColors))
        {
            if (row.IsHeader)
                AddHeader(row.HeaderText!, row.HeaderColor);
            else
                // Omit the grouped field from each to-do row — the group header above already shows it
                // (#67). The pinned Focus section has no group headers, so its rows keep every segment.
                AddTask(row.Task!, row.Depth, row.IsContextParent, view.GroupField);
        }

        // A custom source that draws text like the stock wrapper, overlays each [status] badge with its
        // ClickUp color, and paints each group header as a full-width color bar. Assigning Source
        // (rather than SetSource) lets us pass our source; the ListView disposes the previous one.
        _list.Source = new StatusBadgeListSource(_display, _badges, _headerAttrs);
        _frame.Title = BuildFrameTitle(pinnedIds.Count, todoCount, view);

        // Restore the cursor onto the same task, or the first task row.
        var target = keepTaskId is not null ? _rows.FindIndex(r => r?.Id == keepTaskId) : -1;
        if (target < 0)
            target = _rows.FindIndex(r => r is not null);
        if (target >= 0 && _display.Count > 0)
            _list.SelectedItem = target;

        _statusLabel.Text = _status;
    }

    /// <summary>
    /// Appends a section header, preceded by a blank spacer row for breathing room (except at the very
    /// top of the list). <paramref name="hexColor"/> tints the full-width bar; a null/unparseable color
    /// falls back to the neutral bar.
    /// </summary>
    private void AddHeader(string text, string? hexColor = null)
    {
        if (_display.Count > 0)
            AddSpacer();
        _rows.Add(null);
        _kinds.Add(RowKind.Header);
        _display.Add(text);
        _badges.Add([]);
        _headerAttrs.Add(StatusBadgeListSource.HeaderAttr(hexColor) ?? StatusBadgeListSource.NeutralHeaderAttr);
        _depths.Add(0);
    }

    private void AddSpacer()
    {
        _rows.Add(null);
        _kinds.Add(RowKind.Spacer);
        _display.Add("");
        _badges.Add([]);
        _headerAttrs.Add(null);
        _depths.Add(0);
    }

    private void AddTask(TaskItem task, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null)
    {
        var (text, badges) = BuildRow(task, depth, isContextParent, groupedBy);
        _rows.Add(task);
        _kinds.Add(RowKind.Task);
        _display.Add(text);
        _badges.Add(badges);
        _headerAttrs.Add(null);
        _depths.Add(depth);
    }

    /// <summary>The display text and the row's color badge overlays (status, then priority when set).
    /// <paramref name="groupedBy"/> omits the grouped field's segment (its header already conveys it, #67).</summary>
    private static (string Text, IReadOnlyList<StatusBadgeListSource.Badge> Badges) BuildRow(
        TaskItem task, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null)
    {
        var row = TaskRowFormatter.Format(task, depth, isContextParent, groupedBy);
        var badges = new List<StatusBadgeListSource.Badge>(2);
        if (StatusBadgeListSource.TryCreate(row.StatusStart, row.StatusLength, task.StatusColor) is { } status)
            badges.Add(status);
        if (StatusBadgeListSource.TryCreate(row.PriorityStart, row.PriorityLength, task.PriorityColor) is { } priority)
            badges.Add(priority);
        return (row.Text, badges);
    }

    private void Flash(string message)
    {
        _status = message;
        _statusLabel.Text = message;
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
