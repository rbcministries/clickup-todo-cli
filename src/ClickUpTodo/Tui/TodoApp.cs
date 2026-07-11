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
using Terminal.Gui.Text;
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
    private readonly FeedService _feed;
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly IFocusStore _focus;
    // Composes the seed prompt + launches an interactive `claude` session for the detail view's A
    // keybinding (#26). Built from the persisted AgentDispatch settings (#91) and rebuilt after the F2
    // settings dialog saves, so a custom terminal / claude path / extra args apply without a restart.
    private AgentDispatcher _agent = null!;
    // True while a dispatch is in flight, so a rapid second submit doesn't launch a duplicate session.
    // Only touched on the UI thread (set in DispatchAgent, cleared via Application.Invoke).
    private bool _dispatching;
    // True while a feed / detail auto- or manual refresh fetch is outstanding, so ticks coalesce
    // instead of piling up when a fan-out outlasts the cadence. UI-thread-only (like _dispatching).
    private bool _refreshingFeed;
    private bool _refreshingDetail;

    private Window _window = null!;
    private FrameView _frame = null!;
    private ListView _list = null!;
    private Label _statusLabel = null!;
    // The single window-owned contextual help line (#103): shows the active screen's shortcuts, or the
    // list's when no screen is open. One shared bottom row — screens no longer hand-roll their own.
    private Label _helpLabel = null!;
    private RefreshService _refresh = null!;
    // The stack of full-window screens swapped in over the list (Settings / status picker / detail /
    // Help). The top is visible + focused; any beneath it are mounted-but-hidden so we can return to
    // them (F1 opens Help *over* the current screen and Esc pops back to it with its state intact).
    // Still one visible/focusable screen at a time within the single toplevel (no nested run loop) —
    // the #3/#38 invariants hold. List-initiated opens guard on ActiveScreen, so only Help ever stacks.
    private readonly List<Screen> _screens = [];

    /// <summary>The screen currently on top (visible + focused), or null when the task list is showing.</summary>
    private Screen? ActiveScreen => _screens.Count > 0 ? _screens[^1] : null;

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
    // Per-row fold state, parallel to _display, so ←/→ can read the selected row's state and an in-place
    // update reproduces the correct ▶/▼ marker (#76). None on headers/spacers/leaves/context parents.
    private List<FoldState> _folds = [];
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
    // Teammate-owned subtasks of my in-view parents, pulled in regardless of assignee when the F4
    // subtasks view and the ShowAllSubtasksOfAssignedParents setting are both on (#70). Keyed by id;
    // resolved off the UI thread (FetchAsync) and read on the UI thread during Render, so volatile like
    // _contextParents. They render as not-mine rows nested under their parent and aren't my work
    // (status/pin are blocked on them).
    private volatile IReadOnlyDictionary<string, TaskItem> _foreignSubtasks = EmptyParents;
    // Set when the adaptive foreign-subtask fetch hit a round-trip cap (#87) and omitted some subtasks;
    // surfaced as a note on the post-refresh status line so the truncation isn't silent. Written in
    // FetchAsync (off-thread) and read on the UI thread in OnTasksLoaded, so volatile like _foreignSubtasks.
    private volatile bool _foreignSubtasksTruncated;
    // Ids of the non-pinned subtasks pulled into the Current Focus section (nested under a pinned
    // parent, #75). Set during Render; read by UpdateTaskRow so an in-place status update treats a
    // pulled-in Focus row like a Focus row (keeps every segment) rather than a to-do row.
    private IReadOnlySet<string> _focusNestedIds = new HashSet<string>(StringComparer.Ordinal);
    // Ids of parents the user has expanded this session (#76). Empty = all collapsed (the default). Only
    // meaningful while the subtasks view (F4) is on; ephemeral (never persisted to config, per the issue).
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private string _status = "Loading…";
    private string _signature = "";

    public TodoApp(TaskService tasks, FeedService feed, AppConfig config, ConfigStore configStore, IFocusStore focus)
    {
        _tasks = tasks;
        _feed = feed;
        _config = config;
        _configStore = configStore;
        _focus = focus;
        _agent = BuildAgentDispatcher();
    }

    // Builds the dispatcher from the current AgentDispatch settings (#91), so the preferred terminal,
    // custom claude executable, and extra args take effect. Zero-config settings project onto the
    // default TerminalLauncherOptions, keeping behaviour byte-for-byte identical. A field initializer
    // can't read _config, so this runs from the constructor (and again after an F2 settings save).
    private AgentDispatcher BuildAgentDispatcher() =>
        new(new TerminalLauncher(), _config.AgentDispatch.ToLauncherOptions());

    public void Run(string? driverName = null)
    {
        // driverName lets the user pick a Terminal.Gui driver (windows/dotnet/ansi); null = default.
        // For the ANSI driver (the default on every platform) install the frame-diffing output
        // first: the stock backend re-sends every visible cell on any list redraw (~18 KB per
        // arrow keypress), which makes navigation output-bound — and visibly laggy — on slow
        // terminals/links. Diffing trims that to just the rows that changed (~0.9 KB); changed
        // rows flush whole, byte-identical to stock (see DiffFlushAnsiOutput docs). Best-effort:
        // if the install fails (e.g. a future Terminal.Gui moved its internals) we run the stock
        // driver. CLICKUP_TODO_NO_DIFF=1 is the escape hatch if a terminal misbehaves with it.
        var diffing = (driverName is null or "ansi")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLICKUP_TODO_NO_DIFF"))
            && DiffFlushAnsiBackend.TryInstall();
        Application.Init(driverName);
        try
        {
            _status = $"Loading… (driver: {driverName ?? "default (ansi)"}{(diffing ? ", diffed output" : "")})";
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
            // Application.Shutdown restores the terminal (cooked mode, alt-screen off), so it must run no
            // matter how _window.Dispose fares — hence the nested try/finally. Terminal.Gui 2.4.10 can
            // throw ArgumentOutOfRange from View/Tabs.Dispose while tearing down a tabbed view's subviews
            // (the same bug CloseScreen guards); any screen open at quit is still mounted here, so swallow
            // that known teardown bug (at worst a leak of views the process is about to drop anyway). An
            // unexpected exception still propagates — but only after Shutdown has restored the terminal.
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

    /// <summary>
    /// Every this-many consecutive delta polls, force a full re-fetch (#194). A delta can't see a task
    /// leaving the fetch's scope without a matching update (e.g. unassigned from me by someone else),
    /// so the resync bounds that staleness: at the default poll interval, minutes, not forever. Manual
    /// refresh (r/F5) and fetch-rule changes are always full, so this is only the ceiling.
    /// </summary>
    internal const int FullResyncEveryNthPoll = 10;

    // Consecutive delta polls since the last full load; only ever touched by FetchAsync (the refresh
    // loop runs fetches strictly one at a time).
    private int _deltaPollsSinceFullLoad;

    /// <summary>
    /// Background fetch for the refresh loop: loads the task snapshot — incrementally on background
    /// polls, in full on the initial load, a manual refresh, or the periodic resync (#194) — and, when
    /// the nested subtasks view is on, resolves any parents not in the snapshot so they can be shown as
    /// context headers. Runs off the UI thread; <see cref="_contextParents"/> is set before the result
    /// is marshalled in.
    /// </summary>
    private async Task<IReadOnlyList<TaskItem>> FetchAsync(RefreshKind kind, CancellationToken ct)
    {
        // Load incrementally on background polls, in full otherwise (#194). The resync counter forces a
        // periodic full fetch so a delta can't hide a task that left scope without a matching update.
        var preferDelta = kind == RefreshKind.Poll && _deltaPollsSinceFullLoad < FullResyncEveryNthPoll;
        var snapshot = await _tasks.LoadSnapshotAsync(preferDelta, ct);
        _deltaPollsSinceFullLoad = snapshot.WasDelta ? _deltaPollsSinceFullLoad + 1 : 0;

        // A provably-empty delta means the world hasn't changed: keep the previous resolver results
        // (context parents / foreign subtasks / list colors) instead of re-spending their round-trips.
        // Anything that changes what the resolvers should see (F3/F4/grouping edits) triggers a manual
        // refresh, which is never a delta.
        if (snapshot is { WasDelta: true, Changed: false })
            return snapshot.Tasks;

        var tasks = snapshot.Tasks;

        // The three snapshot-dependent resolvers below only depend on `tasks`, never on each other, so
        // they start together and are awaited together (#192): the refresh pays for the slowest stage
        // rather than their sum. Each keeps its feature gate — a disabled feature costs zero round-trips.
        //
        // Context parents resolve whenever the subtasks view is on: they're rendered as headers whether
        // or not an F3 group is active now that grouping and nesting compose (#57).
        var parentsFetch = _config.View.ShowSubtasks
            ? _tasks.ResolveContextParentsAsync(tasks, ct)
            : Task.FromResult(EmptyParents);
        // A parent's subtasks (regardless of assignee) are pulled in whenever the subtasks view is on at
        // all (#70, #179): both F4 on-states need the full set — "all" shows every pulled-in child, while
        // "mine + unassigned" filters it down to the unassigned ones at render. Hidden fetches nothing.
        var foreignFetch = _config.View.Subtasks != SubtaskView.Hidden
            ? _tasks.ResolveForeignSubtasksAsync(tasks, ct: ct)
            : Task.FromResult(NoForeignSubtasks);
        // List colors are only needed to tint headers when grouping by List.
        var colorsFetch = _config.View.GroupField == TaskField.List
            ? _tasks.ResolveListColorsAsync(tasks.Select(t => t.ListId ?? ""), ct)
            : Task.FromResult(EmptyListColors);

        // WhenAll (rather than awaiting in turn) so a fault in one resolver still observes the others.
        await Task.WhenAll(parentsFetch, foreignFetch, colorsFetch);

        _contextParents = await parentsFetch;
        var foreign = await foreignFetch;
        // Keyed by id for fast Render/guard lookups.
        _foreignSubtasks = foreign.Subtasks.Count == 0
            ? EmptyParents
            : foreign.Subtasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _foreignSubtasksTruncated = foreign.Truncated;
        _listColors = await colorsFetch;
        return tasks;
    }

    // Shared "feature off / nothing found" result for the foreign-subtask resolver above.
    private static readonly ForeignSubtaskResolution NoForeignSubtasks = new([], false);

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
        // The single contextual help line (#103). Seeded with the list's shortcuts; UpdateHelpLine swaps
        // in the active screen's shortcuts on show and back to the list's on close. Format of the list
        // set is byte-for-byte the pre-#103 text, so the default footer is unchanged.
        _helpLabel = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
            Text = HelpLine.Format(HelpItemSets.MainList),
        };

        _window.Add(_frame, _statusLabel, _helpLabel);
        // Re-fit the help line whenever the window re-lays out (i.e. on terminal resize). Terminal.Gui
        // 2.4 has no static Application size-changed event; SubViewsLaidOut is the framework's
        // post-layout hook. UpdateHelpLine only reassigns the text when it changed, so this can't loop.
        _window.SubViewsLaidOut += (_, _) => UpdateHelpLine();
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
                    // Ctrl+R is the (undisplayed) alias for the F5 refresh key.
                    key.Handled = true;
                    RequestRefresh();
                    break;
                case KeyCode.E:
                    // Ctrl+E toggles to the mentions & comments feed — List ↔ Feed navigation.
                    key.Handled = true;
                    OpenNotificationsFeed();
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
                case KeyCode.CursorRight:
                    // Ctrl+→/Ctrl+← = expand-all / collapse-all — the bulk counterpart to the per-parent
                    // →/← fold (#83). Active only while the subtasks view is on; off, they stay unhandled
                    // and fall through to the ListView's native handling (like the bare arrows).
                    if (_config.View.ShowSubtasks && ActiveScreen is null)
                    {
                        key.Handled = true;
                        ExpandAll();
                    }
                    break;
                case KeyCode.CursorLeft:
                    if (_config.View.ShowSubtasks && ActiveScreen is null)
                    {
                        key.Handled = true;
                        CollapseAll();
                    }
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
            case KeyCode.CursorRight:
                // ←/→ drive per-parent fold only while the subtasks view is on (#76); off, they fall
                // through to the ListView's native horizontal scroll.
                if (_config.View.ShowSubtasks && ActiveScreen is null)
                {
                    key.Handled = true;
                    ExpandOrEnter();
                }
                break;
            case KeyCode.CursorLeft:
                if (_config.View.ShowSubtasks && ActiveScreen is null)
                {
                    key.Handled = true;
                    CollapseOrJumpToParent();
                }
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
                CycleSubtaskView();
                break;
            case KeyCode.F5:
                // F5 is the refresh key (icon ↻); Ctrl+R is its undisplayed alias.
                key.Handled = true;
                RequestRefresh();
                break;
            case KeyCode.F6:
                key.Handled = true;
                CycleBadgeDisplay();
                break;
        }
    }

    /// <summary>F6 — cycles how Status/Priority badges render (icons → text → hidden → icons), persists
    /// the choice, and re-renders. A pure display toggle: it re-decorates the same rows, so it keeps the
    /// cursor on the current task.</summary>
    private void CycleBadgeDisplay()
    {
        if (ActiveScreen is not null)
            return;

        var mode = _config.BadgeDisplay.Next();
        _config.BadgeDisplay = mode;
        _configStore.Save(_config);
        Flash(mode.Describe());
        Render(keepTaskId: CurrentTask()?.Id);
    }

    /// <summary>F5 (and its Ctrl+R alias) — refresh now: flashes and wakes the background poll loop.</summary>
    private void RequestRefresh()
    {
        Flash("Refreshing…");
        _refresh.RequestRefresh();
    }

    /// <summary>
    /// Ctrl+E — opens the mentions &amp; comments feed screen (#114, epic #109), the List ↔ Feed
    /// navigation key. Fetches the feed off the UI thread (like <see cref="OpenDetail"/>: flash →
    /// <c>Task.Run</c> → <c>Application.Invoke</c>) and swaps in the data-bearing screen back on it; the
    /// background dashboard refresh keeps running. Opens through the shared screen seam, guarded on
    /// <see cref="ActiveScreen"/> like the other list-initiated opens. The full feed is loaded once
    /// (every entry mention-stamped), so the screen's F3 mentions-only toggle filters locally with no
    /// re-fetch. Loading and error states show on the status line — the screen is only constructed on
    /// success.
    /// </summary>
    private void OpenNotificationsFeed()
    {
        if (ActiveScreen is not null)
            return;

        Flash("Loading feed…");
        _ = Task.Run(async () =>
        {
            try
            {
                var feed = await _feed.LoadFeedAsync(mentionsOnly: false);
                Application.Invoke(() =>
                {
                    if (ActiveScreen is not null)
                        return;
                    // The feed auto-refreshes on the same cadence as the dashboard list (#114 follow-up);
                    // F5 / Ctrl+R force one. RefreshFeed re-fetches and feeds the result back in place.
                    var screen = new NotificationsFeedScreen(feed, _config.RefreshSeconds);
                    screen.RefreshRequested += (_, _) => RefreshFeed(screen);
                    ShowScreen(screen, static () => { });
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load feed: {Short(ex)}"));
            }
        });
    }

    /// <summary>
    /// Re-fetches the feed for an open <see cref="NotificationsFeedScreen"/> (its F5 / Ctrl+R or
    /// auto-refresh tick) and feeds it back on the UI thread. Mirrors <see cref="OpenNotificationsFeed"/>'s
    /// off-thread fetch; skips while the feed isn't front-most, and drops the result if the screen has
    /// since been torn down. A fetch error flashes without disturbing the view.
    /// </summary>
    private void RefreshFeed(NotificationsFeedScreen screen)
    {
        // Runs on the UI thread (from the screen's key handler or its timer tick), so ActiveScreen is a
        // valid read: no point fetching to update a feed that isn't showing.
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        // Coalesce: the feed fan-out (a comment fetch per assigned task) can outlast the refresh cadence
        // on a large workspace. Skip a tick while one is still in flight so ticks don't pile up and
        // multiply API load — and so an earlier fetch can't land after a later one with stale data. The
        // flag is only touched on the UI thread (here and the finally's Invoke), so no locking is needed.
        if (_refreshingFeed)
            return;
        _refreshingFeed = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var feed = await _feed.LoadFeedAsync(mentionsOnly: false);
                Application.Invoke(() =>
                {
                    if (_screens.Contains(screen))
                        screen.UpdateFeed(feed);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh feed: {Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() => _refreshingFeed = false);
            }
        });
    }

    /// <summary>
    /// Cycles the three-state subtasks view (F4, #179): mine + unassigned -> all -> hidden -> …, persisting
    /// the choice. Both on-states fetch the full pulled-in subtask set (so the "mine + unassigned" state can
    /// surface unassigned children); the render then filters it per state, so switching between the two
    /// on-states is a pure client-side re-render. Turning the view on from Hidden needs a refresh to fetch
    /// the context parents and pulled-in subtasks that a client-side re-render can't invent.
    /// </summary>
    private void CycleSubtaskView()
    {
        if (ActiveScreen is not null)
            return;

        var previous = _config.View.Subtasks;
        var next = previous.Next();
        _config.View.Subtasks = next;
        _configStore.Save(_config);
        Flash(next.Describe());

        // Hidden drops the pulled-in resolvers so the list returns to a flat top-level view immediately,
        // without waiting on a refresh.
        if (next == SubtaskView.Hidden)
        {
            _contextParents = EmptyParents;
            _foreignSubtasks = EmptyParents;
            _foreignSubtasksTruncated = false;
        }
        Render(keepTaskId: CurrentTask()?.Id);
        _signature = CurrentSignature(_all);
        // Only Hidden -> on needs a fetch; between the two on-states the full set is already resolved and
        // the render filter alone changes what's shown.
        if (previous == SubtaskView.Hidden)
            _refresh.RequestRefresh();
    }

    private void OpenViewSettings()
    {
        if (ActiveScreen is not null)
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

        // An Assignee rule scopes the server-side fetch (#68), so a change to the assignee rules needs a
        // reload — a client-side re-render can't surface tasks that were never fetched. Every other rule
        // change (status/list/due/priority/sort/group) is a pure client-side re-filter, so re-render
        // directly (BuildSignature would otherwise treat it as a no-op). We compare the raw Assignee IS
        // values (not resolved ids) so a change to a username/email — resolved to an id only at fetch
        // time (#73) — is never missed.
        var before = TaskService.AssigneeRuleValues(previous);
        var after = TaskService.AssigneeRuleValues(result);
        var assigneeChanged = !before.SetEquals(after);

        // The subtasks view is owned by F4 (#179), not this screen, so F3 never changes it — the pulled-in
        // subtask set is unaffected here and only an assignee-rule change needs a refetch (a client-side
        // re-render can't surface tasks never fetched).
        if (assigneeChanged)
        {
            if (assigneeChanged && after.Count == 0)
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
        if (ActiveScreen is not null)
            return;

        var screen = new SettingsScreen(_config.RefreshSeconds, _config.DefaultWorkingDirectory, _config.AgentDispatch, _config.DetailView);

        // Opening the prompt-template editor (#100) stacks it over the settings screen (like Help). On
        // save it folds the edited template back into the settings screen via the request's callback, so
        // the settings screen's own Save is the transaction boundary (an F2 Cancel discards the edit).
        screen.EditPromptTemplateRequested += (_, req) =>
        {
            var editor = new PromptTemplateEditorScreen(req.CurrentTemplate);
            ShowScreen(editor, () =>
            {
                if (editor.Result is not null)
                    req.Apply(editor.Result);
            });
        };

        ShowScreen(screen, () =>
        {
            var result = screen.Result;
            if (result is null)
                return;

            _config.RefreshSeconds = result.RefreshSeconds;
            _config.DefaultWorkingDirectory = result.DefaultWorkingDirectory;
            _config.AgentDispatch = result.AgentDispatch;
            _config.DetailView = result.DetailView;
            _configStore.Save(_config);

            // Rebuild the dispatcher so edited terminal / claude path / extra args apply without a
            // restart (#91). Runs on the UI thread; DispatchAgent captures _agent into a local before
            // its background hand-off, so an in-flight dispatch keeps the instance it started with.
            _agent = BuildAgentDispatcher();

            _refresh.IntervalSeconds = result.RefreshSeconds;
            Flash($"Settings saved · refresh {result.RefreshSeconds}s");
            _refresh.RequestRefresh();
        });
    }

    // ── Screen navigation seam ─────────────────────────────────────────────────
    // Swaps a full-window screen in over the list within the single toplevel (no nested
    // Application.Run). #17's detail view builds on this. See the class header / #38.

    /// <summary>
    /// Mounts a screen on top of the stack: hides whatever is currently visible (the list frame, or the
    /// screen already showing), adds the screen to the window, updates the shared help line, and
    /// focuses it. When the screen raises <see cref="Screen.Closed"/>, <paramref name="onClosed"/> runs
    /// (to read any result) and then the screen beneath it — or the list — is restored.
    /// <para>
    /// Callers that open a screen from the list guard on <see cref="ActiveScreen"/>, so in practice only
    /// Help (via F1, <see cref="OnScreenHelpRequested"/>) ever stacks on top of another screen.
    /// </para>
    /// </summary>
    private void ShowScreen(Screen screen, Action onClosed)
    {
        // Hide the currently-visible layer so only the new top draws/focuses (one visible screen at a
        // time — #3). It stays mounted so Esc can return to it with its state intact.
        if (_screens.Count == 0)
            _frame.Visible = false;
        else
            _screens[^1].Visible = false;

        _screens.Add(screen);

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            // Guard against a double-fire (e.g. two Esc presses before teardown runs).
            if (!_screens.Contains(screen))
                return;
            screen.Closed -= handler;
            // Defer teardown out of the screen's own key handler: disposing the view mid-keypress
            // can leave Terminal.Gui's input/focus machinery pointing at a freed view. Running on the
            // next loop iteration lets the current input cycle finish first.
            Application.Invoke(() =>
            {
                onClosed();          // read the screen's result while it's still intact
                CloseScreen(screen); // then tear it down and restore the layer beneath it
            });
        };
        screen.Closed += handler;
        // The shared footer + status line replace each screen's hand-rolled hint Label (#103).
        screen.FlashRequested += OnScreenFlash;
        screen.HelpRequested += OnScreenHelpRequested;

        _window.Add(screen);
        UpdateHelpLine();
        screen.OnShown();
    }

    /// <summary>
    /// Tears down <paramref name="screen"/> (which must be on the stack) and restores the layer beneath
    /// it — the screen below, or the task list with its cursor intact when the stack empties.
    /// </summary>
    private void CloseScreen(Screen screen)
    {
        if (!_screens.Remove(screen))
            return;

        screen.FlashRequested -= OnScreenFlash;
        screen.HelpRequested -= OnScreenHelpRequested;
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

        // Restore the layer beneath: the screen now on top, or the list when the stack is empty.
        if (_screens.Count > 0)
        {
            var below = _screens[^1];
            below.Visible = true;
            below.SetFocus();
        }
        else
        {
            _frame.Visible = true;
            _list.SetFocus();
        }
        UpdateHelpLine();
    }

    /// <summary>Routes a screen's transient message (e.g. a validation error) to the status line.</summary>
    private void OnScreenFlash(object? sender, string message) => Flash(message);

    /// <summary>
    /// F1 from a screen opens Help stacked over it (Esc returns to the underlying screen). Ignored when
    /// Help is already on top so F1-in-Help can't restack it.
    /// </summary>
    private void OnScreenHelpRequested(object? sender, EventArgs e)
    {
        if (ActiveScreen is HelpScreen)
            return;
        ShowScreen(new HelpScreen(), static () => { });
    }

    /// <summary>
    /// Sets the shared help line to the active screen's shortcuts (or the list's when idle), fitted to
    /// the footer's current width (#H2/#104): when they don't all fit, the trailing item becomes
    /// <c>F1 Help + Shortcuts</c>. Widths are measured column-aware (<c>GetColumns</c>) so the footer's
    /// emoji/wide glyphs count correctly. Re-runs on resize via <c>_window.SubViewsLaidOut</c>; the text
    /// is only reassigned when it actually changes, so that layout pass can't loop.
    /// </summary>
    private void UpdateHelpLine()
    {
        var items = HelpLine.ForActiveScreen(ActiveScreen?.HelpItems, HelpItemSets.MainList);
        // The label's laid-out content width. Before the first layout it's 0 — render the full set and
        // let the first SubViewsLaidOut re-fit it.
        var width = _helpLabel.Frame.Width;
        var text = width > 0
            ? HelpLine.Format(HelpLine.Fit(items, width, static s => s.GetColumns()))
            : HelpLine.Format(items);
        if (_helpLabel.Text != text)
            _helpLabel.Text = text;
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

    /// <summary>
    /// → in the subtasks view (#76): expand a collapsed parent's subtasks; on an already-expanded parent
    /// move the cursor into its first child; otherwise a no-op.
    /// </summary>
    private void ExpandOrEnter()
    {
        var i = _list.SelectedItem ?? -1;
        if (i < 0 || i >= _folds.Count)
            return;

        switch (_folds[i])
        {
            case FoldState.Collapsed when _rows[i]?.Id is { } id:
                _expanded.Add(id);
                Render(keepTaskId: id);
                break;
            case FoldState.Expanded:
                // Move into the first child — the next row indented deeper than this one.
                var depth = i < _depths.Count ? _depths[i] : 0;
                if (i + 1 < _rows.Count && _rows[i + 1] is not null && _depths[i + 1] > depth)
                    _list.SelectedItem = i + 1;
                break;
        }
    }

    /// <summary>
    /// ← in the subtasks view (#76): collapse an expanded parent; otherwise jump to (and collapse) the
    /// selected row's foldable parent — a context parent (never foldable) is jumped to without collapsing.
    /// </summary>
    private void CollapseOrJumpToParent()
    {
        var i = _list.SelectedItem ?? -1;
        if (i < 0 || i >= _folds.Count)
            return;

        if (_folds[i] == FoldState.Expanded && _rows[i]?.Id is { } id)
        {
            _expanded.Remove(id);
            Render(keepTaskId: id);
            return;
        }

        // On a child / leaf / collapsed row: hop to its parent. A visible child's parent is always
        // expanded (or a context parent), so this collapses it and lands the cursor on it.
        var parentId = _rows[i]?.ParentId;
        if (string.IsNullOrEmpty(parentId))
            return;
        var j = _rows.FindIndex(r => r?.Id == parentId);
        if (j < 0)
            return;
        if (_folds[j] == FoldState.Expanded)
        {
            _expanded.Remove(parentId!);
            Render(keepTaskId: parentId);
        }
        else
        {
            _list.SelectedItem = j; // context parent (not foldable) — just select it
        }
    }

    /// <summary>
    /// Ctrl+→ in the subtasks view (#83): expand every foldable parent in the current view at once.
    /// Foldable parents are derived from the whole candidate universe (the snapshot ∪ pulled-in foreign
    /// subtasks), not just the rendered rows, so parents whose collapsed subtree is currently hidden are
    /// reached too. Extra ids that aren't foldable in a given section are harmlessly ignored by the
    /// arranger. The selected row stays visible (expanding only reveals more), so the cursor is kept.
    /// </summary>
    private void ExpandAll()
    {
        var foldable = SubtaskArranger.FoldableParentIds(CandidateUniverse());
        if (foldable.Count == 0)
        {
            Flash("No subtasks to expand.");
            return;
        }
        _expanded.UnionWith(foldable);
        RenderKeepingCursor(CurrentTask()?.Id);
        Flash("Expanded all subtasks.");
    }

    /// <summary>
    /// Ctrl+← in the subtasks view (#83): collapse every parent at once by clearing the expanded set (the
    /// default state). The cursor is kept on the selected task's top-level ancestor, since a nested
    /// child's own row disappears once its parents fold; a child under a (never-folded) context parent
    /// stays put.
    /// </summary>
    private void CollapseAll()
    {
        if (_expanded.Count == 0)
        {
            Flash("All subtasks already collapsed.");
            return;
        }
        // Resolve the ancestor against the same universe once (avoids rebuilding it), then fold.
        var keep = CurrentTask()?.Id is { } id
            ? SubtaskArranger.TopLevelAncestorId(CandidateUniverse(), id)
            : null;
        _expanded.Clear();
        RenderKeepingCursor(keep);
        Flash("Collapsed all subtasks.");
    }

    /// <summary>
    /// <see cref="Render"/>, but when there's no task row to anchor to (<paramref name="keepTaskId"/> is
    /// null because the cursor sat on a header/spacer) keep the cursor near where it was instead of letting
    /// Render fall back to the very first row — a bulk fold shouldn't teleport the cursor to the top (#83).
    /// The row index is clamped to the (possibly shorter) new list.
    /// </summary>
    private void RenderKeepingCursor(string? keepTaskId)
    {
        var priorRow = _list.SelectedItem ?? -1;
        Render(keepTaskId);
        if (keepTaskId is null && priorRow >= 0 && _display.Count > 0)
            _list.SelectedItem = Math.Min(priorRow, _display.Count - 1);
    }

    /// <summary>Every task that can appear in the subtasks view — the snapshot plus the pulled-in
    /// subtasks currently <em>visible</em> under the active F4 state (#70, #179) — the universe over which
    /// foldable parents and ancestor walks are computed. Uses <see cref="VisibleForeignSubtasks"/> so
    /// fold/expand-all reasons about the same rows the render shows (in "mine + unassigned" the
    /// others-only children are neither rendered nor fold candidates). The two sources are disjoint with
    /// unique ids: foreign-subtask resolution excludes snapshot ids and de-dupes.</summary>
    private IReadOnlyList<TaskItem> CandidateUniverse()
    {
        var visibleForeign = VisibleForeignSubtasks();
        if (visibleForeign.Count == 0)
            return _all;
        var universe = new List<TaskItem>(_all.Count + visibleForeign.Count);
        universe.AddRange(_all);
        universe.AddRange(visibleForeign.Values);
        return universe;
    }

    /// <summary>
    /// The pulled-in ("foreign") subtasks that render under the active F4 state (#179): the full resolved
    /// set in <see cref="SubtaskView.All"/>, only the unassigned ones in
    /// <see cref="SubtaskView.MineAndUnassigned"/>, and none when Hidden. Both on-states fetch the full
    /// set, so switching between them is a pure re-render over this filtered view.
    /// </summary>
    private IReadOnlyDictionary<string, TaskItem> VisibleForeignSubtasks()
    {
        var state = _config.View.Subtasks;
        if (_foreignSubtasks.Count == 0 || state == SubtaskView.Hidden)
            return EmptyParents;
        if (state == SubtaskView.All)
            return _foreignSubtasks;
        return _foreignSubtasks.Values
            .Where(SubtaskVisibility.IsUnassigned)
            .ToDictionary(t => t.Id, StringComparer.Ordinal);
    }

    /// <summary>A visible pulled-in subtask that has no assignee (#179) — rendered with the
    /// <c>(unassigned)</c> marker.</summary>
    private static bool IsForeignUnassigned(TaskItem task, IReadOnlyDictionary<string, TaskItem> visibleForeign)
        => visibleForeign.ContainsKey(task.Id) && SubtaskVisibility.IsUnassigned(task);

    /// <summary>A visible pulled-in subtask assigned only to others (#70) — rendered with the
    /// <c>(not assigned to you)</c> marker (F4 "all" state only).</summary>
    private static bool IsForeignOthers(TaskItem task, IReadOnlyDictionary<string, TaskItem> visibleForeign)
        => visibleForeign.ContainsKey(task.Id) && !SubtaskVisibility.IsUnassigned(task);

    // ── Actions ────────────────────────────────────────────────────────────

    private void TogglePin()
    {
        var task = CurrentTask();
        if (task is null)
            return;
        // A subtask pulled in under my parent that isn't assigned to me (#70) isn't part of my snapshot,
        // so pinning it would be a no-op (Focus renders from _all). Refuse it with a clear message.
        if (_foreignSubtasks.ContainsKey(task.Id))
        {
            Flash("This subtask isn't assigned to you — nothing to pin.");
            return;
        }
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
        if (task is null || ActiveScreen is not null)
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
                    if (ActiveScreen is not null)
                        return;
                    // Root the Dispatch pane's working-dir browser (#95) at the saved base dir (#92),
                    // falling back to home if it doesn't exist yet (a task-derived launch creates it on
                    // first use, #98, but the browser has to start somewhere that exists).
                    var detailHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var detailBaseDir = SettingsForm.ResolveDefaultWorkingDirectory(_config.DefaultWorkingDirectory, detailHome);
                    var browserRoot = Directory.Exists(detailBaseDir) ? detailBaseDir : detailHome;
                    var screen = new TaskDetailScreen(
                        detail, comments, browserRoot,
                        settings: _config.DetailView,
                        defaultSessionMode: _config.AgentDispatch.DefaultSessionMode,
                        defaultPostToComments: _config.AgentDispatch.DefaultPostResultsToComments,
                        // Pre-fill the Dispatch working-dir field from the per-task cache (#96) — the
                        // last explicit dir dispatched from this task, or blank if none. Read live on
                        // each pane open so a dispatch within this same open screen is reflected on reopen.
                        workingDirectoryPreFill: () => DispatchWorkingDirectoryCache.PreFill(_config.TaskWorkingDirectories, detail.Id));
                    // Ctrl+A (in the detail view) → compose + launch a claude session (#26/#93). The
                    // detail view stays open; dispatch runs off the UI thread so the TUI stays live. The
                    // prompt, the one-off/interactive mode (#94), the working dir (#95), and the
                    // post-to-Comments flag (#97) are consumed. The detail view opens on the configured
                    // tab/sort/scroll (#108).
                    screen.AgentDispatchRequested += (_, request) => DispatchAgent(detail, comments, request);
                    // F5 / Ctrl+R and the screen's own 30s tick ask for fresh data; re-fetch off the UI
                    // thread and feed it back into the still-open screen (its tab/scroll stay put).
                    screen.RefreshRequested += (_, _) => RefreshDetail(screen, task.Id);
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
    /// Re-fetches a task's detail + comments for an open <see cref="TaskDetailScreen"/> (its F5 / Ctrl+R
    /// or 30s auto-refresh, #114 follow-up) and feeds them back on the UI thread. Mirrors
    /// <see cref="OpenDetail"/>'s off-thread fetch; the result is dropped if that screen has since been
    /// torn down (it's no longer on the stack), and a fetch error flashes without disturbing the view.
    /// The background dashboard refresh is independent and keeps running.
    /// </summary>
    private void RefreshDetail(TaskDetailScreen screen, string taskId)
    {
        // Skip while the detail isn't front-most (e.g. Help stacked over it): no point spending a
        // round-trip to update a hidden view. Runs on the UI thread (from the screen's key handler or
        // its 30s timer tick), so ActiveScreen is a valid read. The next tick refreshes once it's back.
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        // Coalesce overlapping refreshes: skip a tick while one is still in flight so ticks can't pile
        // up and an earlier fetch can't land after a later one with stale data (UI-thread-only flag).
        if (_refreshingDetail)
            return;
        _refreshingDetail = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailAsync(taskId);
                var comments = await _tasks.GetTaskCommentsAsync(taskId);
                Application.Invoke(() =>
                {
                    // Only apply if this screen is still mounted (it may sit beneath a stacked Help).
                    if (_screens.Contains(screen))
                        screen.UpdateData(detail, comments);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh task: {Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() => _refreshingDetail = false);
            }
        });
    }

    /// a new terminal (#26) — an interactive session or, when the request's
    /// <see cref="DispatchRequest.SessionMode"/> is <see cref="AgentSessionMode.OneOff"/>, a one-off
    /// <c>claude -p</c> run (#94). Runs off the UI thread (file write + process launch), then reports
    /// the outcome on the status line; the detail view and background refresh keep running. The working
    /// directory and prompt preamble are resolved from the AgentDispatch settings on the UI thread and
    /// threaded into the dispatch (#91). A working directory explicitly picked in the Dispatch pane
    /// (#95, <see cref="DispatchRequest.WorkingDirectory"/>) overrides the configured mode and starts
    /// the session there. Otherwise, in the default <see cref="AgentWorkingDirectory.TaskDerived"/>
    /// mode the launch starts in the saved base working directory (#92, created on first use) and the
    /// prompt instructs the agent to write outputs to a per-task <c>./{custom-id}</c> subdir (#98);
    /// Home/Fixed modes resolve to their own dir with no subdir instruction.
    /// </summary>
    private void DispatchAgent(TaskDetail detail, IReadOnlyList<CommentItem> comments, DispatchRequest request)
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
        var oneOff = request.SessionMode == AgentSessionMode.OneOff;
        var postToComments = request.PostToComments;

        // Resolve the dispatch settings on the UI thread before the background hand-off (#91).
        // Capture _agent locally so a concurrent F2 settings-save (which rebuilds _agent) can't swap
        // the instance mid-dispatch.
        var agent = _agent;
        var prompt = request.Prompt;
        var settings = _config.AgentDispatch;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // An explicit pane pick (#95) is the override that wins over the configured mode via the
        // existing ResolveEffectiveWorkingDirectory "cached" slot (which #96 also seeds); a blank field
        // ⇒ null ⇒ the configured default. A hand-typed leading ~ is expanded (same as the F2 base-dir
        // field) so it reaches the launcher as an absolute path. The task-derived candidate is the
        // saved base working directory (#92); ResolveWorkingDirectory only uses it in TaskDerived mode,
        // so Home/Fixed are unaffected. In TaskDerived mode *without* an explicit pick we also seed a
        // per-task ./{custom-id} output-subdir instruction so each task's work stays separated inside
        // the shared base dir (#98) — an explicit pick means the user chose their exact dir, so we
        // don't force a subdir there (AgentDispatchSettings.UsesTaskDerivedOutput). The prompt template
        // (#100) is threaded in as the composer's template (blank ⇒ default); its {outputDirInstruction}
        // placeholder consumes the subdir.
        var expandedPick = SettingsForm.ExpandHomePath(request.WorkingDirectory, home);
        var chosenDir = expandedPick.Length == 0 ? null : expandedPick;
        var baseDir = SettingsForm.ResolveDefaultWorkingDirectory(_config.DefaultWorkingDirectory, home);
        var workingDir = settings.ResolveEffectiveWorkingDirectory(chosenDir, taskDerivedDirectory: baseDir, homeDirectory: home);
        var useTaskDerived = settings.UsesTaskDerivedOutput(chosenDir);
        var outputSubdir = useTaskDerived ? AgentPromptComposer.OutputSubdirectoryToken(detail) : null;
        var template = settings.PromptTemplate;

        // Remember an explicit non-default pick for this task (#96) so the next dispatch pre-fills it,
        // across relaunches; reverting to the default (blank field / pick == the configured mode dir)
        // clears the entry. Done on the UI thread before the hand-off, and only persisted when the
        // cache actually changed. resolvedDefault is what the mode would pick with no explicit dir,
        // ~-expanded (as chosenDir is) so a Fixed dir stored as "~/foo" still matches an explicit pick
        // of the same resolved path and clears the entry rather than persisting a redundant one.
        var resolvedDefaultRaw = settings.ResolveWorkingDirectory(taskDerivedDirectory: baseDir, homeDirectory: home);
        var resolvedDefault = resolvedDefaultRaw is null ? null : SettingsForm.ExpandHomePath(resolvedDefaultRaw, home);
        if (DispatchWorkingDirectoryCache.Update(_config.TaskWorkingDirectories, detail.Id, chosenDir, resolvedDefault))
            _configStore.Save(_config);

        // One-off mode (#94) runs claude -p as a background child of the app — no terminal window — with
        // a "thinking" spinner and the captured output rendered in a screen (#99). Interactive mode keeps
        // opening a real terminal below (an interactive session needs a live TTY).
        if (oneOff)
        {
            RunBackgroundDispatch(detail, comments, agent, prompt, workingDir, template, outputSubdir, useTaskDerived, postToComments);
            return;
        }

        Flash($"Launching Claude for '{detail.Name}'…");
        _ = Task.Run(async () =>
        {
            try
            {
                // A task-derived launch starts in the base dir; create it on first use (#98) so
                // Process.Start doesn't fail on a not-yet-existing path. Home/Fixed dirs and an explicit
                // pane pick are the user's own (Home always exists; a Fixed dir / explicit pick is their
                // choice — a browser pick always exists, and a hand-typed missing path surfaces a launch
                // error rather than being silently created).
                if (useTaskDerived && !string.IsNullOrWhiteSpace(workingDir))
                    Directory.CreateDirectory(workingDir);

                var result = await agent.DispatchAsync(detail, comments, prompt, workingDir, template, outputSubdir, oneOff, postToComments);
                Application.Invoke(() => { _dispatching = false; Flash(result.StatusMessage); });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => { _dispatching = false; Flash($"Could not launch Claude: {Short(ex)}"); });
            }
        });
    }

    /// <summary>
    /// Runs a one-off <c>claude -p</c> dispatch (#99) as a background child process: mounts an
    /// <see cref="AgentRunScreen"/> over the detail view (through the shared screen seam), runs the
    /// dispatch off the UI thread with a cancellation token wired to the screen's Esc, and marshals the
    /// captured output — or a cancellation / failure — back to the screen via <see cref="Application.Invoke"/>.
    /// The working-dir/subdir/template/post-to-Comments inputs were already resolved by the caller so a
    /// one-off run's prompt matches what the interactive path would compose. Must run on the UI thread.
    /// </summary>
    private void RunBackgroundDispatch(
        TaskDetail detail, IReadOnlyList<CommentItem> comments, AgentDispatcher agent, string prompt,
        string? workingDir, string? template, string? outputSubdir, bool useTaskDerived, bool postToComments)
    {
        var cts = new CancellationTokenSource();
        var screen = new AgentRunScreen(detail.Name);
        screen.CancelRequested += (_, _) => cts.Cancel();
        // Closing the screen (Esc after it finished) cancels any straggler and releases the token source.
        ShowScreen(screen, () =>
        {
            cts.Cancel();
            cts.Dispose();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                // A task-derived launch starts in the base dir; create it on first use (#98), same as the
                // interactive path, so the child process doesn't fail on a not-yet-existing path.
                if (useTaskDerived && !string.IsNullOrWhiteSpace(workingDir))
                    Directory.CreateDirectory(workingDir);

                var run = await agent.DispatchBackgroundAsync(detail, comments, prompt, workingDir, template, outputSubdir, postToComments, cts.Token);
                Application.Invoke(() => { _dispatching = false; screen.ShowResult(AgentRunModel.FormatOutput(run), run.Success); });
            }
            catch (OperationCanceledException)
            {
                Application.Invoke(() => { _dispatching = false; screen.ShowCancelled("Run cancelled — the Claude process was stopped."); });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => { _dispatching = false; screen.ShowResult($"Could not run Claude: {Short(ex)}", success: false); });
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
        // A subtask pulled in under my parent that isn't assigned to me is context, not my work (#70).
        if (_foreignSubtasks.ContainsKey(task.Id))
        {
            Flash("This subtask isn't assigned to you — status unchanged.");
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

        if (ActiveScreen is not null)
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
        // Reproduce the row's ▶/▼ fold marker from its stored state so an in-place update keeps it (#76).
        var marker = FoldMarker(index < _folds.Count ? _folds[index] : FoldState.None, _config.View.ShowSubtasks);
        var (text, badges) = BuildRow(updated, _config.BadgeDisplay, _tasks.UserId, index < _depths.Count ? _depths[index] : 0, groupedBy: groupedBy, marker: marker);
        _badges[index] = badges;
        // Mutating _display fires CollectionChanged (via the wrapper the source composes), which
        // redraws just this row; the parallel _badges entry is read during that redraw.
        _display[index] = sending ? $"{text}  (sending…)" : text;
    }

    private void ShowHelp()
    {
        if (ActiveScreen is not null)
            return;
        ShowScreen(new HelpScreen(), static () => { });
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void OnTasksLoaded(IReadOnlyList<TaskItem> tasks)
    {
        _all = tasks;
        _status = $"Updated {DateTime.Now:HH:mm:ss} · {tasks.Count} task(s) · refresh every {_config.RefreshSeconds}s";
        // Surface an adaptive-fetch cap (#87) on the persisted status line — a Flash here would be
        // repainted away by this same success path, so it's folded into the line the path writes.
        if (_foreignSubtasksTruncated)
            _status += " · some subtasks omitted";

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
        // Fold in the F4 state (not just on/off) so switching between the two on-states — a pure re-render
        // over the same fetched set — is treated as a change rather than a no-op (#179).
        sb.Append("#sub=").Append(_config.View.Subtasks);
        if (_config.View.ShowSubtasks)
        {
            foreach (var id in _contextParents.Keys.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(';').Append(id);
            // Fold in the pulled-in foreign subtasks so adding/removing one — or an edit to one (status,
            // rename, reschedule; UpdatedMs advances on any ClickUp edit) — is treated as a render change
            // rather than a no-op refresh (#70). These rows aren't in `tasks`/_all, so BuildSignature
            // above doesn't cover them.
            sb.Append("#fsub=");
            foreach (var kv in _foreignSubtasks.OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(kv.Value.StatusName)
                  .Append(':').Append(kv.Value.UpdatedMs).Append(';');
        }
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
        if (view.Subtasks.TitleFlag() is { } subtaskFlag)
            flags.Add(subtaskFlag);
        return flags.Count > 0 ? $"{title} · {string.Join(" · ", flags)}" : title;
    }

    /// <summary>Rebuilds the single list (focus section + to-do section) and restores the cursor.</summary>
    private void Render(string? keepTaskId)
    {
        // Pinned tasks are shown as today (unaffected by filters/grouping — explicit pins shouldn't
        // vanish); the filter/sort/group view (F3) applies to the non-pinned set. Sort applies to both.
        var view = _config.View;
        var nest = view.ShowSubtasks;
        // The pulled-in subtasks visible under the active F4 state (#179): the full set in "all", only the
        // unassigned ones in "mine + unassigned". Used everywhere the render places or suppresses pulled-in
        // rows, so the "mine + unassigned" state excludes others-only children consistently.
        var visibleForeign = VisibleForeignSubtasks();

        // The pinned "Current Focus" section. When the subtasks view (F4) is on, a pinned parent's
        // in-snapshot subtasks nest indented beneath it (reusing SubtaskArranger) instead of falling
        // through to the to-do set un-indented; those pulled-in subtask ids are excluded from the
        // non-pinned set below so they don't render twice. Pins ignore F3 filters/grouping. (#75)
        var pinnedIds = _all.Where(t => _focus.IsPinned(t.Id)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        // Feed the pulled-in teammate-owned subtasks (#70) into the Focus layout too, so a foreign child of
        // a pinned parent nests under it in Focus rather than vanishing (#85). NestedSubtaskIds then covers
        // both in-snapshot and foreign rows pulled into Focus — the exact set to keep out of the to-do list.
        var foreignList = nest && visibleForeign.Count > 0 ? visibleForeign.Values.ToList() : null;
        var focus = FocusSectionLayout.Build(_all, pinnedIds, nest, view.SortField, view.SortDirection, _expanded, foreignList);
        _focusNestedIds = focus.NestedSubtaskIds;

        // The non-pinned set feeds the F3 view. Drop pinned tasks and (when nesting) any subtask pulled
        // into the Focus section above, so it renders only once. When subtasks are hidden (the default),
        // also drop them here so the main list stays a flat top-level view. (#46, #75)
        var nonPinned = _all.Where(t => !pinnedIds.Contains(t.Id) && !focus.NestedSubtaskIds.Contains(t.Id));
        if (!nest)
            nonPinned = nonPinned.Where(t => string.IsNullOrEmpty(t.ParentId));
        // When nesting, fold in the teammate-owned subtasks of my parents (#70) before Apply, so they're
        // filtered (Status IS NOT etc.), sorted, and grouped consistently and the arranger can nest them
        // under their present parent. Populated only when the F4 view + the setting are both on. Foreign
        // children whose ancestor is pinned were already pulled into the Focus section above (#85), so
        // exclude exactly those (NestedSubtaskIds) here — the rest nest under their non-pinned parent.
        else if (visibleForeign.Count > 0)
            nonPinned = nonPinned.Concat(
                visibleForeign.Values.Where(t => !focus.NestedSubtaskIds.Contains(t.Id)));
        var groups = TaskView.Apply(nonPinned, view);
        var todoCount = groups.Sum(g => g.Tasks.Count);
        var grouped = view.GroupField is not null;
        // Grouping and nesting compose: within each F3 group, subtasks nest under their parent when both
        // fall in the same group. An in-snapshot (assigned) subtask whose parent lands in a different
        // group renders flat within its own group; a pulled-in teammate-owned subtask (#70) in that same
        // position is instead suppressed, since a "(not assigned to you)" row only belongs nested under a
        // visible parent, never un-indented (#172). (#46, #57)

        _rows.Clear();
        _kinds.Clear();
        _display = new ObservableCollection<string>();
        _badges = new List<IReadOnlyList<StatusBadgeListSource.Badge>>();
        _headerAttrs = new List<Attribute?>();
        _depths = new List<int>();
        _folds = new List<FoldState>();

        // A background color per group header, by the grouped field (status/list/priority/date). Null
        // entries (and the non-field pinned/tasks headers) fall back to the neutral bar. (#61)
        var headerColors = GroupHeaderPalette.Resolve(view.GroupField, groups, _listColors);

        // The Focus header count is the number of pinned tasks (the anchors); pulled-in subtasks are
        // nested child rows, not pins. Focus rows keep every segment (no group header sits above them,
        // #67), so AddTask is called with groupedBy null.
        if (pinnedIds.Count > 0)
            AddHeader($"{FocusHeaderPrefix} ({pinnedIds.Count})");
        foreach (var row in focus.Rows)
            // A pulled-in subtask (#70/#85) nested under a pinned parent gets the not-mine marker, or the
            // (unassigned) marker when it has no assignee (#179), exactly as it would in the to-do section.
            AddTask(row.Task, row.Depth, row.IsContextParent, groupedBy: null, fold: row.Fold,
                isForeignSubtask: IsForeignOthers(row.Task, visibleForeign),
                isUnassignedSubtask: IsForeignUnassigned(row.Task, visibleForeign));

        // The single tasks-section header only appears (when ungrouped) to separate the to-do rows
        // from a pinned section above them.
        var ungroupedTasksHeader = pinnedIds.Count > 0 ? $"{TasksHeaderPrefix} ({todoCount}) ─" : null;
        // A teammate-owned subtask (#70) must never surface un-indented at top level: when its parent is
        // filtered out of the to-do set (e.g. a completed parent dropped by a Status IS NOT rule), it has
        // no visible parent to nest under, so the arranger suppresses it rather than leaking it flat as
        // "(not assigned to you)" (#172). Only relevant while nesting and while foreign subtasks exist.
        var suppressTopLevel = nest && visibleForeign.Count > 0
            ? new HashSet<string>(visibleForeign.Keys, StringComparer.Ordinal)
            : null;
        foreach (var row in SectionLayout.BuildTodoSection(groups, _contextParents, grouped, nest, ungroupedTasksHeader, headerColors, _expanded, suppressTopLevel))
        {
            if (row.IsHeader)
                AddHeader(row.HeaderText!, row.HeaderColor);
            else
                // Omit the grouped field from each to-do row — the group header above already shows it
                // (#67). The pinned Focus section has no group headers, so its rows keep every segment.
                // Carry the ▶/▼ fold state (#76); a pulled-in subtask (#70) also gets a not-mine or, when
                // it has no assignee, an (unassigned) marker (#179).
                AddTask(row.Task!, row.Depth, row.IsContextParent, view.GroupField,
                    fold: row.Fold, isForeignSubtask: IsForeignOthers(row.Task!, visibleForeign),
                    isUnassignedSubtask: IsForeignUnassigned(row.Task!, visibleForeign));
        }

        // A custom source that draws text like the stock wrapper, overlays each [status] badge with its
        // ClickUp color, and paints each group header as a full-width color bar. Assigning Source
        // (rather than SetSource) lets us pass our source; the ListView disposes the previous one.
        // Type-ahead (#12) searches title-only keys so the ▶/▼ marker + badges on the rendered line don't
        // break "type the first letters of a title to jump" (#76); header/spacer rows keep their text.
        var searchKeys = new List<string>(_rows.Count);
        for (var i = 0; i < _rows.Count; i++)
            searchKeys.Add(_rows[i]?.Name ?? _display[i]);
        _list.Source = new StatusBadgeListSource(_display, _badges, _headerAttrs, searchKeys);
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
        _folds.Add(FoldState.None);
    }

    private void AddSpacer()
    {
        _rows.Add(null);
        _kinds.Add(RowKind.Spacer);
        _display.Add("");
        _badges.Add([]);
        _headerAttrs.Add(null);
        _depths.Add(0);
        _folds.Add(FoldState.None);
    }

    private void AddTask(TaskItem task, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null, FoldState fold = FoldState.None, bool isForeignSubtask = false, bool isUnassignedSubtask = false)
    {
        var (text, badges) = BuildRow(task, _config.BadgeDisplay, _tasks.UserId, depth, isContextParent, groupedBy, FoldMarker(fold, _config.View.ShowSubtasks), isForeignSubtask, isUnassignedSubtask);
        _rows.Add(task);
        _kinds.Add(RowKind.Task);
        _display.Add(text);
        _badges.Add(badges);
        _headerAttrs.Add(null);
        _depths.Add(depth);
        _folds.Add(fold);
    }

    /// <summary>
    /// The leading fold marker (#76) for a row: a ▶/▼ glyph on a foldable parent, a two-column gutter on
    /// other rows so titles line up, or nothing when the subtasks view is off (unchanged layout).
    /// </summary>
    private static string FoldMarker(FoldState fold, bool nest)
        => !nest ? "" : fold switch
        {
            FoldState.Expanded => "▼ ",
            FoldState.Collapsed => "▶ ",
            _ => "  ",
        };

    /// <summary>Fixed white background for the trailing assignees badge (#161) — not tinted by a
    /// ClickUp field colour like Status/Priority; the readable dark foreground follows from
    /// <see cref="StatusBadgeColor.PreferDarkText"/> (black on white).</summary>
    private const string AssigneesBadgeColor = "ffffff";

    /// <summary>Fixed muted-gray background for the leading custom-id (or fallback task-id) chip — a
    /// neutral identifier tint, deliberately not a ClickUp field colour, so the id reads as metadata
    /// beside the Status/Priority badges rather than as another status. The light foreground follows
    /// from <see cref="StatusBadgeColor.PreferDarkText"/> (white on dark gray).</summary>
    private const string CustomIdBadgeColor = "5a5a5a";

    /// <summary>The display text and the row's color badge overlays (status, then priority when set,
    /// the leading custom-id/task-id chip, then the trailing assignees badge, #161).
    /// <paramref name="groupedBy"/> omits the grouped field's
    /// segment (its header already conveys it, #67). <paramref name="marker"/> is the leading ▶/▼ fold
    /// marker or gutter (#76). <paramref name="badges"/> selects how the badges render (F6).
    /// <paramref name="currentUserId"/> decides the trailing assignees badge (shown when a non-current
    /// user is assigned).</summary>
    private static (string Text, IReadOnlyList<StatusBadgeListSource.Badge> Badges) BuildRow(
        TaskItem task, BadgeDisplay badgeDisplay, long currentUserId, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null, string marker = "", bool isForeignSubtask = false, bool isUnassignedSubtask = false)
    {
        var row = TaskRowFormatter.Format(task, depth, isContextParent, groupedBy, marker, isForeignSubtask, badgeDisplay, currentUserId, isUnassignedSubtask);
        var badges = new List<StatusBadgeListSource.Badge>(4);
        // The Status/Priority badges (icon chip or bracketed text) are tinted with their field colours;
        // an absent/hidden badge carries no span, so TryCreate returns null and nothing is shaded.
        if (StatusBadgeListSource.TryCreate(row.StatusStart, row.StatusLength, task.StatusColor) is { } status)
            badges.Add(status);
        if (StatusBadgeListSource.TryCreate(row.PriorityStart, row.PriorityLength, task.PriorityColor) is { } priority)
            badges.Add(priority);
        // The leading custom-id (or fallback task-id) chip is muted-gray, not field-tinted; a hidden-mode
        // row carries no span, so TryCreate returns null and nothing is shaded.
        if (StatusBadgeListSource.TryCreate(row.CustomIdStart, row.CustomIdLength, CustomIdBadgeColor) is { } customId)
            badges.Add(customId);
        // The trailing assignees badge (#161) is white-backed, not field-tinted; the same absent/hidden
        // span sentinel makes TryCreate return null so nothing is shaded when it's not shown.
        if (StatusBadgeListSource.TryCreate(row.AssigneesStart, row.AssigneesLength, AssigneesBadgeColor) is { } assignees)
            badges.Add(assignees);
        return (row.Text, badges);
    }

    private void Flash(string message)
    {
        _status = message;
        _statusLabel.Text = message;
    }

    private static string Short(Exception ex) => ex is ClickUpApiException c ? c.Message : ex.Message;
}
