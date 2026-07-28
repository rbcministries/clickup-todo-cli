using System.Collections.ObjectModel;
using System.Diagnostics;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;
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
    // Persists the last loaded working set for an instant first paint on the next launch (#122). Read
    // once at startup (TryPaintCachedTasks) and written after each changed live load (OnTasksLoaded).
    private readonly TaskCache _taskCache;
    // Persists the last aggregated feed for an instant paint when the feed screen opens (#123). Read
    // in OpenNotificationsFeed and written after each successful aggregation (open + RefreshFeed).
    private readonly FeedCache _feedCache;
    // Candidate-people pool for the future Quick Updates Assignees pane (#155/#158): tallied from each
    // loaded working set and topped up (once, off-thread) from the workspace members. Never touches
    // rendering or input — no #3/#12 impact.
    private readonly AssigneeFrequencyCache _assignees;
    // Candidate-lists pool for the future List selector (#238/#239): tallied from each loaded working
    // set's home lists and backfilled (count-0) from the scheduled list-hierarchy walk (#236). Never
    // touches rendering or input — no #3/#12 impact.
    private readonly ListFrequencyCache _lists;
    // Cross-platform open-in-browser (#308): Windows shell association, macOS `open`, Linux `xdg-open`
    // & friends, resolved by BrowserLaunchPlanner. The TUI isn't unit-tested; the launch logic lives
    // in the planner and is covered there. Injected (#304) so the E2E harness can swap in a recording
    // launcher; defaults to the real OS launcher.
    private readonly IBrowserLauncher _browser;
    // How many candidates the Assignees pane wants available before it stops needing the deferred
    // workspace-members top-up (it fills its empty state up to 10 rows).
    private const int AssigneeCandidateTarget = 10;
    // Set once the one-shot assignee top-up has been kicked, so it fires after the first load, not on
    // every refresh. UI-thread-only.
    private bool _assigneeTopUpKicked;
    // Composes the seed prompt + launches an interactive `claude` session for the detail view's A
    // keybinding (#26). Built from the persisted AgentDispatch settings (#91) and rebuilt after the F2
    // settings dialog saves, so a custom terminal / claude path / extra args apply without a restart.
    private AgentDispatcher _agent = null!;
    // True while a dispatch is in flight, so a rapid second submit doesn't launch a duplicate session.
    // Only touched on the UI thread (set in DispatchAgent, cleared via Application.Invoke).
    private bool _dispatching;
    // Opens a task in its own terminal tab (#301) via the shared cross-platform launcher (#25/#307).
    // Interactive-only, so a plain field (not rebuilt on F2 like _agent); the tab launch reads the
    // preferred-terminal setting at call time.
    private readonly ITerminalLauncher _tabLauncher = new TerminalLauncher();
    // True while a new-tab launch is in flight, so a rapid second Ctrl+Enter can't spawn duplicate tabs.
    // UI-thread-only (set in LaunchTaskInNewTab, cleared via Application.Invoke), like _dispatching.
    private bool _launchingTab;
    // True while a feed / detail auto- or manual refresh fetch is outstanding, so ticks coalesce
    // instead of piling up when a fan-out outlasts the cadence. UI-thread-only (like _dispatching).
    private bool _refreshingFeed;
    // Set when a refresh is requested while one is already in flight, so the queued request runs once the
    // current fetch completes instead of being dropped — a state-changing F12 toggle must not be lost to
    // coalescing. Bounded to a single pending run (mirrors the list's queue-style RefreshService).
    // UI-thread-only.
    private bool _feedRefreshPending;
    private bool _refreshingDetail;
    // The cross-process nudge channel (#292). The producer (#294) records a marker after every confirmed
    // write; this is the consumer side (#295) — a background scan that turns another instance's markers
    // into a per-task re-fetch. `_changeMarkers` is the shared store (Null when the file-backed state
    // store has no cross-process channel), `_markerConsumer` the pure cursor scan over it. UI-thread-only.
    private readonly IChangeMarkerStore _changeMarkers;
    private readonly ChangeMarkerConsumer _markerConsumer;
    // True from a marker-poll's off-thread ReadAll through its UI-thread Advance+dispatch, so two scans
    // can't overlap and the short cadence can't pile ReadAlls up. (The per-task fetches a scan dispatches
    // are fire-and-forget and guarded separately — the detail path by _refreshingDetail, the row path by
    // an UpdatedMs ordering check in RefreshNudgedRow.) UI-thread-only.
    private bool _pollingMarkers;
    // Marker-check cadence (#295), deliberately decoupled from the API poll: a `changes` read is a cheap,
    // bounded DB-only op, so cross-tab updates propagate on a short fixed cadence while API fetches stay
    // targeted, independent of the 60s-default RefreshSeconds.
    private static readonly TimeSpan MarkerPollInterval = TimeSpan.FromSeconds(4);

    private Window _window = null!;
    private FrameView _frame = null!;
    private ListView _list = null!;
    // The shared bottom rows (#103/#346): a transient status line plus the single window-owned
    // contextual help line (the active screen's shortcuts, or the list's when idle). Screens no longer
    // hand-roll their own. Built in Build.
    private ContextualFooter _footer = null!;
    // The items currently rendered on the help line (post-Fit), cached so a footer click (#289) can
    // hit-test the click column against exactly what's on screen at the present width.
    private IReadOnlyList<HelpItem> _helpFooter = HelpItemSets.MainList;
    // The main list's command shortcuts, dispatched through the central (context, action) → key table
    // (#355) so the key for each command and its footer label share one source of truth (Keybindings /
    // HelpItemSets). Movement/arrow/Tab keys and undisplayed aliases (Ctrl+R, Ctrl+C, Esc quit) stay in
    // OnListKey — they are intentionally not table-governed footer commands.
    private KeybindingDispatcher _listKeys = null!;
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
    // Per-row char span of the leading ▶/▼ fold marker within the rendered text, parallel to _display, so
    // a mouse click can hit-test the arrow column (#287). (-1, 0) on rows without a marker (headers,
    // spacers, leaves, or when nesting is off). Length is always the 2-char "▶ "/"▼ " on a foldable row.
    private List<(int Start, int Length)> _markerSpans = [];
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
    // The status line's seed text, composed before Build (the driver annotation is appended in Run);
    // once Build creates the footer, the live status lives on _footer (#346).
    private string _status = "Loading…";
    private string _signature = "";

    // A task id to land the cursor on at the next render (#213): set when a New Task is created so the
    // refreshed list selects it, honoured once by OnTasksLoaded then cleared. Touched on the UI thread.
    private string? _pendingSelectId;

    public TodoApp(TaskService tasks, FeedService feed, AppConfig config, ConfigStore configStore,
        IFocusStore focus, TaskCache taskCache, FeedCache feedCache, AssigneeFrequencyCache assignees,
        ListFrequencyCache lists, IBrowserLauncher? browserLauncher = null,
        IChangeMarkerStore? changeMarkers = null)
    {
        _tasks = tasks;
        _feed = feed;
        _config = config;
        _configStore = configStore;
        _focus = focus;
        _taskCache = taskCache;
        _feedCache = feedCache;
        _assignees = assignees;
        _lists = lists;
        _browser = browserLauncher ?? new SystemBrowserLauncher();
        // The nudge channel's read side (#295). Defaults to the no-op store so every existing caller/test
        // is unchanged; the Null store's empty InstanceId disarms the marker poll (see Run).
        _changeMarkers = changeMarkers ?? NullChangeMarkerStore.Instance;
        _markerConsumer = new ChangeMarkerConsumer(_changeMarkers.InstanceId);
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
            // Instant first paint from the persisted working set (#122): render the last snapshot now,
            // synchronously on the UI thread, before Application.Run starts pumping — so the first live
            // refresh (marshalled in via Application.Invoke) can only ever arrive after this. When the
            // live set matches, OnTasksLoaded's signature fast-path skips the re-render (no flicker);
            // when it differs, the cursor is kept by task id.
            TryPaintCachedTasks();
            _refresh = new RefreshService(
                fetch: FetchAsync,
                intervalSeconds: _config.RefreshSeconds,
                onUpdate: tasks => Application.Invoke(() => OnTasksLoaded(tasks)),
                onError: ex => Application.Invoke(() => Flash($"Refresh failed: {ErrorText.Short(ex)}")));
            _refresh.Start();
            ArmMarkerPoll();
            Application.Run(_window);
        }
        finally
        {
            _refresh?.Dispose();
            // Application.Shutdown restores the terminal (cooked mode, alt-screen off), so it must run no
            // matter how _window.Dispose fares — hence the nested try/finally. Any screen open at quit is
            // still mounted here, so the shared teardown guard swallows Terminal.Gui 2.4.10's known
            // tabbed-view dispose bug (#346) rather than crash after the run loop exits.
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

    /// <summary>Minimum age between workspace list-walk passes (#236): the hierarchy changes on human
    /// timescales, so ~30 min bounds staleness without spending the walk's round-trips every poll.
    /// A minimum age, not an exact period — the walk runs on the first refresh cycle at or after it
    /// (see <see cref="FetchCadenceGate"/>).</summary>
    internal static readonly TimeSpan WorkspaceListWalkMinAge = TimeSpan.FromMinutes(30);

    private const string WorkspaceListWalkGroup = "workspace-lists";

    /// <summary>Minimum age between warm closed-task prefetches (#253): the set only bridges the F12→All
    /// transition, so a few minutes of staleness is invisible (the on-demand refresh corrects it) while
    /// keeping the extra include_closed=true fetch off the steady-state poll. A minimum age, not a period
    /// — never-run ⇒ due, so it warms on the first background poll after startup and then rides the loop
    /// (see <see cref="FetchCadenceGate"/>).</summary>
    internal static readonly TimeSpan ClosedPrefetchMinAge = TimeSpan.FromMinutes(3);

    private const string ClosedPrefetchGroup = "closed-prefetch";

    // How many closed tasks the last prefetch's bounds dropped (#253); surfaced on the F12→All bridge so
    // an over-cap warm set isn't a silent truncation. Written off-thread by the prefetch step, read on
    // the UI thread — the int write/read is atomic and it's advisory, so volatile suffices.
    private volatile int _closedPrefetchDropped;

    // Per-group cadence gate (#246 ADR): lets slow-cadence work ride the existing refresh loop
    // instead of adding timers or a scheduler. Groups: the workspace list walk and the closed prefetch.
    private readonly FetchCadenceGate _cadence = new();

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

        // Snapshot-INDEPENDENT work first (#246 ADR): the workspace list walk (#236) is gated on its
        // own minimum age, never on the snapshot — and is decided before the empty-delta early
        // return below (which only skips snapshot-DERIVED resolvers), so a quiet workspace can't
        // starve it. Initial/Manual force it due, matching "manual is always full" (#194). Mid-pass
        // the walk stays due every cycle (MarkRan fires only on pass completion), which is what
        // spreads a large workspace's walk across cycles instead of bursting it.
        var walkFetch = kind != RefreshKind.Poll || _cadence.IsDue(WorkspaceListWalkGroup, WorkspaceListWalkMinAge)
            ? RunWorkspaceListWalkStepAsync(ct)
            : Task.FromResult(false);

        // Warm the closed-task cache (#253) on its own slow cadence, gated three ways: only on a
        // background Poll (never Initial/Manual — the warm set benefits a *future* F12→All, not the
        // fetch the user is currently waiting on, so it must not add latency to a first or manual
        // paint — unlike the walk, whose list cache feeds the current render); only while below the F12
        // All state (in All the live snapshot already carries closed tasks, so it'd be a redundant
        // include_closed=true fetch); and only when its minimum age has elapsed. Snapshot-INDEPENDENT
        // like the walk, so it's decided here and awaited in both the empty-delta early return and the
        // main path.
        var closedPrefetch = kind == RefreshKind.Poll
                             && !_config.View.IncludesClosedTasks
                             && _cadence.IsDue(ClosedPrefetchGroup, ClosedPrefetchMinAge)
            ? RunClosedPrefetchStepAsync(ct)
            : Task.FromResult(false);

        // A provably-empty delta means the world hasn't changed: keep the previous resolver results
        // (context parents / foreign subtasks / list colors) instead of re-spending their round-trips.
        // Anything that changes what the resolvers should see (F3/F4/grouping edits) triggers a manual
        // refresh, which is never a delta.
        if (snapshot is { WasDelta: true, Changed: false })
        {
            if (await walkFetch)
                _cadence.MarkRan(WorkspaceListWalkGroup);
            if (await closedPrefetch)
                _cadence.MarkRan(ClosedPrefetchGroup);
            return snapshot.Tasks;
        }

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

        // WhenAll (rather than awaiting in turn) so a fault in one resolver still observes the others;
        // the walk step and closed prefetch join the batch (#246 ADR), so a due slow-cadence job costs
        // the cycle max, not sum.
        await Task.WhenAll(parentsFetch, foreignFetch, colorsFetch, walkFetch, closedPrefetch);

        _contextParents = await parentsFetch;
        var foreign = await foreignFetch;
        // Keyed by id for fast Render/guard lookups.
        _foreignSubtasks = foreign.Subtasks.Count == 0
            ? EmptyParents
            : foreign.Subtasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _foreignSubtasksTruncated = foreign.Truncated;
        _listColors = await colorsFetch;
        if (await walkFetch)
            _cadence.MarkRan(WorkspaceListWalkGroup);
        if (await closedPrefetch)
            _cadence.MarkRan(ClosedPrefetchGroup);
        return tasks;
    }

    /// <summary>
    /// One bounded step of the workspace list walk (#236), wrapped best-effort: the walk only feeds
    /// a lookaside cache, so its failure must not surface as "Refresh failed" when the task rows and
    /// resolvers were fine. A failed step reports the pass complete so the gate is stamped — per-group
    /// error backoff (#246 ADR): the next attempt waits the walk's full minimum age instead of
    /// retrying at poll cadence. Returns whether to stamp the cadence gate.
    /// </summary>
    private async Task<bool> RunWorkspaceListWalkStepAsync(CancellationToken ct)
    {
        try
        {
            var resolution = await _tasks.ResolveWorkspaceListsAsync(ct);
            // Backfill the list-frequency pool's long tail (#238): seed every list the walk has
            // discovered as a count-0 candidate, so lists no task row surfaced are still searchable.
            // Additive and idempotent — lists already tallied keep their real count.
            _lists.SeedLists(resolution.Lists);
            return resolution.PassComplete;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine shutdown — let the refresh loop's own handling see it
        }
        catch (Exception ex)
        {
            // Everything else backs off — including an HttpClient timeout, which surfaces as a
            // (Task)CanceledException with our ct UNSIGNALLED (the same trap RefreshService.RunAsync
            // filters for): rethrown, it would fail the whole cycle as "Refresh failed" even though
            // the snapshot and resolvers succeeded.
            Debug.WriteLine($"Workspace list walk failed (backing off to its next window): {ex}");
            return true;
        }
    }

    /// <summary>
    /// One warm closed-task prefetch step (#253), wrapped best-effort exactly like the walk: the cache
    /// only bridges the F12→All paint, so a failed fetch must not surface as "Refresh failed" when the
    /// task rows were fine. Records how many the bounds dropped (for the bridge's truncation note) and
    /// returns whether to stamp the cadence gate — true on both success and (backed-off) failure, so a
    /// failing prefetch waits its full minimum age rather than retrying every poll (#246 ADR).
    /// </summary>
    private async Task<bool> RunClosedPrefetchStepAsync(CancellationToken ct)
    {
        try
        {
            _closedPrefetchDropped = await _tasks.PrefetchClosedTasksAsync(ct);
            if (_closedPrefetchDropped > 0)
                Debug.WriteLine($"Closed-task prefetch bounded its set, dropped {_closedPrefetchDropped} task(s).");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine shutdown — let the refresh loop's own handling see it
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Closed-task prefetch failed (backing off to its next window): {ex}");
            return true;
        }
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
        _listKeys = BuildListKeyDispatcher();
        _list.KeyDown += OnListKey;
        _list.MouseEvent += OnListMouse;
        _frame.Add(_list);

        // The shared status + contextual help footer (#103/#346). The help line is seeded with the
        // list's shortcuts — byte-for-byte the pre-#103 text, so the default footer is unchanged;
        // UpdateHelpLine swaps in the active screen's shortcuts on show and back to the list's on close.
        _footer = new ContextualFooter(_status, initialHelp: HelpLine.Format(HelpItemSets.MainList));
        // Clicking an action hint on the footer fires its shortcut (#289). The Label stays
        // CanFocus=false, so this adds a mouse affordance without a second focusable pane (#3/#38).
        _footer.HelpLabel.MouseEvent += OnHelpBarMouse;

        _window.Add(_frame);
        _footer.AddTo(_window);
        // Re-fit the help line whenever the window re-lays out (i.e. on terminal resize). Terminal.Gui
        // 2.4 has no static Application size-changed event; SubViewsLaidOut is the framework's
        // post-layout hook. UpdateHelpLine only reassigns the text when it changed, so this can't loop.
        _window.SubViewsLaidOut += (_, _) => UpdateHelpLine();
        _list.SetFocus();
    }

    // ── Key handling ─────────────────────────────────────────────────────────

    /// <summary>
    /// The main list's command shortcuts, wired through the central <see cref="Keybindings"/> table
    /// (#355): each action's key is resolved from the table, so this method is the only place the
    /// main-list bindings and their footer labels (<see cref="HelpItemSets.MainList"/>) are tied
    /// together. Movement (arrows/Tab), the subtasks-guarded fold keys, and the undisplayed aliases
    /// (Ctrl+R, Ctrl+C, Esc quit) are not footer commands and stay literal in <see cref="OnListKey"/>.
    /// </summary>
    private KeybindingDispatcher BuildListKeyDispatcher()
        => new KeybindingDispatcher(ScreenContext.MainList)
            .On(KeyAction.QuickUpdate, OpenQuickUpdates)   // #159/#290, standardized to Ctrl+U
            .On(KeyAction.OpenDetail, OpenDetail)
            .On(KeyAction.QuickOpen, OpenQuickOpen)         // #303
            .On(KeyAction.OpenInNewTab, () => LaunchTaskInNewTab(CurrentTask()))   // #301
            .On(KeyAction.NewTask, OpenNewTask)             // #213
            .On(KeyAction.OpenInBrowser, OpenInBrowser)
            .On(KeyAction.TogglePin, TogglePin)
            .On(KeyAction.Feed, OpenNotificationsFeed)      // List ↔ Feed
            .On(KeyAction.Help, ShowHelp)
            .On(KeyAction.Settings, OpenSettings)
            .On(KeyAction.FilterSortGroup, OpenViewSettings)
            .On(KeyAction.CycleSubtasks, CycleSubtaskView)
            .On(KeyAction.Refresh, RequestRefresh)          // F5 (Ctrl+R is the undisplayed alias below)
            .On(KeyAction.CycleBadges, CycleBadgeDisplay)
            .On(KeyAction.ToggleCompleted, CycleShowCompleted)
            .On(KeyAction.Quit, RequestExit);   // #298/#299 exit seam (see RequestExit)

    private void OnListKey(object? sender, Key key)
    {
        // Table-driven command shortcuts first (#355). Bare letters never match (the table only holds
        // chords / function keys), so the ListView's type-ahead search (keyed on the task title) is
        // untouched.
        if (_listKeys.Dispatch(key))
        {
            key.Handled = true;
            return;
        }

        // Undisplayed aliases and movement — intentionally not table-governed footer commands.
        if (key.IsCtrl)
        {
            switch (key.KeyCode & ~KeyCode.CtrlMask)
            {
                case KeyCode.R:
                    // Ctrl+R is the (undisplayed) alias for the F5 refresh key.
                    key.Handled = true;
                    RequestRefresh();
                    break;
                case KeyCode.C:
                    // Ctrl+C as a quit alias (the OS/terminal may intercept it first).
                    key.Handled = true;
                    RequestExit();
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
                // Esc on the list is at the root (no screen is open — screens handle their own Esc), so
                // this is back-at-root: hand off to the exit seam (#298/#299) rather than quitting inline.
                // It stays an undisplayed alias for Ctrl+Q (the footer shows the Ctrl+Q command).
                key.Handled = true;
                RequestExit();
                break;
        }
    }

    /// <summary>
    /// Mouse gestures on the task list, all resolved through the shared <see cref="RowHitTester"/> (the
    /// click's viewport-relative Y plus the list's scroll offset <c>Viewport.Y</c>). Guarded on
    /// <see cref="ActiveScreen"/> so nothing fires while a screen is stacked over the list:
    /// <list type="bullet">
    /// <item><b>Ctrl+Left-Click a task row → open it in its own terminal tab</b> (#301), the mouse
    /// equivalent of Ctrl+Enter. Checked first so a Ctrl-modified click launches a tab rather than
    /// toggling a fold or opening detail; a header/spacer row no-ops.</item>
    /// <item><b>Double-click a task row → open Task Detail</b> (A, #286), the mouse equivalent of Enter.
    /// A double-click on a header/spacer row or the empty space beneath a short list resolves to a null
    /// task and no-ops, exactly like Enter there.</item>
    /// <item><b>Single-click a parent's ▶/▼ arrow → toggle its subtasks</b> (B, #287), the mouse
    /// equivalent of →/←. Gated on the subtasks view like the keyboard fold, and scoped to the narrow
    /// arrow column — a click anywhere else on the row is left unhandled so the ListView's native
    /// single-click selection still moves the cursor, and the row body stays free for A's
    /// double-click-to-open (so a title double-click can't be mistaken for two fold toggles).</item>
    /// </list>
    /// Every other mouse event is left unhandled, so native selection and drag-scroll are untouched.
    /// </summary>
    private void OnListMouse(object? sender, Mouse e)
    {
        if (e.Position is not { } pos || ActiveScreen is not null)
            return;

        // Ctrl+Left-Click opens the clicked task in its own terminal tab (#301) — the mouse equivalent
        // of Ctrl+Enter. Checked before the fold/double-click branches so a Ctrl-modified click launches
        // a tab rather than toggling a fold or opening detail. Resolves the row via the shared hit-tester.
        if (e.Flags.HasFlag(MouseFlags.LeftButtonClicked) && e.Flags.HasFlag(MouseFlags.Ctrl))
        {
            if (RowHitTester.TaskAt(pos.Y, _list.Viewport.Y, _rows) is { } ctrlTask)
            {
                e.Handled = true;
                LaunchTaskInNewTab(ctrlTask);
            }
            return;
        }

        // Double-click → open detail (A). Checked first and independent of the subtasks view.
        if (e.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked))
        {
            if (RowHitTester.TaskAt(pos.Y, _list.Viewport.Y, _rows) is not { } task)
                return;
            e.Handled = true;
            OpenTaskDetail(task.Id);
            return;
        }

        // Single-click within a foldable parent's arrow column → toggle its fold (B).
        if (e.Flags.HasFlag(MouseFlags.LeftButtonClicked) && _config.View.ShowSubtasks)
        {
            var index = RowHitTester.RowIndexAt(pos.Y, _list.Viewport.Y, _rows.Count);
            if (index < 0 || index >= _folds.Count
                || _folds[index] is not (FoldState.Collapsed or FoldState.Expanded))
                return;
            // _markerSpans is parallel to _folds/_display (grown together in AddTask/AddHeader/AddSpacer),
            // so the _folds bound above covers it and _display[index] below.
            var (markerStart, markerLength) = _markerSpans[index];
            // Measure with the same grapheme/column-aware GetColumns the renderer uses, so wide/emoji
            // badges ahead of the arrow don't shift the target column (mirrors HelpLine.HitTest).
            if (!RowHitTester.IsWithinFoldMarker(pos.X, _display[index], markerStart, markerLength, static s => s.GetColumns()))
                return;
            e.Handled = true;
            ToggleFoldAt(index);
        }
    }

    /// <summary>
    /// Ctrl+Enter / Ctrl+Left-Click — open <paramref name="task"/> in its own terminal tab (#301):
    /// resolves how to relaunch this app (<see cref="AppLaunchCommand.ForTask(string)"/>) and hands it to
    /// the shared cross-platform launcher off the UI thread, preferring a new tab of the current terminal
    /// (falling back to a new window per emulator support). On success the status line names the terminal;
    /// when no emulator can be launched, it flashes the exact command and copies it to the clipboard so
    /// the user can run it themselves (the issue's documented fallback). A null task (header/spacer row)
    /// no-ops. Re-entrancy-guarded so a rapid second gesture can't spawn duplicate tabs.
    /// </summary>
    private void LaunchTaskInNewTab(TaskItem? task)
    {
        if (task is null || ActiveScreen is not null)
            return;
        if (_launchingTab)
        {
            Flash("A task tab is already opening…");
            return;
        }

        // Resolve the command before arming the re-entrancy guard: ForTask is pure and could in principle
        // throw (a blank id), and doing it first means such a throw can't leave _launchingTab stuck true.
        var command = AppLaunchCommand.ForTask(task.Id);
        // A new tab of the current terminal where the host supports it (#255's LaunchLocation), honouring
        // the user's preferred-terminal setting on Windows. ClaudeExecutable/ExtraArgs don't apply to an
        // app launch, so a purpose-built options value (not AgentDispatch.ToLauncherOptions) is used.
        var options = new TerminalLauncherOptions
        {
            LaunchLocation = LaunchLocation.NewTab,
            Preferred = _config.AgentDispatch.PreferredTerminal,
        };
        _launchingTab = true;
        var name = task.Name;
        Flash($"Opening '{name}' in a new terminal tab…");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _tabLauncher.LaunchAppAsync(command, options);
                Application.Invoke(() =>
                {
                    _launchingTab = false;
                    if (!result.Success)
                    {
                        FlashLaunchFallback(command);
                        return;
                    }
                    var message = $"Opened '{name}' in a new tab ({result.LaunchedWith}).";
                    Flash(string.IsNullOrWhiteSpace(result.Note) ? message : $"{message} {result.Note}");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => { _launchingTab = false; FlashLaunchFallback(command, ErrorText.Short(ex)); });
            }
        });
    }

    /// <summary>The no-terminal fallback (#301): flash the exact command and copy it to the clipboard so
    /// the user can open the task tab themselves. <paramref name="reason"/> names the failure when the
    /// launch threw (vs. simply finding no emulator).</summary>
    private void FlashLaunchFallback(AppLaunchCommand command, string? reason = null)
    {
        var cmd = command.ToDisplayCommand();
        var lead = reason is null ? "Couldn't open a terminal tab." : $"Couldn't open a terminal tab ({reason}).";
        Flash(TryCopyToClipboard(cmd)
            ? $"{lead} Command copied to clipboard: {cmd}"
            : $"{lead} Run: {cmd}");
    }

    /// <summary>Best-effort clipboard copy for the fallback; a headless/unsupported clipboard just yields
    /// false so the caller shows the run-it-yourself form instead.</summary>
    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            return Clipboard.TrySetClipboardData(text);
        }
        catch (Exception)
        {
            return false;
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

    /// <summary>F6 on the Task Detail's Task Tree tab (#415) — the tab's counterpart to the main list's
    /// <see cref="CycleBadgeDisplay"/>. Cycles + persists the shared <see cref="AppConfig.BadgeDisplay"/>
    /// (Icons → Text → Hidden), reflects the new mode into <b>every</b> stacked detail's tree (a pure
    /// in-place re-render) and the hidden main list, so Esc-ing back through the visited-task chain shows
    /// the same mode everywhere. Runs on the UI thread from the screen's key handler; no-op if the raising
    /// screen isn't front-most.</summary>
    private void CycleTreeBadgeDisplay(TaskDetailScreen screen)
    {
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        var mode = _config.BadgeDisplay.Next();
        _config.BadgeDisplay = mode;
        _configStore.Save(_config);
        // Reflect into every detail on the back-stack, not just the front-most: the tree tab navigates
        // detail→detail (Enter a child stacks its detail over this one, #291), so a cycle here must keep
        // the trees beneath in step or Esc-ing back would surface a stale mode. Each call is a no-op for a
        // detail whose tree hasn't loaded, so the beneath-screens just adopt the mode for their next render.
        foreach (var stacked in _screens)
            if (stacked is TaskDetailScreen detail)
                detail.SetTreeBadgeDisplay(mode);
        // Keep the (hidden) main list in step so the badges match when the detail closes — the same
        // pure re-decorate the main list's own F6 does. Its cursor stays on the current task.
        Render(keepTaskId: CurrentTask()?.Id);
        Flash(mode.Describe());
    }

    /// <summary>F5 (and its Ctrl+R alias) — refresh now: flashes and wakes the background poll loop.</summary>
    private void RequestRefresh()
    {
        Flash("Refreshing…");
        _refresh.RequestRefresh();
    }

    /// <summary>
    /// Ctrl+E — opens the mentions &amp; comments feed screen (#114, epic #109), the List ↔ Feed
    /// navigation key. On a <b>warm cache</b> (#123) the last aggregated feed paints immediately and a
    /// background <see cref="RefreshFeed"/> then swaps in live data — so the screen opens on the first
    /// frame with no "Loading…" wait. On a <b>cold cache</b> it keeps the original flow: fetch off the
    /// UI thread (like <see cref="OpenDetail"/>: flash → <c>Task.Run</c> → <c>Application.Invoke</c>)
    /// and construct the screen only on success. Either way the background dashboard refresh keeps
    /// running, the open is guarded on <see cref="ActiveScreen"/> like the other list-initiated opens,
    /// and the full feed is loaded once (every entry mention-stamped) so the screen's F3 mentions-only
    /// toggle filters locally with no re-fetch.
    /// </summary>
    private void OpenNotificationsFeed()
    {
        if (ActiveScreen is not null)
            return;

        // Warm cache (#123): paint the last aggregated feed instantly, then refresh live in the
        // background. An empty cached feed is treated as a miss (nothing to instant-paint) and takes the
        // cold path below. Load runs on the UI thread, matching the store's single-threaded contract.
        if (_feedCache.LoadSnapshot(_config) is { Items.Count: > 0 } cached)
        {
            // The cache stores only the aggregated comments (#123); the recent-activity source (#117) is
            // re-derived by the near-immediate RefreshFeed below, so the instant paint opens comments-only
            // and activity fills in a moment later when F6 is on.
            var screen = CreateFeedScreen(new FeedResult(cached.Items, []));
            ShowScreen(screen, static () => { });
            // Mark how stale the painted feed is (#124); the live refresh replaces it moments later.
            var age = RelativeTime.Format(DateTimeOffset.UtcNow - cached.CapturedAt);
            Flash($"Showing cached feed from {age} · refreshing…");
            RefreshFeed(screen); // off-thread live load, swaps fresh in + re-saves the cache
            return;
        }

        Flash("Loading feed…");
        // Capture the completed flag + its cache key at fetch-start so the result is fetched with, and
        // saved under, one consistent fingerprint (see RefreshFeed). No feed screen exists yet, so the
        // flag can't be toggled during this cold load, but capturing keeps the two fetch paths uniform.
        var includeClosed = _config.FeedShowCompleted;
        var cacheKey = FeedCache.KeyFor(_config);
        _ = Task.Run(async () =>
        {
            try
            {
                var feed = await _feed.LoadFeedAsync(includeClosed, mentionsOnly: false);
                Application.Invoke(() =>
                {
                    if (ActiveScreen is not null)
                        return;
                    // Cache the freshly-aggregated comments so the next open paints instantly (#123); the
                    // activity source (#117) is display-only and re-derived on refresh, so it isn't cached.
                    _feedCache.Save(cacheKey, feed.Comments);
                    ShowScreen(CreateFeedScreen(feed), static () => { });
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load feed: {ErrorText.Short(ex)}"));
            }
        });
    }

    /// <summary>
    /// Builds a <see cref="NotificationsFeedScreen"/> over <paramref name="feed"/> with the host's
    /// event wiring: F5 / Ctrl+R and the auto-refresh tick re-fetch via <see cref="RefreshFeed"/>, Enter
    /// on a row opens that comment's task detail stacked over the feed (#115, Esc returns here), and F12
    /// toggles whether completed-task activity is included (the feed's own "Show Completed", independent
    /// of the list's #178). The feed runs on its own longer cadence
    /// (<see cref="AppConfig.FeedRefreshSeconds"/>, #123), independent of the dashboard task-list poll,
    /// because assembling it is far heavier.
    /// </summary>
    private NotificationsFeedScreen CreateFeedScreen(FeedResult feed)
    {
        var screen = new NotificationsFeedScreen(
            feed.Comments, feed.Activity, _config.FeedRefreshSeconds,
            showCompleted: _config.FeedShowCompleted, showActivity: _config.FeedShowActivity);
        screen.RefreshRequested += (_, _) => RefreshFeed(screen);
        // F12 changes what's fetched (closed tasks were never loaded while off), so unlike the F3 local
        // filter the host persists the flag and re-fetches — see ToggleFeedShowCompleted.
        screen.ToggleCompletedRequested += (_, _) => ToggleFeedShowCompleted(screen);
        // F6 (#117) is a pure display toggle — the activity is already loaded — so the host only persists
        // the flag and reflects it back; no re-fetch. See ToggleFeedShowActivity.
        screen.ToggleActivityRequested += (_, _) => ToggleFeedShowActivity(screen);
        screen.OpenTaskRequested += (_, taskId) => OpenTaskDetail(taskId);
        return screen;
    }

    /// <summary>
    /// Re-fetches the feed for an open <see cref="NotificationsFeedScreen"/> (its F5 / Ctrl+R or
    /// auto-refresh tick, or the initial background load behind a warm-cache open) and feeds it back on
    /// the UI thread, re-saving the cache after each successful aggregation (#123). Mirrors
    /// <see cref="OpenNotificationsFeed"/>'s off-thread fetch; skips while the feed isn't front-most,
    /// and drops the screen update if it has since been torn down. A fetch error flashes without
    /// disturbing the view.
    /// </summary>
    private void RefreshFeed(NotificationsFeedScreen screen)
    {
        // Runs on the UI thread (from the screen's key handler or its timer tick), so ActiveScreen is a
        // valid read: no point fetching to update a feed that isn't showing.
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        // Coalesce: the feed fan-out (a comment fetch per assigned task) can outlast the refresh cadence
        // on a large workspace. While one is in flight, don't start a second — but remember that another
        // was asked for and run it once the current one lands, rather than dropping it. This keeps ticks
        // from piling up (at most one queued) while never losing a state-changing request such as an F12
        // toggle, which flips a KeyFor-relevant flag and needs the fetch to actually happen. The flags are
        // only touched on the UI thread (here and the finally's Invoke), so no locking is needed.
        if (_refreshingFeed)
        {
            _feedRefreshPending = true;
            return;
        }
        _refreshingFeed = true;

        // Capture the completed flag and its matching cache key on the UI thread at fetch-start. F12 can
        // flip the flag while this fetch runs; capturing here means the result is fetched with, and saved
        // under, one consistent fingerprint — so the cache never files open-only data under the completed
        // key (or vice-versa), and the pending re-fetch below picks up the new flag on its own pass.
        var includeClosed = _config.FeedShowCompleted;
        var cacheKey = FeedCache.KeyFor(_config);

        _ = Task.Run(async () =>
        {
            try
            {
                var feed = await _feed.LoadFeedAsync(includeClosed, mentionsOnly: false);
                Application.Invoke(() =>
                {
                    // Cache the freshly-aggregated comments regardless of whether the screen is still open —
                    // the data is valid for the next open either way (#123) — under the fingerprint it was
                    // fetched with (captured above), not the (possibly since-toggled) live one. The activity
                    // source (#117) rides along on the in-memory result but isn't persisted (display-only).
                    _feedCache.Save(cacheKey, feed.Comments);
                    if (_screens.Contains(screen))
                    {
                        screen.UpdateFeed(feed);
                    }
                    else if (ActiveScreen is NotificationsFeedScreen active)
                    {
                        // The instance that started this fetch was torn down (a close+reopen during the
                        // in-flight refresh), but a feed screen is front-most again. Because the warm-open
                        // reopen is coalesced away by `_refreshingFeed`, dropping this result would leave
                        // the reopened feed on cached data until the next tick — so land the fresh,
                        // context-correct result on the current feed instead (#123 review).
                        active.UpdateFeed(feed);
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh feed: {ErrorText.Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() =>
                {
                    _refreshingFeed = false;
                    // Run the queued refresh (e.g. an F12 toggle that arrived mid-fetch), on the feed
                    // that's front-most now, so its new flag actually takes effect.
                    if (_feedRefreshPending && ActiveScreen is NotificationsFeedScreen pending)
                    {
                        _feedRefreshPending = false;
                        RefreshFeed(pending);
                    }
                    else
                    {
                        _feedRefreshPending = false;
                    }
                });
            }
        });
    }

    /// <summary>
    /// Toggles the feed's F12 "Show Completed" — whether the feed includes activity from completed
    /// (closed-type) tasks — and persists it (<see cref="AppConfig.FeedShowCompleted"/>). Independent of
    /// the main list's F12 (#178/#191), which owns <see cref="ViewSettings.Completed"/>. Because the
    /// closed tasks were never fetched, a client-side re-render can't surface them: this re-fetches via
    /// <see cref="RefreshFeed"/> after reflecting the new state in the screen's title. If a refresh is
    /// already in flight the re-fetch is queued (not dropped), so the toggle always takes effect. Runs on
    /// the UI thread (from the screen's key handler); no-op if that screen isn't front-most.
    /// </summary>
    private void ToggleFeedShowCompleted(NotificationsFeedScreen screen)
    {
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        var on = !_config.FeedShowCompleted;
        _config.FeedShowCompleted = on;
        _configStore.Save(_config);
        screen.SetShowCompleted(on);
        Flash(on ? "Feed: showing completed tickets (F12)." : "Feed: completed tickets hidden (F12).");
        RefreshFeed(screen);
    }

    /// <summary>
    /// Toggles the feed's F6 "show activity" — whether the recent-activity source (#117), the user's
    /// recently-updated assigned tasks, is merged into the feed — and persists it
    /// (<see cref="AppConfig.FeedShowActivity"/>). Unlike F12 (<see cref="ToggleFeedShowCompleted"/>) the
    /// activity is already loaded alongside the comments, so this is a pure client-side re-render:
    /// <see cref="NotificationsFeedScreen.SetShowActivity"/> rebuilds the rows locally with <b>no
    /// re-fetch</b>. Runs on the UI thread (from the screen's key handler); no-op if that screen isn't
    /// front-most.
    /// </summary>
    private void ToggleFeedShowActivity(NotificationsFeedScreen screen)
    {
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        var on = !_config.FeedShowActivity;
        _config.FeedShowActivity = on;
        _configStore.Save(_config);
        screen.SetShowActivity(on);
        // "enabled/disabled" describes the persisted toggle, not the view: under the F3 mentions-only
        // filter the activity rows stay suppressed, so a "showing/hidden" claim would be misleading.
        Flash(on ? "Feed: recent activity enabled (F6)." : "Feed: recent activity disabled (F6).");
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

    /// <summary>
    /// Cycles the three-state F12 completed view (#191): Active (hide done + closed) -> WithDone (show
    /// done, hide closed) -> All (show everything) -> …, persisting the choice. The display gate in
    /// <see cref="TaskView"/> re-renders immediately from the current snapshot. Only reaching
    /// <see cref="CompletedView.All"/> requests a refresh, since that's the one state whose fetch must
    /// return closed-type tasks the server drops otherwise — done-type arrives regardless, so
    /// Active↔WithDone is a pure client-side re-render (mirrors <see cref="CycleSubtaskView"/>'s
    /// Hidden→on refresh; a Manual refresh is a full fetch).
    /// </summary>
    private void CycleShowCompleted()
    {
        if (ActiveScreen is not null)
            return;

        var next = _config.View.Completed.Next();
        _config.View.Completed = next;
        _configStore.Save(_config);

        // Bridge paint (#253): entering All, splice the warm closed set into the snapshot so closed rows
        // appear immediately instead of after the on-demand include_closed=true fetch below returns. The
        // authoritative refresh replaces _all with a superset, so this is a transient bridge, never an
        // overlay; SupplementWithClosed returns the same instance (no-op) when the cache is empty or its
        // tasks are already present.
        var flash = next.Describe();
        if (next == CompletedView.All)
        {
            _all = _tasks.SupplementWithClosed(_all);
            if (_closedPrefetchDropped > 0)
                flash += " Older completed omitted until refresh.";
        }
        Flash(flash);

        Render(keepTaskId: CurrentTask()?.Id);
        _signature = CurrentSignature(_all);
        if (next == CompletedView.All)
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

        var screen = new SettingsScreen(_config.RefreshSeconds, _config.FeedRefreshSeconds, _config.FeedActivityLookbackDays, _config.DefaultWorkingDirectory, _config.WorkspaceSubdomain, _config.AgentDispatch, _config.DetailView);

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
            // The feed screen reads FeedRefreshSeconds when it opens (#123), so a reopened feed picks
            // up the new cadence — no live retiming of an already-open feed's timer is needed.
            _config.FeedRefreshSeconds = result.FeedRefreshSeconds;
            // The look-back window (#244) is read live by FeedService on the next load, so a reopened
            // (or next-polled) feed picks up the new window with no extra wiring.
            _config.FeedActivityLookbackDays = result.FeedActivityLookbackDays;
            _config.DefaultWorkingDirectory = result.DefaultWorkingDirectory;
            // Read live by LaunchBrowser (#304) on the next Ctrl+B, so a saved change takes effect
            // immediately with no extra wiring.
            _config.WorkspaceSubdomain = result.WorkspaceSubdomain;
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

    /// <summary>
    /// Ctrl+N — opens the New Task compose screen (#213/#240) over the list. Guarded on
    /// <see cref="ActiveScreen"/> like the other list-initiated opens (only Help stacks). Requires a
    /// configured Personal Tasks list (the fallback seed / create target when the cursor can't supply one);
    /// flashes and no-ops when unset. The embedded assignee selector draws its candidate pool from the #155
    /// frequency cache and seeds the current user as a locked default; the List selector (#239) draws from
    /// the #238 list-frequency cache and is seeded with the cursor's list as the primary/home create target
    /// (personal-list fallback — see <see cref="NewTaskForm.ResolveListSeed"/>). On success the list
    /// refreshes and the cursor lands on the new task.
    /// </summary>
    private void OpenNewTask()
    {
        if (ActiveScreen is not null)
            return;

        var listId = _config.PersonalTasksListId;
        if (string.IsNullOrWhiteSpace(listId))
        {
            Flash("No Personal Tasks list is configured — run setup to choose one.");
            return;
        }

        // The locked-self default needs a non-blank name, else the selector drops it silently.
        var selfName = string.IsNullOrWhiteSpace(_tasks.UserName) ? "Me" : _tasks.UserName;
        var self = new TaskAssignee(_tasks.UserId, selfName);

        // Seed the List selector's primary/home create target (#240) from the cursor's task list, falling
        // back to the configured Personal Tasks list on a header row (no current task), a context parent
        // (#46), a foreign subtask (#70/#179), or a task with a blank list id. The context/foreign
        // classification reuses the same membership the row markers read.
        var cursor = CurrentTask();
        var primaryList = NewTaskForm.ResolveListSeed(
            cursorListId: cursor?.ListId,
            cursorListName: cursor?.ListName,
            cursorIsContextParent: cursor is not null && _contextParents.ContainsKey(cursor.Id),
            cursorIsForeignSubtask: cursor is not null && _foreignSubtasks.ContainsKey(cursor.Id),
            personalListId: listId!,
            personalListName: _config.PersonalTasksListName);

        var screen = new NewTaskScreen(
            match: (query, exclude) => _assignees.Match(query, exclude),
            topFrequent: (n, exclude) => _assignees.TopMostFrequent(n, exclude),
            lockedSelf: self,
            listMatch: (query, exclude) => _lists.Match(query, exclude),
            listTopFrequent: (n, exclude) => _lists.TopMostFrequent(n, exclude),
            primaryList: primaryList,
            createAsync: (targetListId, request, ct) => _tasks.CreateTaskAsync(targetListId, request, ct),
            addToListAsync: (taskId, targetListId, ct) => _tasks.AddTaskToListAsync(taskId, targetListId, ct));
        screen.Created += (_, result) =>
        {
            // Land the next refresh on the new task, then kick that refresh directly (RequestRefresh's
            // own "Refreshing…" flash would clobber this confirmation). The task always exists here (#241);
            // when an additional-list add failed, name the lists so the outcome is unambiguous. (While
            // multi-list create is disabled pending #365, no adds fire, so the partial-failure branch is
            // dormant — kept wired for when the NewTaskScreen re-enables the additional-list adds.)
            var created = result.Created;
            _pendingSelectId = created.Id;
            Flash(result.AllListsSucceeded
                ? $"Created “{created.Name}” · refreshing…"
                : $"Created “{created.Name}”, but couldn't add to {DescribeLists(result.FailedAdditionalLists)} · refreshing…");
            _refresh.RequestRefresh();
        };
        ShowScreen(screen, static () => { });
    }

    /// <summary>
    /// Names the additional lists a multi-list create couldn't add the task to (#241), for the confirmation
    /// flash — the list names, comma-separated, falling back to the list id when a name is blank so the
    /// message never renders an empty entry.
    /// </summary>
    private static string DescribeLists(IReadOnlyList<NamedEntity> lists)
        => string.Join(", ", lists.Select(l => string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name));

    /// <summary>
    /// Ctrl+O — opens the quick-open entry surface (#303) over the list. Guarded on
    /// <see cref="ActiveScreen"/> like the other list-initiated opens. The modal only collects the typed
    /// text; the parse/resolve/navigate runs in <see cref="ResolveAndOpen"/> once the modal has closed
    /// (deferred to the next loop iteration) so the Task Detail view opens over the list rather than
    /// stacking on top of the entry surface.
    /// </summary>
    private void OpenQuickOpen()
    {
        if (ActiveScreen is not null)
            return;

        ShowQuickOpenSurface();
    }

    /// <summary>
    /// Ctrl+O from an open Task Detail (#353): opens the same quick-open entry surface stacked over the
    /// detail. Unlike the list entry point it does not guard on <see cref="ActiveScreen"/> (the detail
    /// <em>is</em> the active screen); resolving a target opens its Task Detail over the current one, so
    /// Esc walks back — mirroring how Quick Updates (Ctrl+U) stacks over the detail.
    /// <para>
    /// This detail→detail navigation rides the single <see cref="_screens"/> back-stack, which is
    /// #401/#298's model: <c>Esc</c> = Back walks it one screen at a time and, at the list root,
    /// <see cref="RequestExit"/> handles quit. #401 shipped the reusable <see cref="NavigationHistory{T}"/>
    /// but no host consumes it yet (its first host consumer is #291, PR #373, which the issue says
    /// <em>drives</em> the shared history). When #291 introduces the dashboard's <c>NavigationHistory</c>,
    /// this open is the other detail→detail source that must push onto that same history so there is one
    /// back-stack, not two — see the note left on #373.
    /// </para>
    /// </summary>
    private void OpenQuickOpenFromScreen() => ShowQuickOpenSurface();

    /// <summary>Shared entry-surface opener behind both quick-open entry points (list Ctrl+O and detail
    /// Ctrl+O, #303/#353). The parse/resolve/navigate runs in <see cref="ResolveAndOpen"/> once the modal
    /// has closed (deferred to the next loop iteration) so the Task Detail opens over whatever was beneath
    /// the entry surface rather than stacking on top of the surface itself.</summary>
    private void ShowQuickOpenSurface()
    {
        var screen = new QuickOpenScreen();
        ShowScreen(screen, () =>
        {
            // Run the resolve on a later main-loop iteration so this entry surface is fully torn down
            // first: doing it inline in the close handler fires it while the modal is still mounted, so
            // OpenTaskDetail captures the modal as its "requester" and then skips the mount once the modal
            // closes (observed under the tui-validate harness — "Loading details…" stuck, detail never
            // shown). AddTimeout guarantees the later iteration.
            if (screen.Result is { } text)
                Application.AddTimeout(TimeSpan.FromMilliseconds(1), () =>
                {
                    ResolveAndOpen(text);
                    return false;
                });
        });
    }

    /// <summary>
    /// Resolves a quick-open input to a task and opens its Task Detail (#303). Cache-first: a task in the
    /// current working set opens with no round-trip; an uncached one flashes "Fetching task…" first and
    /// resolves via the API (a plain id straight through <see cref="OpenTaskDetail"/>; a custom id via the
    /// <c>custom_task_ids</c> lookup, then opened by its real id). An unparseable input, a missing
    /// workspace for a custom id, or a not-found task flashes an error and leaves the list unchanged.
    /// </summary>
    private void ResolveAndOpen(string text)
    {
        var reference = QuickOpenParser.Parse(text);
        if (reference.Kind == QuickOpenKind.Invalid)
        {
            Flash($"Couldn’t open “{Ellipsize(text)}” — enter a task id, custom id, or ClickUp task URL.");
            return;
        }

        // 1. Cache hit → open immediately (its own "Loading details…").
        if (QuickOpenParser.FindInCache(CandidateUniverse(), reference) is { } cached)
        {
            OpenTaskDetail(cached.Id);
            return;
        }

        // A custom-id URL carries its own team id (#353) — prefer it over the configured workspace so a
        // URL pasted from a different workspace resolves against that workspace, not this one.
        var teamId = string.IsNullOrWhiteSpace(reference.TeamId) ? _config.WorkspaceId : reference.TeamId;

        // 2. Uncached plain id → straight through the detail load (its own "Loading details…" flash IS
        // the fetch; there's no separate resolve step, so no redundant "Fetching task…" here). A bare
        // hyphenless token parses as a plain id but may actually be a custom id (#353): pass the team id
        // as a fallback so OpenTaskDetail retries as a custom id if the plain load 404s.
        if (reference.Kind == QuickOpenKind.TaskId)
        {
            OpenTaskDetail(reference.Value, customIdFallbackTeamId: teamId);
            return;
        }

        // 3. A custom id needs the workspace (team) id and a resolve step — the custom-id lookup returns
        // the task's real id, which is then opened through the ordinary detail load. "Fetching task…"
        // covers that resolve round-trip (visible while the off-thread lookup is in flight), after which
        // OpenTaskDetail's "Loading details…" covers the load.
        if (string.IsNullOrWhiteSpace(teamId))
        {
            Flash($"Can’t resolve custom id “{Ellipsize(reference.Value)}” — no workspace is configured.");
            return;
        }

        Flash("Fetching task…");
        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await _tasks.GetTaskDetailByCustomIdAsync(reference.Value, teamId);
                Application.Invoke(() => OpenTaskDetail(detail.Id));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Couldn’t find task “{Ellipsize(reference.Value)}”: {ErrorText.Short(ex)}"));
            }
        });
    }

    /// <summary>Clips an echoed user input to a short, single-line snippet for a flash message.</summary>
    private static string Ellipsize(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= 40 ? s : s[..39] + "…";
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

    /// <summary>
    /// The single "quit from the list root" chokepoint — the exit-confirmation seam (#298, #299
    /// sub-issue 7). <c>Esc</c> is the canonical Back key; the dashboard's root is the main list, so
    /// Back <em>at the root</em> is a quit, and every quit path there (the <see cref="KeyAction.Quit"/>
    /// binding, <c>Esc</c>, <c>Ctrl+C</c>) routes here instead of calling <c>Application.RequestStop</c>
    /// directly, which is what lets #299's confirmation modal live here alone.
    /// <para>
    /// #299: it now asks before quitting. The <see cref="ExitConfirmScreen"/> is mounted like any other
    /// transient modal (<c>docs/navigation-model.md</c>) over the hidden list; answering yes stops the
    /// app, answering no tears the modal down and restores the list with the cursor untouched (a
    /// <see cref="CloseScreen"/> never rebuilds the <c>ListView</c>). Re-entrancy is guarded so mashing
    /// Esc/Ctrl+Q can't stack two questions.
    /// </para>
    /// <para>
    /// Per #298 the planned Alt+←/→ chord was dropped (it collides with terminal-emulator split-pane
    /// navigation, e.g. Windows Terminal); <c>Esc</c> = Back is canonical and there is no Forward key.
    /// Browser-style forward/back <em>across visited tasks</em> — and the <see cref="NavigationHistory{T}"/>
    /// #401 landed as its mechanism — is driven by the detail→detail navigation in #291.
    /// </para>
    /// </summary>
    private void RequestExit()
    {
        if (ActiveScreen is ExitConfirmScreen)
            return;

        var confirm = new ExitConfirmScreen();
        ShowScreen(confirm, () =>
        {
            if (confirm.Confirmed)
                Application.RequestStop();
        });
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
        // The footer fits the items to its current width and returns exactly what it rendered; cache that
        // so a footer click hit-tests against the on-screen items (#289).
        => _helpFooter = _footer.RenderHelp(HelpLine.ForActiveScreen(ActiveScreen?.HelpItems, HelpItemSets.MainList));

    /// <summary>
    /// A left-click on the contextual footer (#289): resolves the clicked item via
    /// <see cref="HelpLine.HitTest"/> and, when it lands on a clickable <em>action</em> hint, re-raises
    /// that hint's keyboard chord through <c>Application.RaiseKeyDownEvent</c> — so the click converges
    /// on the same handler as the keypress (on the focused ListView or the active screen's control) with
    /// no duplicated action logic. A click on a movement/informational hint, a separator, or the empty
    /// space beyond the text is left unhandled (native behaviour; the footer never takes focus).
    /// </summary>
    private void OnHelpBarMouse(object? sender, Terminal.Gui.Input.Mouse e)
    {
        if (!e.Flags.HasFlag(MouseFlags.LeftButtonClicked) || e.Position is not { } pos)
            return;

        var index = HelpLine.HitTest(_helpFooter, pos.X, static s => s.GetColumns());
        if (index < 0)
            return;

        var item = _helpFooter[index];
        if (!item.IsAction || !Key.TryParse(item.ActionKey, out var key))
            return;

        e.Handled = true;
        Application.RaiseKeyDownEvent(key);
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
            case FoldState.Collapsed:
                SetFold(i, expand: true);
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

        if (_folds[i] == FoldState.Expanded && _rows[i]?.Id is not null)
        {
            SetFold(i, expand: false);
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
            SetFold(j, expand: false);
        }
        else
        {
            _list.SelectedItem = j; // context parent (not foldable) — just select it
        }
    }

    /// <summary>
    /// The single fold mutation both the keyboard (→/←, #76) and a mouse arrow-click (#287) converge on:
    /// expand or collapse the parent on row <paramref name="index"/> by toggling its id in the ephemeral
    /// <see cref="_expanded"/> set and re-rendering with the cursor kept on it. A no-op on any row that
    /// isn't a foldable parent (its <see cref="FoldState"/> isn't Collapsed/Expanded, or it carries no
    /// task id), so callers may pass an arbitrary row index. Keeps <see cref="_expanded"/> + the arranger
    /// the one source of truth for fold state.
    /// </summary>
    private void SetFold(int index, bool expand)
    {
        if (index < 0 || index >= _folds.Count || index >= _rows.Count)
            return;
        if (_folds[index] is not (FoldState.Collapsed or FoldState.Expanded))
            return;
        if (_rows[index]?.Id is not { } id)
            return;

        if (expand)
            _expanded.Add(id);
        else
            _expanded.Remove(id);
        Render(keepTaskId: id);
    }

    /// <summary>
    /// Toggle the fold on row <paramref name="index"/> — collapse an expanded parent, expand a collapsed
    /// one. The mouse arrow-click (#287) equivalent of →/←; a no-op on any non-foldable row (guarded by
    /// <see cref="SetFold"/>).
    /// </summary>
    private void ToggleFoldAt(int index)
    {
        if (index < 0 || index >= _folds.Count)
            return;
        SetFold(index, expand: _folds[index] == FoldState.Collapsed);
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

    /// <summary>The not-mine / context classification for a single row — the three trailing-marker
    /// flags <see cref="TaskRowFormatter.Format"/> reads (#264). Pure so the in-place
    /// <see cref="UpdateTaskRow"/> derives the same marker the full render path gives the row,
    /// instead of silently dropping it. <paramref name="contextParents"/> keys are disjoint from the
    /// snapshot (a context parent is never in <c>_all</c>), so <c>ContainsKey</c> reproduces the
    /// render path's <c>row.IsContextParent</c> for the one row an in-place update touches.</summary>
    internal static (bool IsContextParent, bool IsForeignSubtask, bool IsUnassignedSubtask) ClassifyRowMarker(
        TaskItem task,
        IReadOnlyDictionary<string, TaskItem> contextParents,
        IReadOnlyDictionary<string, TaskItem> visibleForeign)
        => (contextParents.ContainsKey(task.Id),
            IsForeignOthers(task, visibleForeign),
            IsForeignUnassigned(task, visibleForeign));

    // ── Actions ────────────────────────────────────────────────────────────

    private void TogglePin()
    {
        var task = CurrentTask();
        if (task is null)
            return;
        // A subtask pulled in under my parent (not in my snapshot, #70/#179) isn't part of my work, so
        // pinning it would be a no-op (Focus renders from _all). Refuse it with a clear message, worded for
        // whether it's unassigned (shown in the F4 "mine + unassigned" state) or assigned to someone else.
        if (_foreignSubtasks.ContainsKey(task.Id))
        {
            Flash(SubtaskVisibility.IsUnassigned(task)
                ? "This subtask isn't assigned to anyone — nothing to pin."
                : "This subtask isn't assigned to you — nothing to pin.");
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
            Application.Invoke(() => Flash($"Could not update focus: {ErrorText.Short(ex)}"));
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
        // Shared rewrite (app.clickup.com → workspace subdomain, #304) + parse + open (#346); the
        // dashboard has a live status line, so unlike the single-task host it flashes each outcome.
        var (result, target) = ClickUpTaskBrowser.Open(_browser, url, _config.WorkspaceSubdomain);
        switch (result)
        {
            case ClickUpTaskBrowser.Result.NoUrl:
                Flash("No URL for this task.");
                break;
            case ClickUpTaskBrowser.Result.InvalidUrl:
                Flash($"Not a valid URL: {target}");
                break;
            case ClickUpTaskBrowser.Result.Opened:
                Flash($"Opened: {name}");
                break;
            case ClickUpTaskBrowser.Result.LaunchFailed:
                var hint = BrowserLaunchPlanner.OpenerHint(BrowserLaunchPlanner.CurrentOS());
                Flash(hint is null ? $"Couldn't open a browser — copy the URL: {target}" : $"Couldn't open a browser ({hint}) — copy the URL: {target}");
                break;
        }
    }

    private void OpenDetail()
    {
        var task = CurrentTask();
        if (task is null || ActiveScreen is not null)
            return;

        OpenTaskDetail(task.Id);
    }

    /// <summary>
    /// Loads a task's detail + comments off the UI thread and mounts a <see cref="TaskDetailScreen"/>
    /// stacked on the current layer — the list (from <see cref="OpenDetail"/>) or the feed (from an
    /// Enter on a feed entry, #115). Captures the layer that requested the open and, once the fetch
    /// lands, only mounts when it is still the active layer: from the list that means "still idle" (a
    /// second open is blocked, matching the old guard); from the feed it means the detail stacks over
    /// the feed and a second Enter is a no-op (the detail is by then active). Esc closes the detail and
    /// the screen seam restores the layer beneath with its selection intact.
    /// </summary>
    private void OpenTaskDetail(string taskId, string? customIdFallbackTeamId = null)
    {
        var requester = ActiveScreen;
        Flash("Loading details…");
        // Fetch the detail + comments off the UI thread, then swap in the detail screen back on it.
        // The background dashboard refresh keeps running while the screen is open.
        _ = Task.Run(async () =>
        {
            try
            {
                // A bare hyphenless custom id parses as a plain id (#353); the fallback team id (when
                // set) lets the fetch retry it as a custom id on a 404. For a real id it's a plain load.
                var detail = await _tasks.GetTaskDetailWithCustomIdFallbackAsync(taskId, customIdFallbackTeamId);
                // Load comments / wire the composer + editor by the RESOLVED id — identical to taskId for a
                // real id, and correct when a fallback resolved a custom id to its real task id.
                var resolvedId = detail.Id;
                var comments = await _tasks.GetTaskCommentsWithRepliesAsync(resolvedId);
                Application.Invoke(() =>
                {
                    if (ActiveScreen != requester)
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
                        // Seed the per-dispatch launch-location toggle (#275) from the persisted default
                        // (#255/#274); the user can override it per dispatch without changing the default.
                        defaultLaunchLocation: _config.AgentDispatch.LaunchLocation,
                        // Pre-fill the Dispatch working-dir field from the per-task cache (#96) — the
                        // last explicit dir dispatched from this task, or blank if none. Read live on
                        // each pane open so a dispatch within this same open screen is reflected on reopen.
                        workingDirectoryPreFill: () => DispatchWorkingDirectoryCache.PreFill(_config.TaskWorkingDirectories, detail.Id),
                        // Ctrl+N (#216) composes + posts a plain-text comment; the screen owns the
                        // optimistic append/revert, the host owns the off-thread ClickUp write.
                        postCommentAsync: (text, ct) => _tasks.CreateTaskCommentAsync(resolvedId, text, ct),
                        // Ctrl+E (#217) edits the plain-text description; the screen owns the editor +
                        // dirty-check + in-place reflection, the host owns the off-thread ClickUp write.
                        setDescriptionAsync: (text, ct) => _tasks.SetTaskDescriptionAsync(resolvedId, text, ct),
                        // The Task Tree tab (#291) renders ancestry/children with the trailing Assignees
                        // badge (#161), so it needs the signed-in user's id. Its badge mode is seeded from
                        // the persisted BadgeDisplay so the tree opens in the same state as the main list
                        // (#415), and F6 on the tab cycles it in place (see CycleTreeBadgeDisplay). The tree
                        // itself is fetched lazily off the UI thread on first cycle to the tab, keyed off the
                        // RESOLVED id (identical to taskId for a real id; correct for a custom-id fallback).
                        currentUserId: _tasks.UserId,
                        treeBadgeDisplay: _config.BadgeDisplay,
                        loadTaskTreeAsync: ct => _tasks.GetTaskTreeAsync(resolvedId, ct));
                    // Ctrl+A (in the detail view) → compose + launch a claude session (#26/#93). The
                    // detail view stays open; dispatch runs off the UI thread so the TUI stays live. The
                    // prompt, the one-off/interactive mode (#94), the working dir (#95), the
                    // post-to-Comments flag (#97), and the per-dispatch launch location (#275) are
                    // consumed. The detail view opens on the configured tab/sort/scroll (#108).
                    screen.AgentDispatchRequested += (_, request) => DispatchAgent(detail, comments, request);
                    // F5 / Ctrl+R and the screen's own 30s tick ask for fresh data; re-fetch off the UI
                    // thread and feed it back into the still-open screen (its tab/scroll stay put).
                    screen.RefreshRequested += (_, _) => RefreshDetail(screen, resolvedId);
                    // Ctrl+U opens Quick Updates for the detail's task, stacked over it; Esc pops back
                    // here (#159). Reads the screen's current task so a mid-view refresh is reflected.
                    screen.QuickUpdatesRequested += (_, _) => OpenQuickUpdatesForDetail(screen);
                    // The Task Tree tab (#291): Enter/double-click a tree row opens that task's detail
                    // stacked over this one, so Esc walks back one task at a time — uniform with the
                    // canonical "Esc = Back" decision (#401/#298) and with the Ctrl+O detail→detail path
                    // (#387). No replace-in-place: the visited-task chain is the single _screens back-stack.
                    screen.OpenTaskRequested += (_, id) => OpenTaskDetail(id);
                    // F6 on the Task Tree tab (#415) cycles the tree's badge display just like the main
                    // list; the host owns the flip/persist so both surfaces share one BadgeDisplay.
                    screen.CycleBadgeDisplayRequested += (_, _) => CycleTreeBadgeDisplay(screen);
                    // Ctrl+O quick-opens another task from within the detail (#353), stacked over it; the
                    // resolved Task Detail opens over this one, so Esc walks back through them.
                    screen.QuickOpenRequested += (_, _) => OpenQuickOpenFromScreen();
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
                Application.Invoke(() => Flash($"Could not load task detail: {ErrorText.Short(ex)}"));
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
                var comments = await _tasks.GetTaskCommentsWithRepliesAsync(taskId);
                Application.Invoke(() =>
                {
                    // Only apply if this screen is still mounted (it may sit beneath a stacked Help).
                    if (_screens.Contains(screen))
                        screen.UpdateData(detail, comments);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh task: {ErrorText.Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() => _refreshingDetail = false);
            }
        });
    }

    // ── Cross-process nudge channel — consumer (#295) ─────────────────────────

    /// <summary>
    /// Arms the nudge-channel consumer (#295): seeds the cursor to the current max marker seq so a fresh
    /// tab never replays history (edge case 1), then starts a repeating marker poll on its own short
    /// cadence (<see cref="MarkerPollInterval"/>), decoupled from the API refresh loop. A no-op store (the
    /// file-backed state store's Null channel) has an empty InstanceId, so nothing is armed — the poll only
    /// runs where a real cross-process channel exists. Runs on the UI thread during <see cref="Run"/>,
    /// before the run loop pumps.
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
    /// One marker-poll tick (#295): read the markers <b>off</b> the UI thread (a <c>changes</c> ReadAll
    /// briefly takes LiteDB's shared-mode cross-process lock), then run the pure cursor scan and dispatch
    /// <b>on</b> the UI thread (it reads <c>_all</c> / <c>_rows</c> / the open detail). A single in-flight
    /// guard keeps two scans from overlapping (the per-task reconciles a scan dispatches are guarded
    /// separately). Best-effort throughout — a read or reconcile failure is swallowed, since a nudge rides
    /// on an edit that already succeeded elsewhere.
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
                    foreach (var taskId in _markerConsumer.Advance(markers, IsNudgeTaskInView, HeldNudgeVersion))
                        ReconcileNudgedTask(taskId);
                }
                finally
                {
                    _pollingMarkers = false;
                }
            });
        });
    }

    /// <summary>A task is "in view" for the nudge scan (#295) when it's in the working set (<c>_all</c> or
    /// a visible <c>_rows</c> entry) or shown in an open Task Detail. UI-thread read.</summary>
    private bool IsNudgeTaskInView(string taskId) =>
        _all.Any(t => t.Id == taskId)
        || _rows.Any(r => r?.Id == taskId)
        || _screens.OfType<TaskDetailScreen>().Any(s => s.Task.Id == taskId);

    /// <summary>The <c>date_updated</c> (epoch ms) we currently hold for a task across <b>every</b> in-view
    /// surface (working set + any open detail), so the scan can suppress a redundant fetch (#295). Returns
    /// the <b>minimum</b> version so suppression fires only when every surface is already at or beyond the
    /// marker — a fresh working-set copy must not mask a stale open detail (which would otherwise miss its
    /// refresh until its own 30s tick). An unknown version on any surface returns null (can't prove current
    /// → don't suppress). Null too when the task isn't in view. UI-thread read.</summary>
    private long? HeldNudgeVersion(string taskId)
    {
        var versions = new List<long?>();
        var item = _all.FirstOrDefault(t => t.Id == taskId) ?? _rows.FirstOrDefault(r => r?.Id == taskId);
        if (item is not null)
            versions.Add(item.UpdatedMs);
        foreach (var screen in _screens.OfType<TaskDetailScreen>().Where(s => s.Task.Id == taskId))
            versions.Add(screen.Task.UpdatedMs);

        if (versions.Count == 0 || versions.Any(v => v is null))
            return null;
        return versions.Min();
    }

    /// <summary>
    /// Reconciles a single task the nudge scan flagged (#295) — another instance changed it. Refreshes the
    /// list row in place (a per-task fetch folded into <c>_all</c>, never a full resync) when the task is in
    /// the working set, and re-fetches any open Task Detail for it via the existing detail-refresh path.
    /// Runs on the UI thread; both reconciles are independent and best-effort.
    /// </summary>
    private void ReconcileNudgedTask(string taskId)
    {
        if (_all.Any(t => t.Id == taskId) || _rows.Any(r => r?.Id == taskId))
            RefreshNudgedRow(taskId);

        // ToList: the detail refresh doesn't mutate _screens, but snapshot it anyway for a stable iterate.
        foreach (var screen in _screens.OfType<TaskDetailScreen>().Where(s => s.Task.Id == taskId).ToList())
            RefreshDetail(screen, taskId);
    }

    /// <summary>
    /// Off-thread single-task fetch for a nudged list row (#295/#376): pull the task's <b>full</b>
    /// <see cref="TaskItem"/> (<see cref="TaskService.GetTaskItemAsync"/>) and replace the existing row
    /// <b>wholesale</b> — the full-fidelity reconcile (#376). Unlike the earlier lossy
    /// <see cref="TaskDetail"/> overlay (status + priority only), a full item carries real assignee ids,
    /// <c>ParentId</c>, <c>StatusType</c> and due date, so a cross-tab assignee / name / due-date change
    /// (not just status/priority) reflects on the row immediately rather than waiting for the next delta
    /// poll. The stale-fetch ordering is decided by the pure <see cref="NudgedRowReconciler"/>. Re-checks
    /// membership on the way back in (a background resync may have dropped the task), and swallows a fetch
    /// failure (the nudge rides on an already-succeeded edit).
    /// <para>
    /// For a task linked into multiple lists (#237), <c>GET /task/{id}</c> reports its <b>home</b> list, so
    /// the wholesale replace adopts the home <c>ListId</c>/<c>ListName</c> rather than the queried-list
    /// values the row was fetched under. In the rare case of viewing a non-home list, a group-by-list
    /// placement could momentarily shift until the next authoritative delta poll (which re-maps from the
    /// queried-list endpoint) self-heals it — an accepted, transient cost of the full-fidelity replace.
    /// </para>
    /// </summary>
    private void RefreshNudgedRow(string taskId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await _tasks.GetTaskItemAsync(taskId);
                Application.Invoke(() =>
                {
                    var existing = _all.FirstOrDefault(t => t.Id == taskId)
                                   ?? _rows.FirstOrDefault(r => r?.Id == taskId);
                    if (existing is null)
                        return; // dropped by a background resync meanwhile — nothing to update.
                    // The pure reconciler drops a stale out-of-order fetch (nudged rows are
                    // fire-and-forget and can overlap across ticks or race the delta poll) and guards the
                    // row's activity stamp from regressing to null. Non-null ⇒ apply wholesale.
                    if (NudgedRowReconciler.Reconcile(existing, fresh) is { } updated)
                        UpdateTaskRow(updated, sending: false, wholesale: true);
                });
            }
            catch
            {
                // Best-effort: a nudge-driven fetch failure must not surface — the edit already succeeded
                // in the other instance, and the next authoritative refresh reconciles regardless.
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

        // Resolve the dispatch on the UI thread before the background hand-off (#91). Capture _agent
        // locally so a concurrent F2 settings-save (which rebuilds _agent) can't swap the instance
        // mid-dispatch. The resolution + working-dir cache reconciliation + launch flows are the shared
        // DispatchCoordinator's (#345), so this dashboard host and the single-task host behave
        // identically; only the Flash / ShowScreen / guard seams differ.
        var agent = _agent;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plan = DispatchCoordinator.Plan(_config.AgentDispatch, request, detail, _config.DefaultWorkingDirectory, home);

        // Remember an explicit non-default pick for this task (#96) so the next dispatch pre-fills it;
        // reverting to the default clears the entry. Persist only when the cache actually changed.
        if (DispatchCoordinator.ReconcileCache(_config.TaskWorkingDirectories, detail.Id, plan))
            _configStore.Save(_config);

        // One-off mode (#94) runs claude -p as a background child of the app — no terminal window — with
        // a "thinking" spinner and the captured output rendered in a screen (#99). Interactive mode keeps
        // opening a real terminal below (an interactive session needs a live TTY).
        if (plan.OneOff)
        {
            DispatchCoordinator.RunBackground(
                agent, detail, comments, plan,
                mount: ShowScreen,
                clearDispatching: () => _dispatching = false);
            return;
        }

        Flash($"Launching Claude for '{detail.Name}'…");
        DispatchCoordinator.RunInteractive(
            agent, detail, comments, plan,
            report: message => { _dispatching = false; Flash(message); });
    }


    private void OpenQuickUpdates()
    {
        var task = CurrentTask();
        if (task is null)
            return;
        // Quick Updates applies to any selected task, including one that isn't my own work — a context
        // parent (#46) or a foreign subtask pulled in under my parent (#70/#179). The former ownership
        // guards that blocked those rows were lifted in #160; only the no-list data constraint remains.
        // The trailing "(not assigned to you)" row markers still convey the context.
        if (string.IsNullOrWhiteSpace(task.ListId))
        {
            Flash("This task has no list, so its statuses can't be loaded.");
            return;
        }

        // Fast path: statuses were warmed by the background prefetch — open instantly, no round-trip.
        if (_tasks.TryGetCachedStatuses(task.ListId!, out var cached))
        {
            ShowQuickUpdates(task, cached, ListTarget);
            return;
        }

        // Cold path: fetch off the UI thread with a loading indicator, then show the screen back on it.
        Flash("Loading statuses…");
        _ = Task.Run(async () =>
        {
            try
            {
                var statuses = await _tasks.GetStatusesForListAsync(task.ListId!);
                Application.Invoke(() => ShowQuickUpdates(task, statuses, ListTarget));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load statuses: {ErrorText.Short(ex)}"));
            }
        });
    }

    /// <summary>
    /// Opens Quick Updates for the task shown in a <see cref="TaskDetailScreen"/> (#159), stacked over
    /// it. Prefers the richer list <see cref="TaskItem"/> from the snapshot (fuller fidelity — assignee
    /// ids, the status <c>type</c>); when the task isn't in the snapshot (e.g. opened from the feed) it
    /// projects one from the detail. Mirrors <see cref="OpenQuickUpdates"/>'s cached-fast-path /
    /// off-thread status load; only opens while the detail is still front-most.
    /// </summary>
    private void OpenQuickUpdatesForDetail(TaskDetailScreen detailScreen)
    {
        if (!ReferenceEquals(ActiveScreen, detailScreen))
            return;

        var detail = detailScreen.Task;
        var task = _all.FirstOrDefault(t => t.Id == detail.Id) ?? TaskItemProjection.FromDetail(detail);
        // Mirror the list path (#160): Quick Updates applies to any task, including a context parent
        // (#46) or foreign subtask (#70/#179) that isn't my own work; only the no-list guard remains.
        if (string.IsNullOrWhiteSpace(task.ListId))
        {
            Flash("This task has no list, so its statuses can't be loaded.");
            return;
        }

        // Decouple the write path from `_all` (#297): if the detail's task has a row/snapshot entry the
        // list target repaints it (unchanged behaviour); otherwise — a feed-opened task (#115), and every
        // task in single-task launch mode (#296) — the commit runs against the loaded task itself, so it
        // no longer dead-ends at "no longer in the list" with no `_all` present.
        // Frozen here, before the cold-path status fetch below: if an absent task materialised in `_all`
        // during that await it would commit against the single-task target and not repaint the now-present
        // row — benign (the write still lands; the next background refresh reconciles the row).
        var target = QuickUpdatesTaskById(task.Id) is not null
            ? ListTarget
            : new SingleTaskUpdateTarget(task);

        if (_tasks.TryGetCachedStatuses(task.ListId!, out var cached))
        {
            ShowQuickUpdates(task, cached, target, detailScreen);
            return;
        }

        Flash("Loading statuses…");
        _ = Task.Run(async () =>
        {
            try
            {
                var statuses = await _tasks.GetStatusesForListAsync(task.ListId!);
                Application.Invoke(() => ShowQuickUpdates(task, statuses, target, detailScreen));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not load statuses: {ErrorText.Short(ex)}"));
            }
        });
    }

    /// <summary>Shows the Quick Updates screen for a task and wires its Status/Priority commits. Must
    /// run on the UI thread. Status and Priority apply on Enter (#157) — the screen stays open (Esc
    /// exits); the Assignees pane's apply lands in #158.
    /// <para>
    /// <paramref name="detailOrigin"/> is the <see cref="TaskDetailScreen"/> Quick Updates was launched
    /// from (#159), or null for the list origin. It governs the stacking guard (the list opens over
    /// nothing; a detail launch stacks over exactly that screen) and receives an optimistic reflection of
    /// each committed status/priority so the popped-back detail shows the change.
    /// </para>
    /// </summary>
    private void ShowQuickUpdates(TaskItem task, IReadOnlyList<StatusOption> statuses,
        IQuickUpdateTarget target, TaskDetailScreen? detailOrigin = null)
    {
        if (statuses.Count == 0)
        {
            Flash("No statuses available for this list.");
            return;
        }

        // One screen is focused at a time (#3/#38): the list origin opens over nothing; the detail origin
        // stacks over exactly the screen that requested it. A stale off-thread status load whose origin is
        // no longer front-most is dropped here.
        if (!ReferenceEquals(ActiveScreen, detailOrigin))
            return;

        // #242 (temporarily disabled — see QuickUpdatesScreen's summary): the List pane's seed. Changing a
        // task's list can strand fields/statuses that don't exist on the target list; ClickUp's PWA has a
        // guided migration for those cases and we don't yet. Re-enable this (and the ctor args + enrich
        // call below, and ApplyListAsync/EnrichListMemberships/HomeListOf) once that migration is designed.
        // var homeList = new NamedEntity(task.ListId!, task.ListName ?? task.ListId!);
        // var additionalLists = (IReadOnlyList<NamedEntity>)(detailOrigin?.Task.Lists ?? [])
        //     .Where(l => !string.Equals(l.Id, task.ListId, StringComparison.Ordinal))
        //     .ToList();

        var screen = new QuickUpdatesScreen(
            task.Name, statuses, task.StatusName, task.PriorityLevel, task.Assignees,
            // Assignees pane (#158): candidate pool from the frequency cache (#155); add/remove apply
            // immediately via ApplyAssigneeAsync (the selector owns the optimistic update + revert).
            _assignees.Match, _assignees.TopMostFrequent,
            (kind, person, ct) => ApplyAssigneeAsync(task.Id, kind, person, target, ct));
        // #242 (disabled): the List pane's ctor args followed the assignee ones —
        //     homeList, additionalLists,
        //     _lists.Match, _lists.TopMostFrequent,
        //     (kind, list, ct) => ApplyListAsync(task.Id, kind, list, ct));
        // Status/Priority apply on Enter and reconcile the screen's ✓ from the server-confirmed value.
        // The commit resolves against and writes back to `target` (#297) — the list snapshot in list mode,
        // the loaded task with no list in single-task mode — decoupling the write path from `_all`.
        // A detail-origin launch (#159) also reflects each committed value onto the detail so the
        // popped-back detail shows it; `statuses` supplies the colour for a reflected status.
        screen.StatusCommitted += status => ApplyStatus(task.Id, status, screen, target, detailOrigin, statuses);
        screen.PriorityCommitted += level => ApplyPriority(task.Id, level, screen, target, detailOrigin);
        ShowScreen(screen, static () => { });

        // #242 (disabled): a list-origin launch enriched the List pane's additional locations here —
        // if (detailOrigin is null)
        //     EnrichListMemberships(task.Id, screen);
    }

    // #242 (temporarily disabled — see QuickUpdatesScreen's summary): the List-pane host helpers
    // (EnrichListMemberships / ApplyListAsync / HomeListOf) are commented out with the pane. The reusable
    // pieces they lean on — TaskService.Add/RemoveTaskFromListAsync (#237) and ListSelectorModel.Membership
    // — stay in place. Re-enable these once the field/status migration is designed.
    //
    // /// <summary>
    // /// Background-fetches a task's full membership and merges its additional "Tasks in Multiple Lists"
    // /// locations into an open Quick Updates List pane (#242). Only runs for a list-origin launch, where
    // /// the snapshot TaskItem carries only the home list. A failed/empty fetch leaves the pane seeded with
    // /// the home list; the enrich no-ops if the screen has moved on or the user already began editing.
    // /// </summary>
    // private void EnrichListMemberships(string taskId, QuickUpdatesScreen screen)
    // {
    //     _ = Task.Run(async () =>
    //     {
    //         try
    //         {
    //             var detail = await _tasks.GetTaskDetailAsync(taskId).ConfigureAwait(false);
    //             if (detail.Lists.Count == 0)
    //                 return;
    //             Application.Invoke(() =>
    //             {
    //                 if (ReferenceEquals(ActiveScreen, screen))
    //                     screen.SeedListMemberships(detail.Lists);
    //             });
    //         }
    //         catch
    //         {
    //             // Best-effort enrich: on failure the pane keeps the home-list seed and any user
    //             // add/remove still reconciles from the server truth — not worth a flash.
    //         }
    //     });
    // }
    //
    // /// <summary>
    // /// Performs a Quick Updates List-pane add/remove (#242): writes the membership change to ClickUp off
    // /// the UI thread over the #237 facade and returns the server-confirmed membership set so the embedded
    // /// ListSelectorView can reconcile. The membership endpoints echo no body, so the confirmed set is read
    // /// back from a fresh GetTaskDetailAsync — the home list (ListId/ListName) plus the additional locations
    // /// (Lists). A disabled "Tasks in Multiple Lists" ClickApp throws a ClickUpApiException that the selector
    // /// catches to revert + flash (non-fatal). The main-list row shows only the home list, so — unlike
    // /// ApplyAssigneeAsync — there is no row to reconcile.
    // /// </summary>
    // private async Task<IReadOnlyList<NamedEntity>> ApplyListAsync(
    //     string taskId, ToggleKind kind, NamedEntity list, CancellationToken ct)
    // {
    //     // Deliberately do NOT thread the selector's token into the write: it's cancelled when the screen
    //     // is disposed (Esc), so forwarding it would drop an add/remove the user already saw applied. Same
    //     // rationale as ApplyAssigneeAsync / ApplyStatus.
    //     _ = ct;
    //     if (kind == ToggleKind.Added)
    //         await _tasks.AddTaskToListAsync(taskId, list.Id).ConfigureAwait(false);
    //     else
    //         await _tasks.RemoveTaskFromListAsync(taskId, list.Id).ConfigureAwait(false);
    //     var detail = await _tasks.GetTaskDetailAsync(taskId).ConfigureAwait(false);
    //     return ListSelectorModel.Membership(HomeListOf(detail), detail.Lists);
    // }
    //
    // /// <summary>The home list of a task detail as a NamedEntity, or null when it has no list. Falls the
    // /// display name back to the id if the detail carries none (the marker still shows).</summary>
    // private static NamedEntity? HomeListOf(TaskDetail detail)
    //     => string.IsNullOrWhiteSpace(detail.ListId)
    //         ? null
    //         : new NamedEntity(detail.ListId!, string.IsNullOrWhiteSpace(detail.ListName) ? detail.ListId! : detail.ListName!);

    /// <summary>
    /// Performs a Quick Updates Assignees-pane add/remove (#158): writes the change to ClickUp off the
    /// UI thread and returns the <b>server-confirmed</b> assignee set so the embedded
    /// <see cref="AssigneeSelectorView"/> can reconcile its own pane display. On success it also
    /// reconciles the task's row in the canonical snapshot + visible list (mirroring
    /// <see cref="ApplyStatus"/>) so the main list — hidden behind the modal — and its assignee badge
    /// (#F6) reflect the change once the screen is dismissed. The selector owns the optimistic pane
    /// update and the revert-on-failure; a throw here propagates to it. The host row only ever moves to
    /// a confirmed set, so a failed write leaves it untouched (nothing to revert host-side); overlapping
    /// same-task writes settle on the last-returning confirmed set and self-heal on the next refresh.
    /// </summary>
    private async Task<IReadOnlyList<TaskAssignee>> ApplyAssigneeAsync(
        string taskId, ToggleKind kind, TaskAssignee person, IQuickUpdateTarget target, CancellationToken ct)
    {
        // Deliberately do NOT thread the selector's cancellation token into the write: that token is
        // cancelled when the screen is disposed (Esc), so forwarding it would cancel an in-flight
        // add/remove the user has already seen applied — silently dropping it until the next refresh.
        // Status/Priority commits (ApplyStatus/ApplyPriority) issue their writes untokened for the same
        // reason; assignees match that. The token still guards the *view's* own reconcile/revert (it
        // re-checks IsCancellationRequested), and our row reconcile below is guarded by
        // QuickUpdatesTaskById.
        _ = ct;
        var confirmed = kind == ToggleKind.Added
            ? await _tasks.AddAssigneeAsync(taskId, person.Id).ConfigureAwait(false)
            : await _tasks.RemoveAssigneeAsync(taskId, person.Id).ConfigureAwait(false);
        Application.Invoke(() =>
        {
            // Resolve/apply through the target (#297): the list target reconciles the row in place — for a
            // foreign subtask / context parent too (#160), not just tasks in _all — while a single-task
            // target updates the loaded task with no list present.
            if (target.Resolve(taskId) is { } t)
                target.Apply(t with { Assignees = confirmed }, sending: false);
        });
        return confirmed;
    }

    /// <summary>The current record for <paramref name="taskId"/> in the canonical snapshot, or null if
    /// it has fallen out of the working set (e.g. a background refresh dropped it).</summary>
    private TaskItem? TaskById(string taskId) => _all.FirstOrDefault(t => t.Id == taskId);

    /// <summary>The current record for a Quick Updates commit: the canonical snapshot, then the visible
    /// rows so a foreign subtask (#70/#179) or context parent (#46) — which live in <see cref="_rows"/>
    /// but not <see cref="_all"/> — resolves too, letting Quick Updates apply to a task that isn't the
    /// user's own work (#160). <see cref="UpdateTaskRow"/> keeps both in sync, so consecutive edits
    /// compose regardless of which side holds the row.</summary>
    private TaskItem? QuickUpdatesTaskById(string taskId) => TaskService.FindById(_all, _rows, taskId);

    // The list-backed Quick Updates write target (#297): resolves against the canonical snapshot + visible
    // rows and repaints the on-screen row via UpdateTaskRow — i.e. the unchanged list-mode behaviour. It
    // holds no state of its own (it delegates to the host's live snapshot), so one shared instance serves
    // every list-origin launch; single-task launches get a fresh SingleTaskUpdateTarget instead.
    private sealed class ListUpdateTarget(TodoApp app) : IQuickUpdateTarget
    {
        public TaskItem? Resolve(string taskId) => app.QuickUpdatesTaskById(taskId);
        public void Apply(TaskItem updated, bool sending) => app.UpdateTaskRow(updated, sending);
    }

    private IQuickUpdateTarget? _listTarget;
    private IQuickUpdateTarget ListTarget => _listTarget ??= new ListUpdateTarget(this);

    // Monotonic per-field commit counters. The Quick Updates screen stays open, so the user can fire a
    // second write for the same field before the first returns; each commit stamps its generation and a
    // late continuation whose generation is no longer current is dropped, so the row + ✓ settle on the
    // latest commit regardless of the order the responses arrive in.
    private int _statusCommitGen;
    private int _priorityCommitGen;

    /// <summary>
    /// Applies a Quick Updates status commit for <paramref name="taskId"/>: move the ✓ optimistically,
    /// optimistic row update, then an off-thread write, confirming with the server's returned status on
    /// success and reverting the one row on failure. The task is looked up fresh from the snapshot so
    /// consecutive edits compose; a superseded (out-of-order) continuation is dropped; the screen's ✓ is
    /// reconciled to the confirmed/reverted value while it's still mounted.
    /// <para>
    /// When launched from the Task Detail view (#159), <paramref name="detailOrigin"/> is that screen and
    /// <paramref name="statuses"/> its list's status options; the committed/confirmed/reverted status is
    /// reflected onto the detail (with the matching colour) so it stays in sync with the list row.
    /// </para>
    /// </summary>
    private void ApplyStatus(string taskId, string status, QuickUpdatesScreen screen,
        IQuickUpdateTarget target, TaskDetailScreen? detailOrigin = null, IReadOnlyList<StatusOption>? statuses = null)
    {
        var task = target.Resolve(taskId);
        if (task is null)
        {
            // The screen hasn't moved its ✓ yet (it defers that to us), so just report and bail.
            Flash("This task is no longer in the list — status unchanged.");
            return;
        }
        var gen = ++_statusCommitGen;
        var previousStatus = task.StatusName;
        var previousColor = task.StatusColor;

        ReconcileScreenStatus(screen, status); // optimistic ✓
        ReflectDetailStatus(detailOrigin, status, ColorForStatus(statuses, status));
        target.Apply(task with { StatusName = status }, sending: true);
        Flash($"Setting '{status}'…");

        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await _tasks.SetStatusAsync(taskId, status);
                Application.Invoke(() =>
                {
                    if (gen != _statusCommitGen)
                        return; // a newer status commit superseded this one
                    var final = confirmed ?? status;
                    if (target.Resolve(taskId) is { } t)
                        target.Apply(t with { StatusName = final }, sending: false);
                    ReconcileScreenStatus(screen, final);
                    ReflectDetailStatus(detailOrigin, final, ColorForStatus(statuses, final));
                    Flash($"Set status to '{final}'.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (gen != _statusCommitGen)
                        return;
                    if (target.Resolve(taskId) is { } t)
                        target.Apply(t with { StatusName = previousStatus }, sending: false); // revert
                    ReconcileScreenStatus(screen, previousStatus);
                    ReflectDetailStatus(detailOrigin, previousStatus, previousColor);
                    Flash($"Could not set status: {ErrorText.Short(ex)}");
                });
            }
        });
    }

    /// <summary>
    /// Applies a Quick Updates priority commit for <paramref name="taskId"/> (<paramref name="level"/>
    /// null = clear), mirroring <see cref="ApplyStatus"/>: optimistic ✓ + row update, off-thread write,
    /// confirm-from-server on success, revert-the-row on failure, drop a superseded continuation.
    /// </summary>
    private void ApplyPriority(string taskId, int? level, QuickUpdatesScreen screen,
        IQuickUpdateTarget target, TaskDetailScreen? detailOrigin = null)
    {
        var task = target.Resolve(taskId);
        if (task is null)
        {
            Flash("This task is no longer in the list — priority unchanged.");
            return;
        }
        var gen = ++_priorityCommitGen;
        var previousLevel = task.PriorityLevel;

        ReconcileScreenPriority(screen, level); // optimistic ✓
        ReflectDetailPriority(detailOrigin, level);
        target.Apply(WithPriority(task, level), sending: true);
        Flash($"Setting priority '{ClickUpPriority.NameFromLevel(level) ?? "none"}'…");

        _ = Task.Run(async () =>
        {
            try
            {
                var confirmed = await _tasks.SetPriorityAsync(taskId, level);
                Application.Invoke(() =>
                {
                    if (gen != _priorityCommitGen)
                        return; // a newer priority commit superseded this one
                    if (target.Resolve(taskId) is { } t)
                        target.Apply(WithPriority(t, confirmed), sending: false);
                    ReconcileScreenPriority(screen, confirmed);
                    ReflectDetailPriority(detailOrigin, confirmed);
                    Flash($"Set priority to '{ClickUpPriority.NameFromLevel(confirmed) ?? "none"}'.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (gen != _priorityCommitGen)
                        return;
                    if (target.Resolve(taskId) is { } t)
                        target.Apply(WithPriority(t, previousLevel), sending: false); // revert
                    ReconcileScreenPriority(screen, previousLevel);
                    ReflectDetailPriority(detailOrigin, previousLevel);
                    Flash($"Could not set priority: {ErrorText.Short(ex)}");
                });
            }
        });
    }

    /// <summary>A copy of <paramref name="task"/> carrying priority <paramref name="level"/> with the
    /// canonical name + colour for that level (null clears all three).</summary>
    private static TaskItem WithPriority(TaskItem task, int? level) => task with
    {
        PriorityLevel = level,
        PriorityName = ClickUpPriority.NameFromLevel(level),
        PriorityColor = ClickUpPriority.ColorFromLevel(level),
    };

    // The async write can resolve after the user has Esc'd or stacked another screen; only touch the
    // screen's ✓ while it's still mounted (a disposed/detached screen's list would throw or be moot).
    private void ReconcileScreenStatus(QuickUpdatesScreen screen, string? status)
    {
        if (_screens.Contains(screen))
            screen.SetEffectiveStatus(status);
    }

    private void ReconcileScreenPriority(QuickUpdatesScreen screen, int? level)
    {
        if (_screens.Contains(screen))
            screen.SetEffectivePriority(level);
    }

    /// <summary>The colour of the status option named <paramref name="status"/> in
    /// <paramref name="statuses"/> (case-insensitive), or null when unknown — used to colour the status
    /// reflected onto the detail view (#159).</summary>
    private static string? ColorForStatus(IReadOnlyList<StatusOption>? statuses, string? status)
        => status is null
            ? null
            : statuses?.FirstOrDefault(s => string.Equals(s.Name, status, StringComparison.OrdinalIgnoreCase))?.Color;

    // Reflect a committed status/priority onto the Task Detail view Quick Updates was launched over (#159),
    // guarded on that screen still being mounted, so the popped-back detail shows the change. A null
    // detailOrigin (the list origin) is a no-op. Priority uses the canonical name/colour for the level,
    // matching the list row's WithPriority.
    private void ReflectDetailStatus(TaskDetailScreen? detailOrigin, string? status, string? color)
    {
        if (detailOrigin is not null && _screens.Contains(detailOrigin))
            detailOrigin.ApplyOptimisticStatus(status, color);
    }

    private void ReflectDetailPriority(TaskDetailScreen? detailOrigin, int? level)
    {
        if (detailOrigin is not null && _screens.Contains(detailOrigin))
            detailOrigin.ApplyOptimisticPriority(ClickUpPriority.NameFromLevel(level), ClickUpPriority.ColorFromLevel(level));
    }

    /// <summary>
    /// Updates a single task's row in place — both the canonical snapshot (<see cref="_all"/>) and
    /// the visible ListView row — without rebuilding the list (no SetSource, so the cursor and
    /// scroll position stay put). Keeping <see cref="_all"/> and <see cref="_signature"/> in sync
    /// means the next periodic background refresh reconciles silently when the server agrees.
    /// </summary>
    private void UpdateTaskRow(TaskItem updated, bool sending, bool wholesale = false)
    {
        // Two fold modes into the canonical snapshot:
        //  • Per-field sync (#158, the default) — the `updated` record always carries the current value
        //    for the fields a given caller didn't touch, so folding status/priority/assignees never
        //    clobbers (a status/priority commit re-applies the task's existing assignees, a no-op, and an
        //    assignee change re-applies its status/priority). The same pure reconcile the single-task
        //    target uses (#297).
        //  • Wholesale (#376, the cross-tab nudge path) — `updated` is an authoritative full TaskItem
        //    freshly fetched for the task, so it replaces the snapshot row outright, carrying real
        //    assignee ids / ParentId / due date a per-field fold would leave stale.
        _all = wholesale
            ? TaskService.ReplaceTaskItem(_all, updated)
            : TaskService.ApplyFieldChanges(_all, updated);
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
        // Reproduce the row's not-mine / context classification (#264): without these flags the row
        // render drops the trailing "(not assigned to you)" (#70/#179) / "(parent — not assigned to
        // you)" (#46) / "(unassigned)" (#179) marker until the next full Render, mirroring how the
        // render path (Render → AddTask → TaskRowRenderer.Render) sets them per row.
        var (isContextParent, isForeignSubtask, isUnassignedSubtask) =
            ClassifyRowMarker(updated, _contextParents, VisibleForeignSubtasks());
        var (text, badges, markerStart, markerLength) = TaskRowRenderer.Render(
            updated, _config.BadgeDisplay, _tasks.UserId, index < _depths.Count ? _depths[index] : 0,
            isContextParent, groupedBy: groupedBy, marker: marker,
            isForeignSubtask: isForeignSubtask, isUnassignedSubtask: isUnassignedSubtask);
        _badges[index] = badges;
        if (index < _markerSpans.Count)
            _markerSpans[index] = (markerStart, markerLength);
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

    /// <summary>
    /// Paints the persisted working set (#122) before the first network load, so a warm cache shows
    /// the task list on the first frame. A miss (nothing cached, or the cache belongs to a different
    /// workspace/list/assignee context) leaves the "Loading…" state untouched for the live load to
    /// replace. Seeds <see cref="_signature"/> from the cached set so an identical live load hits the
    /// OnTasksLoaded fast-path with no re-render. That no-op holds in the default (subtasks-off) view;
    /// with the F4 subtasks view persisted on, the first live load resolves context parents / foreign
    /// subtasks that <see cref="CurrentSignature"/> folds in (empty here), so it re-renders once — but
    /// the cursor is preserved by task id, so there's no selection loss either way. Runs on the UI
    /// thread during <see cref="Run"/>, before the run loop starts.
    /// </summary>
    private void TryPaintCachedTasks()
    {
        var cached = _taskCache.LoadSnapshot(_config);
        if (cached is not { Items.Count: > 0 })
            return;

        _all = cached.Items;
        // Mark how stale the painted set is (#124) so the instant paint reads honestly as cached, not
        // freshly loaded; the live refresh replaces it (and this line) moments later.
        var age = RelativeTime.Format(DateTimeOffset.UtcNow - cached.CapturedAt);
        _footer.Status = $"Showing cached tasks from {age} · {cached.Items.Count} task(s) · refreshing…";
        _signature = CurrentSignature(cached.Items);
        Render(keepTaskId: null);
    }

    private void OnTasksLoaded(IReadOnlyList<TaskItem> tasks)
    {
        _all = tasks;

        // Tally this working set into the assignee-frequency pool (#155) and, once, kick the deferred
        // workspace-members top-up so the pool is full even when few people ride along on the tasks.
        // Both are non-blocking: the tally is a cheap synchronous dictionary update, and the top-up
        // yields to the network off the UI thread (best-effort; failures are swallowed).
        _assignees.RecordFromTasks(tasks);
        if (!_assigneeTopUpKicked)
        {
            _assigneeTopUpKicked = true;
            _ = _assignees.TopUpAsync(AssigneeCandidateTarget);
        }

        // Tally this working set's home lists into the list-frequency pool (#238) — the free, primary
        // candidate tier. Cheap, synchronous, and idempotent (distinct-task counting), so it's safe on
        // every poll; the walk (#236) seeds the long tail separately from RunWorkspaceListWalkStepAsync.
        _lists.RecordFromTasks(tasks);

        _footer.Status = $"Updated {DateTime.Now:HH:mm:ss} · {tasks.Count} task(s) · refresh every {_config.RefreshSeconds}s";
        // Surface an adaptive-fetch cap (#87) on the persisted status line — a Flash here would be
        // repainted away by this same success path, so it's folded into the line the path writes.
        if (_foreignSubtasksTruncated)
            _footer.Status += " · some subtasks omitted";

        // Warm the status cache for the lists currently on screen (best-effort, off the UI thread), so
        // pressing Space opens the picker from cache instead of paying a round-trip (#10).
        var visibleLists = tasks.Where(t => !string.IsNullOrWhiteSpace(t.ListId)).Select(t => t.ListId!);
        _ = _tasks.PrefetchStatusesAsync(visibleLists);

        // Consume any pending post-create selection (#213) up front — cleared here regardless of the
        // fast-path below, so it's honoured exactly once and can never leak onto a later unrelated
        // refresh. A just-created task that lands in this set changes the signature (BuildSignature folds
        // in every task id), so the fast-path below can't swallow it: when the task is present we always
        // reach Render; when it isn't, the cursor is correctly left untouched.
        var pendingSelect = _pendingSelectId;
        _pendingSelectId = null;

        // Rebuilding the ListView (SetSource) forces a full reset + redraw. Skip it when the visible
        // task set is unchanged and just update the (cheap) status line.
        var signature = CurrentSignature(tasks);
        if (signature == _signature)
        {
            _footer.CommitStatus();
            return;
        }
        _signature = signature;
        // Prefer landing on the just-created task when present; otherwise keep the cursor on the current
        // task (Render falls back to the first row when the id isn't found).
        Render(keepTaskId: pendingSelect ?? CurrentTask()?.Id);

        // Persist the freshly-rendered working set for the next launch's instant first paint (#122).
        // Only on a real change — the signature fast-path above already returned for a no-op poll, so
        // the cache isn't rewritten every interval. The bounded payload keeps this off-critical-path;
        // it rides the UI thread like the config save. The optimistic status path (UpdateTaskRow) is
        // intentionally not cached here: the next authoritative load saves the confirmed set, and
        // persisting an as-yet-unconfirmed value could outlive a server rejection.
        _taskCache.Save(_config, tasks);
    }

    /// <summary>
    /// The rendered fingerprint including the subtasks-view state, so toggling F4 or resolving new
    /// context parents is treated as a change (not a no-op refresh) even when the task set is identical.
    /// </summary>
    private string CurrentSignature(IReadOnlyList<TaskItem> tasks)
    {
        var sb = new System.Text.StringBuilder(BuildSignature(tasks));
        // Fold in the F12 completed view (not just on/off) so cycling between states — a pure re-render
        // over the same fetched set — is treated as a render change, not a no-op refresh, even when the
        // underlying task set is byte-identical (#178/#191).
        sb.Append("#done=").Append(_config.View.Completed);
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
        if (view.Completed.TitleFlag() is { } completedFlag)
            flags.Add(completedFlag);
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
        // Pass the F12 completed view (#178/#191) so FocusSectionLayout gates completed descendants
        // (in-snapshot and foreign alike) in one place — a completed subtask never nests under a pinned
        // ancestor unless the view shows it, matching TaskView.Apply's gate on the to-do section;
        // explicit pins stay visible regardless.
        var focus = FocusSectionLayout.Build(_all, pinnedIds, nest, view.SortField, view.SortDirection, _expanded, foreignList, completed: view.Completed);
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
        _markerSpans = new List<(int, int)>();

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

        _footer.CommitStatus();
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
        _markerSpans.Add((-1, 0));
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
        _markerSpans.Add((-1, 0));
    }

    private void AddTask(TaskItem task, int depth = 0, bool isContextParent = false, TaskField? groupedBy = null, FoldState fold = FoldState.None, bool isForeignSubtask = false, bool isUnassignedSubtask = false)
    {
        var (text, badges, markerStart, markerLength) = TaskRowRenderer.Render(task, _config.BadgeDisplay, _tasks.UserId, depth, isContextParent, groupedBy, FoldMarker(fold, _config.View.ShowSubtasks), isForeignSubtask, isUnassignedSubtask);
        _rows.Add(task);
        _kinds.Add(RowKind.Task);
        _display.Add(text);
        _badges.Add(badges);
        _headerAttrs.Add(null);
        _depths.Add(depth);
        _folds.Add(fold);
        _markerSpans.Add((markerStart, markerLength));
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

    private void Flash(string message) => _footer.Flash(message);
}
