using System.Collections.ObjectModel;
using System.Drawing;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window screen showing a task's detail (issue #17): a header (title, tags, assignees) above
/// a tabbed, scrollable pane — Stream / Description / Comments / Other attributes. Built on the shared
/// screen seam (#38) — swapped into the dashboard's single toplevel, not a nested modal <c>Dialog</c>.
/// <para>
/// Esc returns to the list; Ctrl+B requests opening the task in the browser (the host reads
/// <see cref="OpenBrowserRequested"/> in its close handler and owns the launch). Tab cycles tabs;
/// ↑/↓/PgUp/PgDn scroll the focused pane; F1 opens Help. The Stream tab (#106) is the out-of-the-box
/// default (the opening tab is configurable via #108); it opens
/// auto-scrolled to the newest (or oldest) entry per the <see cref="StreamAutoScroll"/> preference
/// (#107). Ctrl+PgUp/Ctrl+PgDn set a single activity order — oldest-first / newest-first — that governs
/// both the Stream and Comments tabs, re-rendering both in place so the order is consistent regardless
/// of which tab is currently shown. The initial tab, default sort, and auto-scroll position come from the persisted
/// <see cref="DetailViewSettings"/> (#108). Tab bodies come
/// from the unit-tested
/// <see cref="TaskDetailFormatter"/>, so this class is only the (CI-untestable) Terminal.Gui glue.
/// </para>
/// <para>
/// <b>Ctrl+A</b> opens the inline Dispatch pane (issue #93, D1 of the #90 epic; superseding the bare
/// <c>A</c> prompt of #26): a bottom-anchored <c>FrameView</c> hosting the prompt, the working-dir
/// control (#95 — an editable field plus a file-tree browser rooted at the base working dir #92), and
/// placeholder controls for the options that land in #94/#97. Tab/Shift+Tab cycle its controls,
/// PgUp/PgDn keep scrolling the tab above, Enter submits (raising <see cref="AgentDispatchRequested"/>
/// with a <see cref="DispatchRequest"/>) and Esc cancels — all routed through the pure
/// <see cref="DispatchPaneModel"/>. The pane is a transient child view — not a nested run-loop or a
/// second screen — so it stays within the single already-open screen; the dashboard's
/// single-<c>ListView</c> model (#3) is untouched.
/// </para>
/// <para>
/// <b>Working-dir browser (#95):</b> a single-column <c>ListView</c> under the field, listing
/// <c>..</c> then the current directory's subdirectories (via the unit-tested
/// <see cref="DirectoryBrowserModel"/>). ↑/↓ move; → descends into the highlighted dir; ← goes up.
/// <b>The highlight drives the path field:</b> moving the cursor mirrors the highlighted directory
/// straight into the field (so the highlighted dir is the one that dispatches — no separate select
/// step), and <b>Enter</b> just confirms it and advances focus. On <c>..</c>, Enter goes up so it
/// never submits from the browser. A blank field falls through to the configured-default /
/// task-derived working dir (#98).
/// </para>
/// </summary>
public sealed class TaskDetailScreen : Screen
{
    /// <summary>How often the detail view silently re-fetches its task + comments (#114 follow-up).
    /// F5 / Ctrl+R force one between ticks.</summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly Tabs _tabs;
    // The view inserted as each tab: Description/Comments are a plain TextView; Other is a container
    // (a coloured header view above its scrollable body), so this is typed as View, not TextView.
    private readonly View[] _tabContents;
    // The focusable, scrollable view for each tab — the TextView that ↑/↓/PgUp/PgDn scroll. For Other
    // that's the custom-fields body (its coloured header is a non-focusable overlay), so it differs
    // from _tabContents there; for the other tabs the two are the same TextView.
    private readonly View[] _scrollTargets;
    private readonly FrameView _promptBox;
    private readonly TextField _promptField;
    // The Dispatch pane's controls, in focus (Tab) order. The prompt, the one-off/interactive toggle
    // (#94), the working-dir control (#95), and the post-to-Comments toggle (#97) all feed a dispatch.
    private readonly View[] _dispatchControls;
    private readonly CheckBox _oneOffToggle;
    private readonly TextField _workingDirField;
    private readonly ListView _dirBrowser;
    private readonly DirectoryBrowserModel _browser;
    private readonly CheckBox _postToCommentsToggle;
    // The per-dispatch launch-location toggle (#275): checked ⇒ open this session in a new tab of the
    // current terminal (where supported), unchecked ⇒ a new window. Seeded from the persisted default
    // and read on submit. Greyed out in one-off mode (a -p run has no terminal); see UpdateLaunchLocationEnabled.
    private readonly CheckBox _launchLocationToggle;
    // Guards the working-dir field against the browser's selection-follows-cursor sync while the pane
    // is being (re)opened: pre-fill writes the per-task cached dir (#96) into the field, then resetting
    // the browser fires ValueChanged, which would otherwise immediately clobber that pre-fill with the
    // browser root. Set only around the open block in ShowPrompt; genuine user navigation runs unguarded.
    private bool _suppressWorkingDirSync;
    // Supplies the per-task cached working directory (#96) the pane's dir field is pre-filled with,
    // read live on each open so a same-session dispatch that updated the cache is reflected on reopen;
    // blank/null ⇒ start blank (⇒ configured default / task-derived dir #98).
    private readonly Func<string>? _workingDirectoryPreFill;

    // The comment composer (#216): a bottom-anchored FrameView hosting a multi-line editor plus Post/
    // Cancel buttons, hidden until Ctrl+N. Like the Dispatch pane it's a transient child view within
    // the single already-open screen (not a nested run-loop / second screen), so the dashboard's
    // single-ListView model (#3) is untouched. The host owns the ClickUp write via _postCommentAsync;
    // the screen owns the optimistic append + reconcile/revert over its own _comments list.
    private readonly FrameView _commentBox;
    private readonly TextView _commentEditor;
    // The composer's focusable controls in Tab order: the editor, then Post, then Cancel.
    private readonly View[] _commentControls;
    private readonly Func<string, CancellationToken, Task<CommentItem>>? _postCommentAsync;
    // Monotonic sentinel-id sequence for optimistic (provisional) comments, so a reconcile/revert
    // finds the right one even with overlapping posts. UI-thread only.
    private int _pendingCommentSeq;
    // The composer's ideal height: the multi-line editor rows + the Post/Cancel button row + the
    // top/bottom frame border. Clamped on show so it degrades gracefully on a short terminal.
    private const int CommentEditorRows = 5;
    private const int CommentComposerPreferredHeight = CommentEditorRows + 1 + 2;

    // Reply into a thread (#330): posts a plain-text reply to a comment's thread over the #327 facade.
    // Null disables reply (Ctrl+T inert), so a non-interactive host stays unaffected. The composer
    // (_commentBox) is reused in reply mode; _replyToCommentId is the target parent while it's open
    // (null ⇒ the plain top-level path). The reply-target picker is a transient FrameView + ListView
    // overlay, shown-then-dismissed like the composer, so the single-ListView model (#3) is untouched.
    private readonly Func<string, string, CancellationToken, Task<CommentItem>>? _postReplyAsync;
    private readonly FrameView _replyPickerBox;
    private readonly ListView _replyPicker;
    private IReadOnlyList<CommentReplyModel.ReplyTarget> _replyTargets = [];
    private string? _replyToCommentId;
    // The picker's ideal height: a few target rows + the top/bottom frame border. Clamped on show.
    private const int ReplyPickerRows = 6;
    private const int ReplyPickerPreferredHeight = ReplyPickerRows + 2;

    // The description editor (#217): a bottom-anchored FrameView hosting a multi-line editor above a
    // Save/Cancel button row (with a hidden inline confirm row between them), hidden until Ctrl+E. Like
    // the comment composer it's a transient child view within the single already-open screen (not a
    // nested run-loop / second screen), so the dashboard's single-ListView model (#3) is untouched. The
    // host owns the ClickUp write via _setDescriptionAsync; the screen owns the seed, the dirty-check,
    // and the in-place reflection of the server-confirmed text.
    private readonly FrameView _descriptionBox;
    private readonly TextView _descriptionEditor;
    // The editor's focusable controls in Tab order: the editor, then Save, then Cancel.
    private readonly View[] _descriptionControls;
    // Inline unsaved-changes confirm row (shown only while an Esc-with-edits discard is pending),
    // mirroring PromptTemplateEditorScreen's reset Y/N — no nested modal (#38).
    private readonly Label _descriptionConfirm;
    private readonly Func<string, CancellationToken, Task<string?>>? _setDescriptionAsync;
    // True between an Esc-on-dirty and the Y/N answer; the next key confirms (discard+close) or dismisses.
    private bool _descriptionPendingDiscard;
    // Guards against a second Save (Ctrl+Enter mash / Save re-press) while a write is in flight.
    private bool _savingDescription;
    // The editor's ideal height: the multi-line editor rows + the confirm row + the Save/Cancel button
    // row + the top/bottom frame border. Clamped on show so it degrades gracefully on a short terminal.
    private const int DescriptionEditorRows = 12;
    private const int DescriptionEditorPreferredHeight = DescriptionEditorRows + 1 + 1 + 2;

    // The Dispatch pane's working-dir layout (#95): rows above the browser (prompt, one-off, dir
    // field, key hint), the browser's own rows, and rows below (post-to-Comments + the #275
    // launch-location toggle). Used to size the pane via DispatchPaneModel.PreferredHeightWithBrowser
    // and to place the ListView.
    private const int DispatchRowsAboveBrowser = 4;
    private const int DispatchBrowserRows = 5;
    private const int DispatchRowsBelowBrowser = 2;

    // The coloured title header (#162) and the three text-based tab bodies, kept as fields so a refresh
    // (#114 follow-up) can re-render each in place — only when its content actually changed, so a poll
    // that finds nothing new never disturbs the cursor or scroll.
    private readonly DetailAttributesView _headerView;
    private readonly DetailPaneView _descriptionPane;
    private readonly DetailPaneView _commentsPane;
    private readonly DetailOtherTabView _otherTab;
    // Last-rendered content fingerprints, so a refresh re-renders a pane only when its content moved.
    // The header + Other tab use structured lines (fingerprinted via OtherTabSignature); the text panes
    // track their last body string directly (a DetailPaneView loads cells, so its Text getter isn't a
    // reliable round-trip to compare against).
    private string _headerSignature;
    private string _otherSignature;
    private string _streamText;
    private string _descriptionText;
    private string _commentsText;

    // The Stream tab (#106) and the data it re-renders from on an activity-order toggle. The order is one
    // shared setting that governs both the Stream and Comments tabs (Ctrl+PgUp/PgDn re-renders both),
    // regardless of which is showing; the initial direction is the persisted default (#108) and the
    // on-screen toggle overrides it for this view only. DetailPaneView (main #184) draws the inter-block
    // separators on the terminal-default background; task/comments are mutable so a refresh (#114
    // follow-up) can re-render from fresh data.
    private readonly DetailPaneView _streamPane;
    private TaskDetail _task;
    private IReadOnlyList<CommentItem> _comments;
    private StreamSort _streamSort;

    // The repeating auto-refresh timer's token (Application.AddTimeout), removed on dispose. Null until
    // OnShown arms it.
    private object? _autoRefreshToken;

    // Set in Dispose so a still-in-flight comment post (#216) that completes after the user closed the
    // detail screen doesn't touch the now torn-down tab views. Both this flag and the post continuations
    // run on the UI thread, so there's no race — the continuation either sees it set and bails, or ran
    // before teardown. Mirrors the host's `_screens.Contains(screen)` guard on the refresh path.
    private bool _disposed;

    // Where the Stream tab is scrolled to on open (#107), from the persisted detail-view settings (#108).
    // Content-relative (newest/oldest) so it stays correct across both sort directions; the concrete edge
    // is resolved by DetailScrollModel.
    private readonly StreamAutoScroll _streamAutoScroll;

    // The tab the view opens on (#108), applied in OnShown — setting Tabs.Value in the constructor
    // doesn't stick (the control resets to the first tab when it's first shown).
    private readonly int _defaultTabIndex;

    // True while an auto-scroll (#107) is owed to the Stream pane but hasn't been applied yet. Auto-scroll
    // needs the pane's viewport laid out, which only happens once it's the visible tab — so when the
    // default tab isn't Stream (#108) we defer the scroll until the user first tabs to it, and a sort
    // toggle re-arms it. Applied by FlushStreamAutoScrollIfActive when Stream is (or becomes) front-most.
    private bool _streamAutoScrollPending = true;

    // ── Task Tree tab (#291) ───────────────────────────────────────────────────
    // The Task Tree tab's ListView (its own scroll target), or null when no tree loader was supplied
    // (the tab is then absent). Both hosts supply a loader today — the dashboard, and single-task launch
    // mode since #374 — so this is non-null in both; the null case is the general no-loader fallback.
    // Built from TaskRowRenderer rows exactly like the main list, so ancestry/children badge
    // identically. Loaded lazily the first time the user cycles to the tab.
    private readonly ListView? _treeList;
    // Fetches the tree (ancestry + task + descendants) off the UI thread; injected by the host so the
    // screen stays service-free. Null ⇒ the tab is absent (mirrors the postComment/setDescription seams).
    private readonly Func<CancellationToken, Task<IReadOnlyList<TaskTreeRow>>>? _loadTaskTreeAsync;
    // Rendering inputs threaded from the host: the signed-in user's id (the trailing Assignees badge #161)
    // and the badge mode. Seeded from the host's persisted BadgeDisplay so the tree opens in the same state
    // as the main list, then cycled in place by F6 (#415) exactly like the main list — Icons → Text →
    // Hidden — with the host persisting each step so both surfaces stay in sync.
    private readonly long? _currentUserId;
    private BadgeDisplay _treeBadgeDisplay;
    // Parallel to the tree ListView's rows: the TaskItem each row renders (null for a placeholder/message
    // row), so a keyboard Enter or a double-click resolves the clicked task via the shared RowHitTester.
    private List<TaskItem?> _treeRows = [];
    // The fetched tree rows (empty until the lazy load lands), retained so an F6 badge cycle (#415) can
    // re-render the list in place — icon/text/hidden is a pure display change over the same rows, no re-fetch.
    private IReadOnlyList<TaskTreeRow> _loadedTreeRows = [];
    // True once the lazy tree load has been kicked off (guarding against re-fetching on every tab cycle).
    private bool _treeLoaded;

    // ── Checklists tab (C, #456) ────────────────────────────────────────────────
    // The Checklists tab's ListView (its own scroll target). Always present — unlike the host-gated Task
    // Tree tab, the checklist data (TaskDetail.Checklists, #454) is already on the screen, so the tab
    // appears in both the dashboard detail and single-task launch mode. Built synchronously from the pure
    // projection (ChecklistArranger, #455); no lazy load.
    private readonly ListView _checklistList;
    // Parallel to the ListView's rows: the projected ChecklistRow each line renders (empty on the
    // empty-state row), so a refresh can re-anchor the selection by item id (ChecklistTabModel) and the
    // D–G write slices can resolve a selected row without re-walking the tree.
    private List<ChecklistRow> _checklistRows = [];
    // Content fingerprint of the last-rendered projection, so an unchanged refresh leaves the selection
    // and scroll untouched (the OtherTabSignature discipline, applied to the checklist rows).
    private string _checklistSignature = "";

    /// <summary>Raised when the user activates a tree row (Enter or double-click) for a task other than
    /// the one being shown (#291). The host opens that task's detail stacked over this one, so Esc walks
    /// back one task at a time — the canonical "Esc = Back" model (#401/#298), uniform with the Ctrl+O
    /// detail→detail path (#387). Inert on the current-task row (a no-op, flashed).</summary>
    public event EventHandler<string>? OpenTaskRequested;

    /// <summary>
    /// Raised when the user clicks a link in one of the text panes (D, #318): a plain click on a ClickUp
    /// task link asks for that task in-app, and a Ctrl+click — or a click on any other web link — asks for
    /// the browser (<see cref="LinkActivator.Resolve"/>). The host owns both destinations, since they
    /// differ per host: the dashboard opens the task stacked over this detail, while single-task launch
    /// mode has no in-app task→task destination yet (#374). Inert while an overlay is up (see
    /// <see cref="OnPaneLinkActivation"/>).
    /// </summary>
    public event EventHandler<LinkActivationRequest>? LinkActivationRequested;

    /// <summary>Raised when the user presses F6 on the Task Tree tab (#415) to cycle how that list renders
    /// its Status/Priority badges (Icons → Text → Hidden), mirroring the main list's F6. The host owns the
    /// single source of truth: it cycles + persists <c>AppConfig.BadgeDisplay</c> and reflects the new mode
    /// back via <see cref="SetTreeBadgeDisplay"/> (a pure re-render, no re-fetch), so the main list and the
    /// tree stay in step. Only meaningful when the tree tab exists (a loader was supplied).</summary>
    public event EventHandler? CycleBadgeDisplayRequested;

    /// <summary>True when the user pressed Ctrl+B to open the task in the browser.</summary>
    public bool OpenBrowserRequested { get; private set; }

    /// <summary>The task currently shown, reflecting any refresh since the screen opened (#159 reads it to
    /// launch Quick Updates for the up-to-date task).</summary>
    public TaskDetail Task => _task;

    /// <summary>
    /// Raised when the user submits a non-empty prompt in the Dispatch pane (Ctrl+A). The argument
    /// carries the typed prompt and the chosen session mode (#94; #95/#97 add the remaining options as
    /// they land); the host composes it with the task detail + comments and launches an interactive
    /// <c>claude</c> session or a one-off <c>claude -p</c> run per the mode. The detail view stays open.
    /// </summary>
    public event EventHandler<DispatchRequest>? AgentDispatchRequested;

    /// <summary>
    /// Raised when the view wants fresh data — on F5 / Ctrl+R, or on the 30s auto-refresh tick (#114
    /// follow-up). The host re-fetches the task detail + comments off the UI thread and feeds them back
    /// via <see cref="UpdateData"/>; the view stays open on its current tab and scroll position.
    /// </summary>
    public event EventHandler? RefreshRequested;

    /// <summary>
    /// Raised when the user asks to open Quick Updates (Ctrl+U, #159) for this task. The host stacks the
    /// Quick Updates screen over the detail view; on exit the screen seam pops back here, and
    /// <see cref="ApplyOptimisticStatus"/> reflects a status change made there so the returned-to detail
    /// shows it immediately.
    /// </summary>
    public event EventHandler? QuickUpdatesRequested;

    /// <summary>
    /// Raised when the user asks to quick-open another task by id / custom id / URL (Ctrl+O, #353) from
    /// within the detail view. The host opens the same entry surface it uses from the main list, stacked
    /// over this detail; resolving a target opens its Task Detail over the current one (Esc walks back),
    /// mirroring how Quick Updates (Ctrl+U) stacks.
    /// </summary>
    public event EventHandler? QuickOpenRequested;

    /// <summary>
    /// Raised when the user asks to open the current task in its own terminal tab (Ctrl+Enter, #384) —
    /// the detail-view counterpart of the main list's Ctrl+Enter / Ctrl+Left-Click gesture (#301). The
    /// host owns the cross-platform launch (and its copy-command fallback), reusing the exact launcher
    /// the list gesture uses. Raised (and advertised) wherever a tree loader was supplied — the
    /// dashboard-hosted detail (#384) and, since #374 gave single-task launch mode the Task Tree tab, that
    /// host too (its subscriber wired in #435) — see the <c>_treeList</c> guard in <see cref="OnKey"/> and
    /// the <see cref="HelpItemSets.DetailWithTaskTree"/> footer set.
    /// </summary>
    public event EventHandler? OpenInNewTabRequested;

    /// <param name="defaultSessionMode">
    /// Seeds the pane's one-off/interactive toggle (#94) from the persisted default (#101); the user
    /// can flip it per dispatch. Defaults to <see cref="AgentSessionMode.Interactive"/>.
    /// </param>
    /// <param name="defaultPostToComments">
    /// Seeds the pane's post-results-to-Comments toggle (#97) from the persisted default; the user can
    /// flip it per dispatch. Defaults to off.
    /// </param>
    /// <param name="defaultLaunchLocation">
    /// Seeds the pane's launch-location toggle (#275) from the persisted default
    /// (<c>AgentDispatchSettings.LaunchLocation</c>); the user can override it per dispatch. Only applies
    /// to interactive dispatches (the toggle is greyed out in one-off mode). Defaults to
    /// <see cref="LaunchLocation.NewWindow"/>.
    /// </param>
    /// <param name="workingDirectoryPreFill">
    /// Supplies the per-task cached working directory (#96) to pre-fill the pane's working-dir field
    /// with. Invoked <b>each time the pane opens</b> (not captured once), so a dispatch that updates the
    /// cache is reflected when the pane is reopened within this same still-open detail screen. Returns
    /// blank ⇒ start blank (⇒ configured default / task-derived dir #98). Null ⇒ always blank. The
    /// browser still resets to its root; pre-fill is independent of navigation.
    /// </param>
    /// <param name="postCommentAsync">
    /// Posts a plain-text comment to this task (#216, over the #210 facade) and returns the created
    /// <see cref="CommentItem"/>. The screen owns the composer UI + optimistic append/revert; the host
    /// owns the off-thread ClickUp write via this callback (the same injected-async seam #212 uses).
    /// Null disables the composer (<c>Ctrl+N</c> is inert), so non-interactive hosts stay unaffected.
    /// </param>
    /// <param name="postReplyAsync">
    /// Posts a plain-text reply into a comment's thread (#330, over the #327 create-reply facade) and
    /// returns the created <see cref="CommentItem"/>. Takes the parent comment id and the reply text. The
    /// screen owns the reply-target picker + reply-mode composer + optimistic nested append/revert; the
    /// host owns the off-thread ClickUp write via this callback. Null disables reply (<c>Ctrl+T</c> is
    /// inert), so non-interactive hosts stay unaffected.
    /// </param>
    /// <param name="setDescriptionAsync">
    /// Writes this task's plain-text description (#217, over the #211 facade) and returns the
    /// server-confirmed value. The screen owns the editor UI + the dirty-check + the in-place reflection;
    /// the host owns the off-thread ClickUp write via this callback (the same injected-async seam the
    /// comment composer uses). Null disables the editor (<c>Ctrl+E</c> is inert), so non-interactive
    /// hosts stay unaffected.
    /// </param>
    public TaskDetailScreen(
        TaskDetail task,
        IReadOnlyList<CommentItem> comments,
        string baseWorkingDirectory,
        DetailViewSettings? settings = null,
        AgentSessionMode defaultSessionMode = AgentSessionMode.Interactive,
        bool defaultPostToComments = false,
        LaunchLocation defaultLaunchLocation = LaunchLocation.NewWindow,
        Func<string>? workingDirectoryPreFill = null,
        Func<string, CancellationToken, Task<CommentItem>>? postCommentAsync = null,
        Func<string, string, CancellationToken, Task<CommentItem>>? postReplyAsync = null,
        Func<string, CancellationToken, Task<string?>>? setDescriptionAsync = null,
        long? currentUserId = null,
        BadgeDisplay treeBadgeDisplay = BadgeDisplay.Text,
        Func<CancellationToken, Task<IReadOnlyList<TaskTreeRow>>>? loadTaskTreeAsync = null)
    {
        var prefs = settings ?? new DetailViewSettings();
        _task = task;
        _comments = comments;
        _workingDirectoryPreFill = workingDirectoryPreFill;
        _postCommentAsync = postCommentAsync;
        _postReplyAsync = postReplyAsync;
        _setDescriptionAsync = setDescriptionAsync;
        _currentUserId = currentUserId;
        _treeBadgeDisplay = treeBadgeDisplay;
        _loadTaskTreeAsync = loadTaskTreeAsync;
        _browser = new DirectoryBrowserModel(baseWorkingDirectory);
        _streamSort = prefs.StreamSort;
        _streamAutoScroll = prefs.AutoScroll;
        Title = task.Name.Length > 60 ? task.Name[..59] + "…" : task.Name;

        // The title line carries trailing coloured Status/Priority badges (#162), which a plain Label
        // can't draw — render the header through the same per-run-coloured view the Other tab uses
        // (DetailAttributesView), fed by the structured HeaderLines. Non-focusable, like the Label it
        // replaces, so the screen's focus/latency model is unchanged. Kept as a field + signature so a
        // refresh (#114 follow-up) re-renders it in place only when its content moved.
        var headerLinesForTitle = TaskDetailFormatter.HeaderLines(task);
        var headerHeight = headerLinesForTitle.Count;
        _headerSignature = OtherTabSignature(headerLinesForTitle, "");
        _headerView = new DetailAttributesView(headerLinesForTitle)
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = headerHeight,
        };

        // NavSafeTabs, not the stock Tabs: its arrow-key tab navigation crashes the app in Terminal.Gui
        // 2.4.10 when cycling past the first/last tab (→ from the last, ← from the first). Tab switching
        // is owned here via Ctrl+←/→ (CycleTab), so the native arrow navigation is disabled.
        _tabs = new NavSafeTabs
        {
            X = 0,
            Y = headerHeight + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };

        // The Stream tab (#106): Description + comments as one timeline, sortable in place. Built first
        // so it's the default selected tab below. Each body is captured so a refresh only re-renders the
        // pane when its content actually moved.
        _streamText = TaskDetailFormatter.Stream(task, comments, _streamSort);
        _streamPane = NewPane("Stream", _streamText);
        _descriptionText = TaskDetailFormatter.Description(task);
        _descriptionPane = NewPane("Description", _descriptionText);
        _commentsText = TaskDetailFormatter.Comments(comments, _streamSort);
        _commentsPane = NewPane($"Comments ({comments.Count})", _commentsText);

        // Click a link in any pane → act on it (D, #318). Each pane hit-tests its own body and resolves
        // the action; the screen only gates it on no overlay owning input and forwards it to the host.
        // The configurable Ctrl+Click destination (#320) rides on the persisted DetailViewSettings — the
        // pane's Resolve consults it, and the host does what the destination asks (a new-tab action
        // degrades to the browser in single-task mode, tracked by #435).
        foreach (var pane in new[] { _streamPane, _descriptionPane, _commentsPane })
        {
            pane.TaskLinkCtrlClickDestination = prefs.TaskLinkCtrlClick;
            pane.LinkActivationRequested += OnPaneLinkActivation;
        }

        // The Other tab colours its Priority/Status values (#66), which a plain TextView can't do. Its
        // content is a container (a coloured, fixed-height header view on top of the scrollable,
        // word-wrapped "Custom fields:" body). DetailOtherTabView owns that split and adapts it so both
        // the header attributes and the custom-fields section stay reachable on a very short window (#81).
        var headerLines = TaskDetailFormatter.HeaderAttributeLines(task);
        var customFieldsBody = TaskDetailFormatter.CustomFieldsBody(task);
        _otherSignature = OtherTabSignature(headerLines, customFieldsBody);
        _otherTab = new DetailOtherTabView(headerLines, customFieldsBody);

        // The Task Tree tab (#291): a focusable ListView showing the task's ancestry + itself + its
        // descendants, indented and badged like the main list (via the shared TaskRowRenderer). Appended
        // as a fifth tab whenever the host supplied a loader — both the dashboard and single-task launch
        // mode do (the latter since #374), so it's present in both. It's its own scroll
        // target; CycleTab/FocusCurrentPane/ScrollActiveTab are array-length-driven and pick it up. Rows
        // load lazily on first cycle to the tab (EnsureTreeLoaded), so opening any detail isn't slowed.
        // The Checklists tab (C, #456): a focusable ListView rendering the task's native ClickUp
        // checklists (groups + nested items, from the pure ChecklistArranger projection over #454's read
        // model). Inserted after Other at index 4 — before the conditionally-appended Task Tree tab, so
        // both indices are stable across hosts — and present in both hosts (the data is already on the
        // screen, no host-supplied loader). Its own scroll target; CycleTab/FocusCurrentPane/MoveActiveTab
        // are array-length-driven and pick it up. Rendered synchronously below (no lazy load).
        _checklistList = new ListView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        RenderChecklist(ChecklistArranger.Project(task.Checklists));

        var tabContents = new List<View> { _streamPane, _descriptionPane, _commentsPane, _otherTab, _checklistList };
        var scrollTargets = new List<View> { _streamPane, _descriptionPane, _commentsPane, _otherTab.ScrollTarget, _checklistList };
        if (_loadTaskTreeAsync is not null)
        {
            _treeList = new ListView
            {
                Title = "Task Tree",
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };
            _treeList.SetSource(new ObservableCollection<string>(["Loading task tree…"]));
            _treeRows = [null];
            // Double-click a tree row → navigate to that task (the mouse equivalent of Enter), via the
            // shared row hit-test (A, #286). Single-click keeps native selection.
            _treeList.MouseEvent += OnTreeMouse;
            tabContents.Add(_treeList);
            scrollTargets.Add(_treeList);
        }
        _tabContents = [.. tabContents];
        _scrollTargets = [.. scrollTargets];

        for (var i = 0; i < _tabContents.Length; i++)
            _tabs.InsertTab(i, _tabContents[i]);
        // Open on the configured default tab (#108); Stream unless the user changed it in F2. The
        // selection is (re)asserted in OnShown — setting it here alone doesn't survive first show.
        _defaultTabIndex = prefs.DefaultTab.ToTabIndex();
        _tabs.Value = _tabContents[_defaultTabIndex];
        // Selecting a tab by clicking its header (a native Tabs gesture) must behave like the Ctrl+←/→
        // cycle: focus the pane so ↑/↓/Enter reach it, and lazy-load the Task Tree tab (#291) — otherwise
        // a mouse user clicking straight onto that tab would be stuck on "Loading task tree…". Wired after
        // the default-tab assignment above so it doesn't fire during construction; CycleTab still handles
        // the keyboard path (this makes both converge). Idempotent: FocusCurrentPane/EnsureTreeLoaded are
        // safe to re-run, so the extra fire during CycleTab's own Value set is harmless.
        _tabs.ValueChanged += (_, _) =>
        {
            FocusCurrentPane();
            EnsureTreeLoaded();
        };

        // The Dispatch pane (#93, D1 of the #90 epic; superseding the single-line #26 prompt): a
        // bottom-anchored FrameView hosting the prompt plus the one-off/interactive (#94), working-dir
        // (#95) and post-to-Comments (#97) option controls. Hidden until Ctrl+A.
        // A transient child view within the single already-open screen — not a nested run-loop or a
        // second toplevel (the #26 design note) — so the dashboard's single-ListView model (#3) is
        // untouched. Its height is computed on show (ShowPrompt) so it degrades gracefully on short
        // terminals: the prompt stays visible; the bottom stub controls clip first. The screen's own
        // shortcuts (incl. Ctrl+A) show in the window-owned contextual help footer via HelpItems (#103).
        var promptLabel = new Label { X = 1, Y = 0, Text = "Prompt:" };
        _promptField = new TextField { X = 9, Y = 0, Width = Dim.Fill(1) };
        // The one-off/interactive toggle (#94) is live: seeded from the persisted default (#101) and
        // read into the DispatchRequest on submit. The working-dir control (#95) below is also live —
        // an editable field plus a file-tree browser; blank ⇒ default working dir. The post-to-Comments
        // (#97) toggle is likewise live: seeded from its persisted default and read on submit.
        _oneOffToggle = new CheckBox
        {
            X = 1,
            Y = 1,
            Text = "Run one-off (claude -p) instead of an interactive session",
            Value = defaultSessionMode == AgentSessionMode.OneOff ? CheckState.Checked : CheckState.UnChecked,
        };
        var dirLabel = new Label { X = 1, Y = 2, Text = "Dir:" };
        _workingDirField = new TextField { X = 9, Y = 2, Width = Dim.Fill(1) };
        var browserHint = new Label
        {
            X = 1,
            Y = 3,
            Text = "↑↓ pick · → open · ← up · Enter confirm (blank ⇒ default dir)",
        };
        _dirBrowser = new ListView
        {
            X = 1,
            Y = DispatchRowsAboveBrowser,
            Width = Dim.Fill(1),
            Height = DispatchBrowserRows,
        };
        _dirBrowser.SetSource(new ObservableCollection<string>(_browser.Entries));
        _dirBrowser.SelectedItem = 0;
        // Live (#97): seeded from the persisted default; when on, the composed prompt instructs the
        // dispatched agent to post a summary comment to the task. The app never posts it itself — the
        // agent does — so the label notes it needs ClickUp MCP access (kept inline, like the one-off
        // toggle's explanatory text, so the pane keeps one focusable control per row).
        _postToCommentsToggle = new CheckBox
        {
            X = 1,
            Y = DispatchRowsAboveBrowser + DispatchBrowserRows,
            Text = "Post results to Comments (agent needs ClickUp MCP access)",
            Value = defaultPostToComments ? CheckState.Checked : CheckState.UnChecked,
        };
        // Live (#275): seeded from the persisted default; checked ⇒ open this session in a new tab of
        // the current terminal (where the host supports it), unchecked ⇒ a new window. Read on submit
        // via DispatchPaneModel.ToLaunchLocation. It only affects interactive sessions — a one-off -p
        // run has no terminal — so it's greyed out (and skipped by Tab) whenever one-off is checked;
        // UpdateLaunchLocationEnabled keeps that in sync with the one-off toggle.
        _launchLocationToggle = new CheckBox
        {
            X = 1,
            Y = DispatchRowsAboveBrowser + DispatchBrowserRows + 1,
            Text = "Open in a new tab of this terminal (interactive only; else a new window)",
            Value = defaultLaunchLocation == LaunchLocation.NewTab ? CheckState.Checked : CheckState.UnChecked,
        };

        _dispatchControls = [_promptField, _oneOffToggle, _workingDirField, _dirBrowser, _postToCommentsToggle, _launchLocationToggle];

        var paneHeight = DispatchPaneModel.PreferredHeightWithBrowser(
            DispatchRowsAboveBrowser, DispatchBrowserRows, DispatchRowsBelowBrowser);
        _promptBox = new FrameView
        {
            Title = "Dispatch to Claude — Enter submit · Tab next · Esc cancel",
            X = 0,
            Y = Pos.AnchorEnd(paneHeight),
            Width = Dim.Fill(),
            Height = paneHeight,
            Visible = false,
        };
        _promptBox.Add(promptLabel, _promptField, _oneOffToggle, dirLabel, _workingDirField, browserHint, _dirBrowser, _postToCommentsToggle, _launchLocationToggle);
        // Each dispatch control routes the pane's keys (Enter/Esc/Tab/PgUp/PgDn) via the pure
        // DispatchPaneModel; other keys fall through so typing/Space-toggle keep working. The browser
        // gets its own handler so Enter/→/← navigate it instead of submitting the dispatch (#95).
        foreach (var control in _dispatchControls)
        {
            if (ReferenceEquals(control, _dirBrowser))
                control.KeyDown += OnBrowserKey;
            else
                control.KeyDown += OnDispatchKey;
        }
        // Selection-follows-cursor (#95 follow-up): moving the highlight in the browser (↑/↓, a mouse
        // click, or a descend/up that re-homes it) writes the highlighted directory straight into the
        // path field, so the highlighted dir is the one that dispatches — no separate Enter/select step.
        // Wired after the constructor's initial SelectedItem=0 so that first assignment can't fire it.
        _dirBrowser.ValueChanged += OnBrowserSelectionChanged;
        // The launch-location toggle (#275) only applies to interactive sessions, so flipping the
        // one-off toggle greys it in/out. Wired after seeding both toggles (their initializers set
        // Value before this handler exists, so no premature fire); the initial state is set once now.
        _oneOffToggle.ValueChanged += (_, _) => UpdateLaunchLocationEnabled();
        UpdateLaunchLocationEnabled();

        // The comment composer (#216): a bottom-anchored FrameView with a multi-line editor above a
        // Post/Cancel button row, hidden until Ctrl+N. Modelled on PromptTemplateEditorScreen — the
        // editor keeps Enter for newlines (TabKeyAddsTab=false so Tab reaches the buttons) and the
        // Post button is the default (Enter posts when a button has focus), so submit is driver-robust;
        // Ctrl+Enter is wired as an extra shortcut. Height is sized on show (ShowCommentComposer).
        _commentEditor = new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            WordWrap = true,
            TabKeyAddsTab = false,
        };
        var postButton = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Post", IsDefault = true };
        var cancelButton = new Button { X = Pos.Right(postButton) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        postButton.Accepting += (_, _) => PostComment();
        cancelButton.Accepting += (_, _) => HideCommentComposer();
        _commentControls = [_commentEditor, postButton, cancelButton];
        _commentBox = new FrameView
        {
            Title = "New comment — Ctrl+Enter or Tab→Post · Esc cancel",
            X = 0,
            Y = Pos.AnchorEnd(CommentComposerPreferredHeight),
            Width = Dim.Fill(),
            Height = CommentComposerPreferredHeight,
            Visible = false,
        };
        _commentBox.Add(_commentEditor, postButton, cancelButton);
        // Each composer control routes the pane's keys (Ctrl+Enter/Esc/Tab/Shift+Tab) via the pure
        // CommentComposerModel; other keys fall through so typing + Enter-newline keep working.
        foreach (var control in _commentControls)
            control.KeyDown += OnCommentKey;

        // The reply-target picker (#330): a bottom-anchored FrameView hosting a single ListView of the
        // task's top-level comments, hidden until Ctrl+T. Like the comment composer it's a transient child
        // view within the single already-open screen (not a nested run-loop / second screen), so the
        // single-ListView model (#3) is untouched. ↑/↓ move the highlight (native), Enter (or a row click,
        // #283) picks — opening the composer in reply mode targeting that comment — and Esc cancels. Sized
        // on show (ShowReplyPicker); its rows are set there from CommentReplyModel.
        _replyPicker = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        _replyPicker.KeyDown += OnReplyPickerKey;
        _replyPicker.MouseEvent += OnReplyPickerMouse;
        _replyPickerBox = new FrameView
        {
            Title = "Reply to… — ↑/↓ choose · Enter reply · Esc cancel",
            X = 0,
            Y = Pos.AnchorEnd(ReplyPickerPreferredHeight),
            Width = Dim.Fill(),
            Height = ReplyPickerPreferredHeight,
            Visible = false,
        };
        _replyPickerBox.Add(_replyPicker);

        // The description editor (#217): a bottom-anchored FrameView with a multi-line editor above a
        // Save/Cancel button row (and a hidden confirm row), shown on Ctrl+E. Modelled on the comment
        // composer — the editor keeps Enter for newlines (TabKeyAddsTab=false so Tab reaches the
        // buttons) and Save is the default (Enter saves when a button has focus), so submit is
        // driver-robust; Ctrl+Enter is wired as an extra save shortcut. Height is sized on show
        // (ShowDescriptionEditor). Seeded (pre-filled) from the current description on each open.
        _descriptionEditor = new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            WordWrap = true,
            TabKeyAddsTab = false,
        };
        // The inline discard confirm sits on its own row above the buttons so it never disturbs the
        // editor/button layout; blank unless an Esc-on-dirty armed it.
        _descriptionConfirm = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = "" };
        var saveButton = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancelDescriptionButton = new Button { X = Pos.Right(saveButton) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        saveButton.Accepting += (_, _) => SaveDescription();
        cancelDescriptionButton.Accepting += (_, _) => CancelDescriptionEditor();
        _descriptionControls = [_descriptionEditor, saveButton, cancelDescriptionButton];
        _descriptionBox = new FrameView
        {
            Title = "Edit description — Ctrl+Enter or Tab→Save · Esc cancel",
            X = 0,
            Y = Pos.AnchorEnd(DescriptionEditorPreferredHeight),
            Width = Dim.Fill(),
            Height = DescriptionEditorPreferredHeight,
            Visible = false,
        };
        _descriptionBox.Add(_descriptionEditor, _descriptionConfirm, saveButton, cancelDescriptionButton);
        // Each editor control routes the pane's keys (Ctrl+Enter/Esc/Tab/Shift+Tab/F1 + the pending Y/N)
        // via OnDescriptionKey; other keys fall through so typing + Enter-newline keep working.
        foreach (var control in _descriptionControls)
            control.KeyDown += OnDescriptionKey;

        // Focus lives in whichever scroll target (TextView) is front-most, so the key handler is wired
        // to each to reliably intercept Tab/Esc/Ctrl+B/Ctrl+A/F1 before the read-only TextView sees them.
        foreach (var target in _scrollTargets)
            target.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([_headerView, _tabs, _promptBox, _commentBox, _replyPickerBox, _descriptionBox]);
    }

    // While the comment composer (Ctrl+N) or description editor (Ctrl+E) overlay is open, the footer
    // shows only that overlay's keys — the command chords are inert to a keypress (OnKey returns early)
    // but their clickable footer hints would otherwise re-raise the chord into the composer (#436). The
    // Task Tree tab's F6 badge cycle (#415) and the Ctrl+Enter new-tab gesture (#384/#435) are only
    // offered when that tab exists (a loader was supplied) — true for both the dashboard and single-task
    // launch mode (since #374); the F6-/Ctrl+Enter-less Detail set is the no-loader fallback.
    public override IReadOnlyList<HelpItem> HelpItems =>
        HelpItemSets.DetailFooter(_commentBox.Visible, _descriptionBox.Visible, _replyPickerBox.Visible, _treeList is not null);

    public override void OnShown()
    {
        // Select the configured default tab (#108) now that the control is shown (a constructor-time
        // Tabs.Value doesn't survive first display), then focus its scroll target so ↑/↓ scroll it.
        _tabs.Value = _tabContents[_defaultTabIndex];
        FocusCurrentPane();
        // Land on the newest (or oldest) Stream entry per the preference (#107). Applied only if Stream
        // is the (now laid-out) front-most tab; otherwise it's deferred until the user tabs to it (#108).
        FlushStreamAutoScrollIfActive();

        // Auto-refresh the detail every 30s (#114 follow-up). The timeout callback fires on the UI
        // thread; returning true keeps it repeating. Armed once here and torn down in Dispose.
        _autoRefreshToken ??= Application.AddTimeout(AutoRefreshInterval, () =>
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
            return true;
        });
    }

    /// <summary>F5 / Ctrl+R — flashes and asks the host to re-fetch this task's detail + comments.</summary>
    private void RequestRefresh()
    {
        RequestFlash("Refreshing…");
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-renders every tab from freshly-fetched data (F5 / Ctrl+R or the 30s tick). Must run on the UI
    /// thread. Each pane is only reassigned when its text actually changed, so an unchanged poll leaves
    /// the cursor and scroll untouched; the Stream tab re-arms its auto-scroll (#107) only when its
    /// content moved, so a genuinely new comment lands on the configured edge without yanking the view
    /// on every idle tick.
    /// </summary>
    public void UpdateData(TaskDetail task, IReadOnlyList<CommentItem> comments)
    {
        _task = task;
        _comments = comments;

        // Title header (#162): coloured attribute lines; re-render in place only when they moved.
        var titleHeaderLines = TaskDetailFormatter.HeaderLines(task);
        var headerSignature = OtherTabSignature(titleHeaderLines, "");
        if (!string.Equals(_headerSignature, headerSignature, StringComparison.Ordinal))
        {
            _headerSignature = headerSignature;
            _headerView.Update(titleHeaderLines);
            // The header is variable-height (the Tags line appears only when the task has tags), so keep
            // the one-row gap before the tabs correct if a refresh changed the line count. _headerView's
            // own Height is re-set by Update; _tabs sits just below it.
            _tabs.Y = titleHeaderLines.Count + 1;
        }

        var streamText = TaskDetailFormatter.Stream(task, comments, _streamSort);
        if (!string.Equals(_streamText, streamText, StringComparison.Ordinal))
        {
            _streamText = streamText;
            RefreshStreamPane(streamText);
        }

        var descriptionText = TaskDetailFormatter.Description(task);
        if (!string.Equals(_descriptionText, descriptionText, StringComparison.Ordinal))
        {
            _descriptionText = descriptionText;
            SetBodyKeepingScroll(_descriptionPane, descriptionText);
        }

        var commentsText = TaskDetailFormatter.Comments(comments, _streamSort);
        if (!string.Equals(_commentsText, commentsText, StringComparison.Ordinal))
        {
            _commentsText = commentsText;
            SetBodyKeepingScroll(_commentsPane, commentsText);
        }
        var commentsTitle = $"Comments ({comments.Count})";
        if (!string.Equals(_commentsPane.Title, commentsTitle, StringComparison.Ordinal))
            _commentsPane.Title = commentsTitle;

        var headerLines = TaskDetailFormatter.HeaderAttributeLines(task);
        var customFieldsBody = TaskDetailFormatter.CustomFieldsBody(task);
        var otherSignature = OtherTabSignature(headerLines, customFieldsBody);
        if (!string.Equals(_otherSignature, otherSignature, StringComparison.Ordinal))
        {
            _otherSignature = otherSignature;
            _otherTab.Update(headerLines, customFieldsBody);
        }

        // Checklists tab (C, #456): rebuild only when the projected content moved, so an unchanged poll
        // leaves the selected row and scroll untouched (the ListView keeps its own state while its Source
        // is not reassigned). When it changed, re-anchor the cursor to the same item id where it survives.
        var checklistProjection = ChecklistArranger.Project(task.Checklists);
        if (!string.Equals(_checklistSignature, ChecklistTabModel.Signature(checklistProjection), StringComparison.Ordinal))
        {
            var oldRows = _checklistRows;
            var oldIndex = _checklistList.SelectedItem ?? 0;
            RenderChecklist(checklistProjection);
            var anchored = ChecklistTabModel.AnchorSelection(oldRows, oldIndex, _checklistRows);
            var count = _checklistList.Source?.Count ?? 0;
            if (count > 0 && anchored >= 0 && anchored < count)
                _checklistList.SelectedItem = anchored;
        }
    }

    /// <summary>
    /// Optimistically reflects a status change applied via Quick Updates stacked over this screen (#159),
    /// so the detail shows the new status the moment it pops back into view. Re-renders through
    /// <see cref="UpdateData"/> (in-place; scroll/cursor preserved). The host's off-thread write and the
    /// 30s auto-refresh reconcile the authoritative server value afterward.
    /// </summary>
    public void ApplyOptimisticStatus(string? statusName, string? statusColor)
        => UpdateData(_task with { StatusName = statusName, StatusColor = statusColor }, _comments);

    /// <summary>
    /// Optimistically reflects a priority change applied via Quick Updates stacked over this screen (#159),
    /// mirroring <see cref="ApplyOptimisticStatus"/> (in-place re-render; the server write + 30s
    /// auto-refresh reconcile afterward). <paramref name="priorityName"/> null clears the priority.
    /// </summary>
    public void ApplyOptimisticPriority(string? priorityName, string? priorityColor)
        => UpdateData(_task with { Priority = priorityName, PriorityColor = priorityColor }, _comments);

    /// <summary>
    /// Re-renders the Stream tab on refresh. If the reader was parked at the auto-scroll edge (i.e.
    /// following the newest — or oldest — entry, per the #107 preference), keep following it as new
    /// entries arrive; otherwise keep their scroll position so a fresh comment doesn't yank the view.
    /// </summary>
    private void RefreshStreamPane(string streamText)
    {
        var followingEdge = DetailScrollModel.ResolveEdge(_streamAutoScroll, _streamSort) switch
        {
            DetailScrollModel.Edge.Bottom => TopRow(_streamPane) >= MaxTopRow(_streamPane),
            _ => TopRow(_streamPane) == 0,
        };
        if (followingEdge)
        {
            // Reset scroll, then re-anchor to the (new) edge. SetBody (not .Text) keeps the separators
            // drawn on the terminal-default background (#184).
            _streamPane.SetBody(streamText, TaskDetailFormatter.CommentSeparator);
            _streamAutoScrollPending = true;
            FlushStreamAutoScrollIfActive();
        }
        else
        {
            SetBodyKeepingScroll(_streamPane, streamText);
        }
    }

    /// <summary>The pane's current top scroll row.</summary>
    private static int TopRow(TextView pane) => pane.Viewport.Y;

    /// <summary>The largest valid top row for the pane's current content and viewport height.</summary>
    private static int MaxTopRow(TextView pane) => Math.Max(0, pane.Lines - Math.Max(1, pane.Viewport.Height));

    /// <summary>Loads a pane's body (via <see cref="DetailPaneView.SetBody"/>, so separator styling
    /// #184 is preserved) but restores the prior top scroll row (clamped to the new content), so an
    /// in-place refresh (#114 follow-up) doesn't reset a reader to the top. On the front-most (laid-out)
    /// pane the viewport height is real; on a background tab the clamp keeps it in range and the offset
    /// re-applies when the user tabs to it.</summary>
    private static void SetBodyKeepingScroll(DetailPaneView pane, string text)
    {
        var top = TopRow(pane);
        pane.SetBody(text, TaskDetailFormatter.CommentSeparator);
        var restored = Math.Min(top, MaxTopRow(pane));
        if (restored > 0)
        {
            var vp = pane.Viewport;
            pane.Viewport = new Rectangle(vp.X, restored, vp.Width, vp.Height);
        }
    }

    /// <summary>A cheap content fingerprint of the Other tab (attribute lines + custom-fields body) so
    /// a refresh only rebuilds that tab when its rendered content moved. Line texts are newline-joined
    /// and separated from the body by a sentinel; a collision would only skip a cosmetic rebuild.</summary>
    private static string OtherTabSignature(
        IReadOnlyList<TaskDetailFormatter.DetailLine> lines, string customFieldsBody)
        => string.Join("\n", lines.Select(l => string.Concat(l.Runs.Select(r => r.Text))))
           + "\n\u0000\n" + customFieldsBody;

    private void OnKey(object? sender, Key key)
    {
        // While the comment composer (#216) or the description editor (#217) is open it owns the
        // keyboard: its own handler (OnCommentKey / OnDescriptionKey) processes Ctrl+Enter/Esc/Tab and
        // lets the rest fall through to the editor. Don't let the screen's chords (Ctrl+B close,
        // Ctrl+A/U/N/E openers, Ctrl+←/→ tab-cycle, F5 refresh) fire underneath and disrupt (or discard)
        // the draft.
        if (_commentBox.Visible || _descriptionBox.Visible || _replyPickerBox.Visible)
            return;

        // Enter on the Task Tree tab (#291) navigates the detail screen to the selected row's task
        // (the current-task row no-ops). Guarded on the tree being the front-most tab, so Enter on the
        // read-only text panes (which ignore it) is undisturbed.
        if (key.KeyCode == KeyCode.Enter && _treeList is not null && ReferenceEquals(_tabs.Value, _treeList))
        {
            key.Handled = true;
            NavigateTreeSelection();
            return;
        }

        // F6 on the Task Tree tab (#415) cycles the tree's badge display (Icons → Text → Hidden), matching
        // the main list. Guarded on the tree being front-most, so F6 on the read-only text panes stays inert
        // (they have no badges). The host owns the flip/persist and reflects it back via SetTreeBadgeDisplay.
        if (key.KeyCode == KeyCode.F6 && _treeList is not null && ReferenceEquals(_tabs.Value, _treeList))
        {
            key.Handled = true;
            CycleBadgeDisplayRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // #319 (E): on a text pane, bare Tab/Shift+Tab step keyboard focus across the in-pane links and
        // Enter activates the focused one — the keyboard equivalent of the #318 click. Routed here (not in
        // the pane) because OnKey already owns the screen's key vocabulary and fires before Terminal.Gui's
        // focus traversal, so it can consume bare Tab. Guarded to the text panes (their scroll target is a
        // DetailPaneView): the Task Tree tab's Enter is handled above and the Other tab has no links. Each
        // call is inert (returns false → key falls through) when there's nothing to do — no links for Tab,
        // no focused link for Enter — so an empty pane's Tab and an unfocused Enter behave as they did.
        if (!_promptBox.Visible && ActiveTextPane() is { } linkPane)
        {
            // Mask ShiftMask so Shift+Tab (which the ansi driver folds into KeyCode.Tab | ShiftMask) is
            // matched too; IsShift then picks the direction — the same shape the comment composer uses.
            // Guarded on !_promptBox.Visible like the sibling command blocks below: the Dispatch pane's
            // own handlers already consume Tab/Enter while it's open, but this removes the hidden reliance.
            if ((key.KeyCode & ~KeyCode.ShiftMask) == KeyCode.Tab && linkPane.StepLinkFocus(forward: !key.IsShift))
            {
                key.Handled = true;
                return;
            }
            if (key.KeyCode == KeyCode.Enter && linkPane.ActivateFocusedLink())
            {
                key.Handled = true;
                return;
            }
        }

        // Bare ↑/↓ scroll the front-most text pane one line, or move the Task Tree selection one row
        // (#452). Claimed here — on the focused pane/list's own KeyDown, which fires before its bindings
        // and before the arrow bubbles up to NavSafeTabs — because otherwise the arrow is swallowed:
        // the read-only TextView moves an invisible caret (no viewport scroll), and the tree ListView's
        // Command.Down bubbles up to NavSafeTabs' inert crash-guard, cancelling its own MoveDown. Inert
        // while the Dispatch prompt is open (its dir-browser owns bare ↑/↓, like the command blocks
        // below) and for any modified arrow (Ctrl+←/→ tab-cycle, Shift-extend). Always consumed, so a
        // press at a content boundary is a no-op that stays on the tab — never a tab switch or crash.
        if (!_promptBox.Visible && !key.IsCtrl && !key.IsShift && !key.IsAlt
            && (key.KeyCode == KeyCode.CursorUp || key.KeyCode == KeyCode.CursorDown))
        {
            key.Handled = true;
            MoveActiveTab(key.KeyCode == KeyCode.CursorDown ? 1 : -1);
            return;
        }

        // Bare PgUp/PgDn page the front-most text pane via the same viewport write as ↑/↓ (#468), so the
        // whole scroll vocabulary of the read-only panes lives in one explicit viewport model rather than
        // split between our ↑/↓ code (#452) and Terminal.Gui's stock TextView paging. Owning it keeps the
        // two gestures composing on one state (`viewport.Y`) regardless of what a given TG version or
        // terminal driver does for Command.PageUp/PageDown — the cross-platform concern behind #468/#312.
        // (On TG 2.4.10 the stock commands already page the viewport, so this is behaviour-preserving
        // today; the value is the explicit, driver-independent ownership.) Text panes only — the Task Tree
        // ListView keeps its stock page-selection (PageActiveTextPane returns false ⇒ the key falls
        // through to it). Inert while the Dispatch prompt is open (its dir-browser owns paging) and for any
        // modified key: Ctrl+PgUp/PgDn are the Stream-sort chords below, excluded here by !IsCtrl. NextTop
        // clamps, so a page at the content boundary is a consumed no-op that stays on the tab.
        if (!_promptBox.Visible && !key.IsCtrl && !key.IsShift && !key.IsAlt
            && (key.KeyCode == KeyCode.PageUp || key.KeyCode == KeyCode.PageDown)
            && PageActiveTextPane(key.KeyCode == KeyCode.PageDown ? 1 : -1))
        {
            key.Handled = true;
            return;
        }

        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.B)
        {
            key.Handled = true;
            OpenBrowserRequested = true;
            Close();
            return;
        }

        // Ctrl+Enter opens this task in its own terminal tab (#384) — the detail counterpart of the main
        // list's #301 gesture; the host owns the launch + copy-command fallback. Ctrl+Enter carries
        // KeyCode.Enter | CtrlMask, so it never trips the bare-Enter tree-row navigation above. Gated on
        // _treeList (the same seam HelpItems uses to pick the footer set), so the footer hint and the key
        // stay in lock-step — present wherever a loader was supplied: the dashboard, and single-task launch
        // mode too since #374/#435 (subscriber in SingleTaskApp). Inert
        // while the Dispatch pane is open (like the other command chords), and unreachable while the
        // comment composer / description editor is open (they own the keyboard via the guard above, where
        // Ctrl+Enter means Save).
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Enter && !_promptBox.Visible && _treeList is not null)
        {
            key.Handled = true;
            OpenInNewTabRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Ctrl+A opens the Dispatch pane (#93; the bare-A trigger of #26 is retired — Ctrl-chords match
        // the codebase's command model and free the letter). Same chord shape as Ctrl+B above; the
        // read-only panes never need Ctrl+A (select-all), so pre-empting it is safe. Inert while open.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.A && !_promptBox.Visible)
        {
            key.Handled = true;
            ShowPrompt();
            return;
        }

        // Ctrl+N opens the comment composer (#216), stacked as a bottom-anchored overlay like the
        // Dispatch pane. Same chord shape; inert while the Dispatch prompt is open or when no post
        // callback was supplied (a non-interactive host). The composer owns the keyboard once shown
        // (the guard at the top of this handler), so a second Ctrl+N inside it is a no-op.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.N && !_promptBox.Visible && _postCommentAsync is not null)
        {
            key.Handled = true;
            ShowCommentComposer();
            return;
        }

        // Ctrl+T opens the reply-target picker (#330), stacked as a bottom-anchored overlay like the
        // comment composer. "T" for reply-into-Thread — Ctrl+R (the Reply mnemonic) is already the Detail
        // refresh alias. Same chord shape; inert while the Dispatch prompt is open or when no reply
        // callback was supplied (a non-interactive host). Picking a target opens the composer in reply
        // mode; the picker owns the keyboard once shown (the guard at the top of this handler).
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.T && !_promptBox.Visible && _postReplyAsync is not null)
        {
            key.Handled = true;
            ShowReplyPicker();
            return;
        }

        // Ctrl+E opens the description editor (#217), stacked as a bottom-anchored overlay like the
        // comment composer. Same chord shape; inert while the Dispatch prompt is open, while a prior
        // save is still in flight (so a completing save can't force-close a freshly reopened editor and
        // lose its draft), or when no write callback was supplied (a non-interactive host). The
        // read-only panes never need Ctrl+E, so pre-empting it is safe. The editor owns the keyboard
        // once shown (the guard at the top of this handler), so a second Ctrl+E inside it is a no-op.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.E && !_promptBox.Visible && !_savingDescription && _setDescriptionAsync is not null)
        {
            key.Handled = true;
            ShowDescriptionEditor();
            return;
        }

        // Ctrl+U opens Quick Updates for this task (#159), stacked over the detail view; Esc there pops
        // back here. Same chord shape as Ctrl+A/B above and inert while the Dispatch prompt is open, so
        // it never interferes with typing a prompt or a read-only pane.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.U && !_promptBox.Visible)
        {
            key.Handled = true;
            QuickUpdatesRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Ctrl+O quick-opens another task (#353) — the same entry surface as the main list, so you can
        // jump between tasks without Esc-ing back first. Same chord shape as Ctrl+U above and inert while
        // the Dispatch prompt is open, so it never interferes with typing a prompt or a read-only pane.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.O && !_promptBox.Visible)
        {
            key.Handled = true;
            QuickOpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Ctrl+R is the (undisplayed) alias of the F5 refresh key: re-fetch this task's detail +
        // comments in every tab (#114 follow-up). Handled here (wired to every scroll target) so it
        // works from whichever tab is front-most. The bare F5 case is in the switch below.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.R)
        {
            key.Handled = true;
            RequestRefresh();
            return;
        }

        // Ctrl+PgUp = oldest-first, Ctrl+PgDn = newest-first for the Stream tab (#106); re-renders it in
        // place. Ctrl-modified so they never collide with the panes' bare PgUp/PgDn scrolling (which the
        // read-only TextView still handles because we only consume the Ctrl-modified chord here).
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.PageUp)
        {
            key.Handled = true;
            SetStreamSort(StreamSort.Ascending);
            return;
        }
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.PageDown)
        {
            key.Handled = true;
            SetStreamSort(StreamSort.Descending);
            return;
        }

        // Ctrl+→ / Ctrl+← cycle the detail tabs (#315), moving tab-switch off bare Tab/Shift+Tab so
        // those free up for in-pane link focus traversal (#319, E). Ctrl-modified so they never collide
        // with the panes' bare cursor movement; DetailTabNav keeps it inert while the Dispatch prompt is
        // open (its dir-browser owns bare ←/→ and its fields own cursor movement) — the same reason the
        // open Dispatch pane used to consume Tab before it reached the tab cycle.
        var tabNav = DetailTabNav.Route(ClassifyTabNav(key), _promptBox.Visible);
        if (tabNav != DetailTabNav.NavAction.None)
        {
            key.Handled = true;
            CycleTab(forward: tabNav == DetailTabNav.NavAction.CycleForward);
            return;
        }

        switch (key.KeyCode)
        {
            case KeyCode.F5:
                key.Handled = true;
                RequestRefresh();
                break;
            case KeyCode.F1:
                key.Handled = true;
                RequestHelp();
                break;
            case KeyCode.Esc:
                key.Handled = true;
                Close();
                break;
        }
    }

    /// <summary>Handles keys while a Dispatch-pane control has focus. The pure <see cref="DispatchPaneModel"/>
    /// decides the action; keys it doesn't claim (<see cref="DispatchPaneModel.PaneAction.PassThrough"/>)
    /// fall through so typing into a field and Space-toggling a check box keep working.</summary>
    private void OnDispatchKey(object? sender, Key key)
    {
        var action = DispatchPaneModel.Route(Classify(key));
        if (action == DispatchPaneModel.PaneAction.PassThrough)
            return;

        key.Handled = true;
        switch (action)
        {
            case DispatchPaneModel.PaneAction.Submit:
                SubmitDispatch();
                break;
            case DispatchPaneModel.PaneAction.Cancel:
                HidePrompt();
                break;
            case DispatchPaneModel.PaneAction.FocusNext:
                MoveDispatchFocus(forward: true);
                break;
            case DispatchPaneModel.PaneAction.FocusPrevious:
                MoveDispatchFocus(forward: false);
                break;
            case DispatchPaneModel.PaneAction.ScrollUnderlyingPageUp:
                ScrollActiveTab(Command.PageUp);
                break;
            case DispatchPaneModel.PaneAction.ScrollUnderlyingPageDown:
                ScrollActiveTab(Command.PageDown);
                break;
        }
    }

    /// <summary>Classifies a Terminal.Gui key into <see cref="DetailTabNav"/>'s chord vocabulary:
    /// <c>Ctrl+→</c>/<c>Ctrl+←</c> cycle tabs (#315), everything else falls through.</summary>
    private static DetailTabNav.NavKey ClassifyTabNav(Key key)
    {
        if (!key.IsCtrl)
            return DetailTabNav.NavKey.Other;
        return (key.KeyCode & ~KeyCode.CtrlMask) switch
        {
            KeyCode.CursorRight => DetailTabNav.NavKey.CtrlRight,
            KeyCode.CursorLeft => DetailTabNav.NavKey.CtrlLeft,
            _ => DetailTabNav.NavKey.Other,
        };
    }

    /// <summary>Classifies a Terminal.Gui key into the pane's key vocabulary. Shift+Tab arrives as a
    /// bare <c>Tab</c> with <see cref="Key.IsShift"/> set (mirrors <see cref="CycleTab"/>).</summary>
    private static DispatchPaneModel.PaneKey Classify(Key key) => key.KeyCode switch
    {
        KeyCode.Enter => DispatchPaneModel.PaneKey.Enter,
        KeyCode.Esc => DispatchPaneModel.PaneKey.Escape,
        KeyCode.Tab => key.IsShift ? DispatchPaneModel.PaneKey.BackTab : DispatchPaneModel.PaneKey.Tab,
        KeyCode.PageUp => DispatchPaneModel.PaneKey.PageUp,
        KeyCode.PageDown => DispatchPaneModel.PaneKey.PageDown,
        _ => DispatchPaneModel.PaneKey.Other,
    };

    /// <summary>
    /// Handles keys while the working-dir file-tree browser (#95) has focus. Enter confirms the
    /// highlighted directory (already mirrored into the field by the selection-follows-cursor sync) and
    /// advances focus, → descends into it, ← / a "confirm" on ".." goes up; everything else (↑/↓ list
    /// navigation, Tab, Esc, PgUp/PgDn) routes through the same <see cref="DispatchPaneModel"/> path as
    /// the other controls — the ↑/↓ that move the highlight are what trigger the field sync. Intercepting
    /// Enter here keeps it from submitting the dispatch while browsing.
    /// </summary>
    private void OnBrowserKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Enter:
                key.Handled = true;
                SelectBrowserEntry();
                break;
            case KeyCode.CursorRight:
                key.Handled = true;
                DescendBrowserEntry();
                break;
            case KeyCode.CursorLeft:
                key.Handled = true;
                NavigateBrowserUp();
                break;
            default:
                // Tab/Esc/PgUp/PgDn and pass-through keys (↑/↓ list navigation) behave as elsewhere.
                OnDispatchKey(sender, key);
                break;
        }
    }

    /// <summary>The highlighted browser row (0 = ".."), clamped to a valid index.</summary>
    private int SelectedBrowserIndex() => _dirBrowser.SelectedItem is int i && i >= 0 ? i : 0;

    /// <summary>
    /// Selection-follows-cursor: whenever the browser's highlight moves, mirror the highlighted
    /// directory into the working-dir field so it — not a stale field value — is what dispatches. A
    /// highlighted subdirectory is an explicit pick; the ".." row resolves via
    /// <see cref="DirectoryBrowserModel.SelectionPathAt"/> to the directory being browsed, or to blank
    /// at the root (so grazing the list doesn't turn the configured default dir into an explicit pick
    /// and drop task-derived per-task output #98). Suppressed while the pane is (re)opening so the
    /// pre-filled per-task cached dir (#96) survives the browser's reset.
    /// </summary>
    private void OnBrowserSelectionChanged(object? sender, ValueChangedEventArgs<int?> e)
    {
        if (_suppressWorkingDirSync)
            return;
        _workingDirField.Text = _browser.SelectionPathAt(e.NewValue is int i && i >= 0 ? i : 0);
    }

    /// <summary>
    /// Refreshes the ListView from the model's current listing. Highlights <paramref name="selectEntry"/>
    /// if present (so going up lands on the directory you came out of), else the first row ("..").
    /// </summary>
    private void RefreshBrowser(string? selectEntry = null)
    {
        _dirBrowser.SetSource(new ObservableCollection<string>(_browser.Entries));
        var index = 0;
        if (selectEntry is { Length: > 0 })
        {
            for (var i = 0; i < _browser.Entries.Count; i++)
            {
                if (string.Equals(_browser.Entries[i], selectEntry, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
        }
        _dirBrowser.SelectedItem = index;
    }

    /// <summary>Goes up one level and highlights the directory we came out of (rather than "..").</summary>
    private void NavigateBrowserUp()
    {
        var leaving = Path.GetFileName(_browser.CurrentDirectory);
        _browser.NavigateUp();
        RefreshBrowser(selectEntry: leaving);
    }

    /// <summary>Enter: confirm the highlighted directory into the field and advance focus; ".." goes up.
    /// The field already tracks the highlight (selection-follows-cursor); the write here keeps Enter
    /// correct even if the highlight was never moved (so the field was never synced).</summary>
    private void SelectBrowserEntry()
    {
        var index = SelectedBrowserIndex();
        if (_browser.IsParent(index))
        {
            NavigateBrowserUp();
            return;
        }
        _workingDirField.Text = _browser.PathAt(index);
        MoveDispatchFocus(forward: true);
    }

    /// <summary>→: descend into the highlighted directory (or up, for "..") to browse deeper.</summary>
    private void DescendBrowserEntry()
    {
        var index = SelectedBrowserIndex();
        if (_browser.IsParent(index))
        {
            NavigateBrowserUp();
            return;
        }
        _browser.Descend(index);
        RefreshBrowser();
    }

    /// <summary>Submits the pane: hides it, then (only for non-empty text) raises the dispatch event
    /// carrying the prompt, the one-off/interactive session mode (#94), the chosen working directory
    /// (#95; blank ⇒ null ⇒ default dir), the post-to-Comments flag (#97), and the launch-location
    /// override (#275; only honoured for an interactive session — the host ignores it for one-off).</summary>
    private void SubmitDispatch()
    {
        var text = _promptField.Text?.ToString() ?? string.Empty;
        var sessionMode = _oneOffToggle.Value == CheckState.Checked
            ? AgentSessionMode.OneOff
            : AgentSessionMode.Interactive;
        var dir = _workingDirField.Text?.ToString();
        var postToComments = _postToCommentsToggle.Value == CheckState.Checked;
        var launchLocation = DispatchPaneModel.ToLaunchLocation(_launchLocationToggle.Value == CheckState.Checked);
        HidePrompt();
        // A stray Enter shouldn't launch a session — only dispatch when something was typed.
        if (!string.IsNullOrWhiteSpace(text))
            AgentDispatchRequested?.Invoke(this, new DispatchRequest(text, sessionMode, dir, postToComments, launchLocation));
    }

    /// <summary>Greys the launch-location toggle (#275) in/out to match the session mode: a one-off
    /// <c>claude -p</c> run has no terminal, so new-window-vs-new-tab is meaningless there. Disabled
    /// controls are also skipped by <see cref="MoveDispatchFocus"/> so Tab still cycles cleanly.</summary>
    private void UpdateLaunchLocationEnabled()
    {
        var sessionMode = _oneOffToggle.Value == CheckState.Checked
            ? AgentSessionMode.OneOff
            : AgentSessionMode.Interactive;
        _launchLocationToggle.Enabled = DispatchPaneModel.LaunchLocationApplies(sessionMode);
    }

    /// <summary>Moves focus to the next/previous dispatch control, wrapping at both ends and skipping
    /// any control that can't currently take focus — e.g. the launch-location toggle greyed out in
    /// one-off mode (#275) — so Tab never lands on (or stalls at) a disabled control.</summary>
    private void MoveDispatchFocus(bool forward)
    {
        var current = Array.FindIndex(_dispatchControls, static c => c.HasFocus);
        if (current < 0)
            current = 0;
        var next = current;
        for (var i = 0; i < _dispatchControls.Length; i++)
        {
            next = DispatchPaneModel.NextFocus(next, _dispatchControls.Length, forward);
            if (_dispatchControls[next].Enabled && _dispatchControls[next].CanFocus)
                break;
        }
        _dispatchControls[next].SetFocus();
    }

    /// <summary>Scrolls the front-most tab's body while the pane holds keyboard focus (PgUp/PgDn pass
    /// through to it rather than being trapped in the pane), so the user can review it while composing.</summary>
    private void ScrollActiveTab(Command command)
    {
        var current = Array.IndexOf(_tabContents, _tabs.Value);
        if (current < 0)
            current = 0;
        // A text pane pages via the shared viewport model (#468), so the composer's "scroll underlying"
        // (PgUp/PgDn) uses the same explicit scroll state as the reading-path ↑/↓ and PgUp/PgDn rather than
        // Terminal.Gui's stock TextView paging; the Task Tree ListView keeps its stock command
        // (page-selection). Any other command still routes straight through.
        if (_scrollTargets[current] is TextView
            && (command == Command.PageUp || command == Command.PageDown))
            PageActiveTextPane(command == Command.PageUp ? -1 : 1);
        else
            _scrollTargets[current].InvokeCommand(command);
    }

    /// <summary>Moves the front-most tab by <paramref name="delta"/> rows for a bare ↑/↓ (#452): a
    /// text pane (or the Other tab's fields body) scrolls one line via its viewport; the Task Tree
    /// <see cref="ListView"/> moves its selection one row (its setter calls
    /// <c>EnsureSelectedItemVisible</c>, so the list scrolls to follow). The pure
    /// <see cref="DetailScrollModel"/> clamps to the content edges, so at a boundary this is a no-op.</summary>
    private void MoveActiveTab(int delta)
    {
        var current = Array.IndexOf(_tabContents, _tabs.Value);
        if (current < 0)
            current = 0;
        switch (_scrollTargets[current])
        {
            case ListView list:
                var count = list.Source?.Count ?? 0;
                var selected = list.SelectedItem is int i && i >= 0 ? i : 0;
                list.SelectedItem = DetailScrollModel.NextIndex(selected, count, delta);
                break;
            case TextView pane:
                var vp = pane.Viewport;
                var top = DetailScrollModel.NextTop(vp.Y, vp.Height, pane.Lines, delta);
                pane.Viewport = new Rectangle(vp.X, top, vp.Width, vp.Height);
                break;
        }
    }

    /// <summary>Pages the front-most tab's text pane one viewport page in <paramref name="direction"/>
    /// (−1 up, +1 down) for a bare PgUp/PgDn (#468) — the page counterpart of <see cref="MoveActiveTab"/>'s
    /// one-line ↑/↓ branch, a viewport write on the same <c>viewport.Y</c> so the two gestures share one
    /// explicit scroll state independent of Terminal.Gui's stock paging. The pure
    /// <see cref="DetailScrollModel"/> supplies the page size (<see cref="DetailScrollModel.PageDelta"/>)
    /// and clamps to the content edges, so a page at a boundary is a no-op. Returns <see langword="false"/>
    /// when the front-most tab is not a text pane (the Task Tree <see cref="ListView"/>), so the caller
    /// leaves the key to that list's stock page-selection.</summary>
    private bool PageActiveTextPane(int direction)
    {
        var current = Array.IndexOf(_tabContents, _tabs.Value);
        if (current < 0)
            current = 0;
        if (_scrollTargets[current] is not TextView pane)
            return false;
        var vp = pane.Viewport;
        var delta = direction * DetailScrollModel.PageDelta(vp.Height);
        var top = DetailScrollModel.NextTop(vp.Y, vp.Height, pane.Lines, delta);
        pane.Viewport = new Rectangle(vp.X, top, vp.Width, vp.Height);
        return true;
    }

    private void ShowPrompt()
    {
        if (_promptBox.Visible)
            return;
        _promptField.Text = string.Empty;
        // Pre-fill the working dir from the per-task cache (#96) — the last explicit dir dispatched
        // from this task, or blank (⇒ default dir #98) if none — read live so a dispatch earlier in
        // this same open detail screen is reflected on reopen. Reset the browser to its root (the base
        // working dir #92); pre-fill is independent of browser navigation.
        // Guard the selection-follows-cursor sync: resetting the browser fires ValueChanged, which
        // would otherwise overwrite the pre-fill with the browser root before the user touches it.
        _suppressWorkingDirSync = true;
        try
        {
            _workingDirField.Text = _workingDirectoryPreFill?.Invoke() ?? string.Empty;
            _browser.Reset();
            RefreshBrowser();
        }
        finally
        {
            _suppressWorkingDirSync = false;
        }
        // Size the pane to the current tab body so it degrades gracefully on short terminals: the
        // prompt row + borders always survive; the bottom controls (browser, post-to-Comments) clip first.
        var height = DispatchPaneModel.ClampHeight(
            DispatchPaneModel.PreferredHeightWithBrowser(
                DispatchRowsAboveBrowser, DispatchBrowserRows, DispatchRowsBelowBrowser),
            Viewport.Height, minTabRows: 3);
        _promptBox.Height = height;
        _promptBox.Y = Pos.AnchorEnd(height);
        _promptBox.Visible = true;
        _promptField.SetFocus();
    }

    private void HidePrompt()
    {
        if (!_promptBox.Visible)
            return;
        _promptBox.Visible = false;
        FocusCurrentPane();
    }

    /// <summary>Returns focus to the front-most tab's scroll target (after the prompt box closes).</summary>
    private void FocusCurrentPane()
    {
        var current = Array.IndexOf(_tabContents, _tabs.Value);
        if (current < 0)
            current = 0;
        _scrollTargets[current].SetFocus();
    }

    /// <summary>The front-most tab's scroll target when it is a read-only text pane (Stream / Description /
    /// Comments), or <c>null</c> for the Task Tree / Other tabs. The seam for the #319 link focus keys, so
    /// bare Tab/Enter act only where there are links to traverse.</summary>
    private DetailPaneView? ActiveTextPane()
    {
        var current = Array.IndexOf(_tabContents, _tabs.Value);
        return current < 0 ? null : _scrollTargets[current] as DetailPaneView;
    }

    // ── Comment composer (#216) ───────────────────────────────────────────────

    /// <summary>Handles keys while a comment-composer control has focus. Tab/Shift+Tab cycle the
    /// composer's controls (glue, like the Dispatch pane); the pure <see cref="CommentComposerModel"/>
    /// decides the rest, and keys it doesn't claim (<see cref="CommentComposerModel.ComposerAction.PassThrough"/>)
    /// fall through so multi-line typing (incl. Enter → newline) and the buttons keep working.</summary>
    private void OnCommentKey(object? sender, Key key)
    {
        // Tab / Shift+Tab move between the editor and the Post/Cancel buttons, so the editor's
        // TabKeyAddsTab=false doesn't just swallow Tab. Shift+Tab arrives as a bare Tab with IsShift.
        if (key.KeyCode == KeyCode.Tab)
        {
            key.Handled = true;
            MoveCommentFocus(forward: !key.IsShift);
            return;
        }

        // F1 opens Help even while composing (the #216 criterion: the editor must not swallow F1).
        // Handled here because the top-of-OnKey composer guard would otherwise eat it; the draft stays
        // intact under the stacked Help screen. Esc is handled below as cancel.
        if (key.KeyCode == KeyCode.F1)
        {
            key.Handled = true;
            RequestHelp();
            return;
        }

        var action = CommentComposerModel.Route(ClassifyComposer(key));
        if (action == CommentComposerModel.ComposerAction.PassThrough)
            return;

        key.Handled = true;
        switch (action)
        {
            case CommentComposerModel.ComposerAction.Post:
                PostComment();
                break;
            case CommentComposerModel.ComposerAction.Cancel:
                HideCommentComposer();
                break;
        }
    }

    /// <summary>Classifies a Terminal.Gui key into the composer's vocabulary. Ctrl+Enter submits (a
    /// best-effort shortcut alongside the default Post button — on drivers that fold it into a bare
    /// Enter it just inserts a newline, which is harmless); Esc cancels; everything else passes through
    /// so typing and Enter-newline in the multi-line editor are undisturbed.</summary>
    private static CommentComposerModel.ComposerKey ClassifyComposer(Key key)
    {
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Enter)
            return CommentComposerModel.ComposerKey.Submit;
        if (key.KeyCode == KeyCode.Esc)
            return CommentComposerModel.ComposerKey.Cancel;
        return CommentComposerModel.ComposerKey.Other;
    }

    /// <summary>Moves focus to the next/previous composer control, wrapping at both ends (reuses the
    /// Dispatch pane's wraparound cycle).</summary>
    private void MoveCommentFocus(bool forward)
    {
        var current = Array.FindIndex(_commentControls, static c => c.HasFocus);
        if (current < 0)
            current = 0;
        _commentControls[DispatchPaneModel.NextFocus(current, _commentControls.Length, forward)].SetFocus();
    }

    /// <summary>Opens the comment composer: clears the editor, sizes the pane to the terminal (the
    /// editor rows clip before the button row on a very short window, reusing the Dispatch pane's
    /// clamp), shows it and focuses the editor. In <b>reply mode</b> (<paramref name="replyTo"/> set,
    /// #330) it stashes the target comment id and retitles the frame "Reply to &lt;author&gt;"; a plain
    /// open clears reply mode and titles it "New comment".</summary>
    private void ShowCommentComposer(CommentReplyModel.ReplyTarget? replyTo = null)
    {
        if (_commentBox.Visible)
            return;
        _replyToCommentId = replyTo?.CommentId;
        _commentBox.Title = replyTo is { } t
            ? $"Reply to {t.Author} — Ctrl+Enter or Tab→Post · Esc cancel"
            : "New comment — Ctrl+Enter or Tab→Post · Esc cancel";
        _commentEditor.Text = string.Empty;
        var height = DispatchPaneModel.ClampHeight(CommentComposerPreferredHeight, Viewport.Height, minTabRows: 3);
        _commentBox.Height = height;
        _commentBox.Y = Pos.AnchorEnd(height);
        _commentBox.Visible = true;
        _commentEditor.SetFocus();
    }

    /// <summary>Closes the composer and returns focus to the front-most tab (mirrors HidePrompt). Clears
    /// reply mode so a later plain Ctrl+N isn't stuck targeting a comment.</summary>
    private void HideCommentComposer()
    {
        if (!_commentBox.Visible)
            return;
        _commentBox.Visible = false;
        _replyToCommentId = null;
        FocusCurrentPane();
    }

    /// <summary>
    /// Posts the composed comment (#216): an empty/whitespace body just closes the composer (a no-op —
    /// ClickUp rejects an empty <c>comment_text</c>). Otherwise it optimistically appends a provisional
    /// comment, closes the composer, and writes off the UI thread via the injected callback —
    /// reconciling the provisional to the server-confirmed comment on success or reverting it on
    /// failure, the same optimistic/revert discipline the Quick Updates status/priority paths use.
    /// A background refresh that lands mid-post can drop the provisional before reconcile finds it; the
    /// reconcile is then a no-op and the next refresh re-pulls the real posted comment (self-healing).
    /// </summary>
    private void PostComment()
    {
        var raw = _commentEditor.Text?.ToString();
        // Capture the reply target before HideCommentComposer clears it. Reply mode needs the reply
        // callback; the top-level path needs the comment callback — bail (just close) if the required one
        // is absent or the body is empty.
        var replyTo = _replyToCommentId;
        var required = replyTo is null ? _postCommentAsync is not null : _postReplyAsync is not null;
        if (!CommentComposerModel.IsPostable(raw) || !required)
        {
            HideCommentComposer();
            return;
        }
        var text = CommentComposerModel.Normalize(raw);
        HideCommentComposer();

        // A client sentinel id (so reconcile/revert can find the optimistic entry) and a client "now"
        // stamp (so it sorts as the newest entry). Re-render via UpdateData, which recomputes the
        // Stream/Comments panes in place with scroll preservation.
        var pendingId = $"{CommentComposerModel.PendingIdPrefix}{++_pendingCommentSeq}";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (replyTo is not null)
        {
            PostReply(replyTo, pendingId, text, nowMs);
            return;
        }

        // Top-level comment (#216): optimistic append + reconcile/revert.
        var provisional = CommentComposerModel.Provisional(pendingId, text, nowMs);
        UpdateData(_task, CommentComposerModel.Append(_comments, provisional));
        RequestFlash("Posting comment…");

        // Fully-qualified: this screen exposes a `Task` property (the shown TaskDetail), which would
        // otherwise shadow System.Threading.Tasks.Task in this expression.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var confirmed = await _postCommentAsync!(text, CancellationToken.None).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    if (_disposed)
                        return; // the detail screen was closed mid-post — don't touch torn-down views
                    UpdateData(_task, CommentComposerModel.Reconcile(_comments, pendingId, confirmed));
                    RequestFlash("Comment posted.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (_disposed)
                        return;
                    UpdateData(_task, CommentComposerModel.Revert(_comments, pendingId));
                    RequestFlash($"Could not post comment: {ShortError(ex)}");
                });
            }
        });
    }

    /// <summary>Posts a reply into <paramref name="parentCommentId"/>'s thread (#330): optimistically
    /// nests a provisional reply under its parent (per #329), writes off the UI thread via
    /// <see cref="_postReplyAsync"/>, and reconciles the provisional to the server-confirmed reply on
    /// success or reverts it on failure — the same optimistic/revert discipline as the top-level path. A
    /// background refresh that lands mid-post can drop the parent before reconcile finds it; the transform
    /// is then a no-op and the next refresh re-pulls the real reply (self-healing).</summary>
    private void PostReply(string parentCommentId, string pendingId, string text, long nowMs)
    {
        var provisional = CommentComposerModel.ProvisionalReply(pendingId, text, nowMs, parentCommentId, _task.Id);
        UpdateData(_task, CommentComposerModel.AppendReply(_comments, parentCommentId, provisional));
        RequestFlash("Posting reply…");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var confirmed = await _postReplyAsync!(parentCommentId, text, CancellationToken.None).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    if (_disposed)
                        return;
                    UpdateData(_task, CommentComposerModel.ReconcileReply(_comments, parentCommentId, pendingId, confirmed));
                    RequestFlash("Reply posted.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (_disposed)
                        return;
                    UpdateData(_task, CommentComposerModel.RevertReply(_comments, parentCommentId, pendingId));
                    RequestFlash($"Could not post reply: {ShortError(ex)}");
                });
            }
        });
    }

    /// <summary>Opens the reply-target picker (#330): projects the current comments into pick rows
    /// (top-level, non-pending, newest-first), or flashes and no-ops when there are none. Sizes the
    /// overlay to the terminal (reusing the Dispatch clamp), shows it and focuses the list.</summary>
    private void ShowReplyPicker()
    {
        if (_replyPickerBox.Visible || _postReplyAsync is null)
            return;
        _replyTargets = CommentReplyModel.Targets(_comments);
        if (_replyTargets.Count == 0)
        {
            RequestFlash("No comments to reply to.");
            return;
        }
        _replyPicker.SetSource(new ObservableCollection<string>(_replyTargets.Select(t => t.Label)));
        _replyPicker.SelectedItem = 0;
        var height = DispatchPaneModel.ClampHeight(ReplyPickerPreferredHeight, Viewport.Height, minTabRows: 3);
        _replyPickerBox.Height = height;
        _replyPickerBox.Y = Pos.AnchorEnd(height);
        _replyPickerBox.Visible = true;
        _replyPicker.SetFocus();
    }

    /// <summary>Closes the reply picker and returns focus to the front-most tab (mirrors HideCommentComposer).</summary>
    private void HideReplyPicker()
    {
        if (!_replyPickerBox.Visible)
            return;
        _replyPickerBox.Visible = false;
        _replyTargets = []; // drop the snapshot's CommentItem references; ShowReplyPicker rebuilds it
        FocusCurrentPane();
    }

    /// <summary>Picks the highlighted reply target: closes the picker and opens the composer in reply mode
    /// for that comment. Idempotent (a stray double-fire from key + mouse can't double-open) via the
    /// picker-visible guard in <see cref="ShowCommentComposer"/>/here.</summary>
    private void PickReplyTarget()
    {
        if (!_replyPickerBox.Visible)
            return;
        var index = _replyPicker.SelectedItem ?? -1;
        if (index < 0 || index >= _replyTargets.Count)
            return;
        var target = _replyTargets[index];
        HideReplyPicker();
        ShowCommentComposer(target);
    }

    /// <summary>Reply-picker keys: Enter picks the highlighted comment, Esc cancels; ↑/↓ are the list's
    /// own. Everything else falls through.</summary>
    private void OnReplyPickerKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Enter:
                key.Handled = true;
                PickReplyTarget();
                break;
            case KeyCode.Esc:
                key.Handled = true;
                HideReplyPicker();
                break;
        }
    }

    /// <summary>A left-click on a reply-picker row selects and picks it (the mouse path, #283), mirroring
    /// the shared selector's row hit-test (offset by the list's scroll).</summary>
    private void OnReplyPickerMouse(object? sender, Mouse e)
    {
        if (!e.Flags.HasFlag(MouseFlags.LeftButtonClicked) || e.Position is not { Y: >= 0 } pos)
            return;
        var row = _replyPicker.Viewport.Y + pos.Y;
        if (row < 0 || row >= _replyTargets.Count)
            return;
        e.Handled = true;
        _replyPicker.SelectedItem = row;
        PickReplyTarget();
    }

    /// <summary>A one-line, length-capped rendering of an exception for the status flash.</summary>
    private static string ShortError(Exception ex)
    {
        var msg = ex.Message.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return msg.Length > 80 ? msg[..79] + "…" : msg;
    }

    // ── Description editor (#217) ──────────────────────────────────────────────

    /// <summary>Handles keys while a description-editor control has focus. A pending unsaved-changes
    /// discard confirm (armed by Esc-on-dirty) is answered first; then Tab/Shift+Tab cycle the editor
    /// and buttons, and F1 opens Help — both would otherwise be swallowed by the editor / the
    /// top-of-OnKey overlay guard. The pure <see cref="DescriptionEditorModel"/> decides the rest, and
    /// keys it doesn't claim (<see cref="DescriptionEditorModel.EditorAction.PassThrough"/>) fall through
    /// so multi-line typing (incl. Enter → newline) keeps working.</summary>
    private void OnDescriptionKey(object? sender, Key key)
    {
        // While a discard is pending, the next keystroke answers the Y/N: only Y discards the edits and
        // closes; anything else (incl. Esc/N) dismisses the confirm and returns to editing (draft kept).
        if (_descriptionPendingDiscard)
        {
            key.Handled = true;
            _descriptionPendingDiscard = false;
            _descriptionConfirm.Text = "";
            if ((key.KeyCode & ~KeyCode.ShiftMask) == KeyCode.Y)
                HideDescriptionEditor();
            return;
        }

        // Tab / Shift+Tab move between the editor and the Save/Cancel buttons, so the editor's
        // TabKeyAddsTab=false doesn't just swallow Tab. Shift+Tab arrives as a bare Tab with IsShift.
        if (key.KeyCode == KeyCode.Tab)
        {
            key.Handled = true;
            MoveDescriptionFocus(forward: !key.IsShift);
            return;
        }

        // F1 opens Help even while editing (mirrors the composer): handled here because the top-of-OnKey
        // overlay guard would otherwise eat it; the draft stays intact under the stacked Help screen.
        if (key.KeyCode == KeyCode.F1)
        {
            key.Handled = true;
            RequestHelp();
            return;
        }

        var action = DescriptionEditorModel.Route(ClassifyDescription(key));
        if (action == DescriptionEditorModel.EditorAction.PassThrough)
            return;

        key.Handled = true;
        switch (action)
        {
            case DescriptionEditorModel.EditorAction.Save:
                SaveDescription();
                break;
            case DescriptionEditorModel.EditorAction.Cancel:
                CancelDescriptionEditor();
                break;
        }
    }

    /// <summary>Classifies a key into the editor's vocabulary. Ctrl+Enter saves (a best-effort shortcut
    /// alongside the default Save button — on drivers that fold it into a bare Enter it just inserts a
    /// newline, which is harmless); Esc cancels; everything else passes through so typing and
    /// Enter-newline in the multi-line editor are undisturbed.</summary>
    private static DescriptionEditorModel.EditorKey ClassifyDescription(Key key)
    {
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Enter)
            return DescriptionEditorModel.EditorKey.Save;
        if (key.KeyCode == KeyCode.Esc)
            return DescriptionEditorModel.EditorKey.Cancel;
        return DescriptionEditorModel.EditorKey.Other;
    }

    /// <summary>Moves focus to the next/previous editor control, wrapping at both ends (reuses the
    /// Dispatch pane's wraparound cycle).</summary>
    private void MoveDescriptionFocus(bool forward)
    {
        var current = Array.FindIndex(_descriptionControls, static c => c.HasFocus);
        if (current < 0)
            current = 0;
        _descriptionControls[DispatchPaneModel.NextFocus(current, _descriptionControls.Length, forward)].SetFocus();
    }

    /// <summary>Opens the description editor: seeds (pre-fills) it with the current description, sizes
    /// the pane to the terminal (the editor rows clip before the button row on a very short window,
    /// reusing the Dispatch pane's clamp), shows it and focuses the editor.</summary>
    private void ShowDescriptionEditor()
    {
        // Don't reopen over an in-flight save: its continuation would close this editor and discard the
        // draft. The Ctrl+E opener guards this too; this is defence in depth for any future caller.
        if (_descriptionBox.Visible || _savingDescription)
            return;
        _descriptionEditor.Text = DescriptionEditorModel.Seed(_task.Description);
        _descriptionPendingDiscard = false;
        _descriptionConfirm.Text = "";
        var height = DispatchPaneModel.ClampHeight(DescriptionEditorPreferredHeight, Viewport.Height, minTabRows: 3);
        _descriptionBox.Height = height;
        _descriptionBox.Y = Pos.AnchorEnd(height);
        _descriptionBox.Visible = true;
        _descriptionEditor.SetFocus();
    }

    /// <summary>Closes the editor and returns focus to the front-most tab (mirrors HidePrompt), clearing
    /// any pending discard confirm.</summary>
    private void HideDescriptionEditor()
    {
        if (!_descriptionBox.Visible)
            return;
        _descriptionBox.Visible = false;
        _descriptionPendingDiscard = false;
        _descriptionConfirm.Text = "";
        FocusCurrentPane();
    }

    /// <summary>Esc / Cancel: closes immediately when there are no unsaved edits, otherwise arms the
    /// inline discard confirm (the next key answers Y/N) rather than losing the draft silently.</summary>
    private void CancelDescriptionEditor()
    {
        if (!DescriptionEditorModel.IsDirty(_task.Description, _descriptionEditor.Text?.ToString()))
        {
            HideDescriptionEditor();
            return;
        }
        _descriptionPendingDiscard = true;
        _descriptionConfirm.Text = "Discard unsaved changes to the description? (Y / N)";
    }

    /// <summary>
    /// Saves the edited description (#217). An unchanged editor just closes (no needless write). A real
    /// change is written off the UI thread via the injected callback; on success the detail reflects the
    /// server-confirmed text in place (via <see cref="UpdateData"/>, scroll/cursor preserved) and the
    /// editor closes, on failure the editor stays open with the draft intact and the error is flashed. A
    /// second Save while a write is in flight is ignored. The <c>_disposed</c> guard mirrors the
    /// comment-post path so a save that completes after the screen closed doesn't touch torn-down views.
    /// </summary>
    private void SaveDescription()
    {
        if (_setDescriptionAsync is null)
            return;
        if (_savingDescription)
        {
            // A save is already in flight; ignore the re-press but acknowledge it rather than silently
            // no-op'ing, so a user mashing Save/Ctrl+Enter gets feedback.
            RequestFlash("Still saving…");
            return;
        }
        var raw = _descriptionEditor.Text?.ToString();
        if (!DescriptionEditorModel.IsDirty(_task.Description, raw))
        {
            HideDescriptionEditor();
            return;
        }
        var text = DescriptionEditorModel.Normalize(raw);
        _savingDescription = true;
        RequestFlash("Saving description…");

        // Fully-qualified: this screen exposes a `Task` property (the shown TaskDetail), which would
        // otherwise shadow System.Threading.Tasks.Task in this expression.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var confirmed = await _setDescriptionAsync(text, CancellationToken.None).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    _savingDescription = false;
                    if (_disposed)
                        return; // the detail screen was closed mid-save — don't touch torn-down views
                    // Prefer the server-confirmed value, but fall back to what we sent if the PUT
                    // response omitted the description text (a partial response would otherwise blank a
                    // just-saved non-empty body until the next refresh). Safe for the clear case too:
                    // there `text` is "", which renders "(no description)" exactly as a null would.
                    UpdateData(_task with { Description = confirmed ?? text }, _comments);
                    HideDescriptionEditor();
                    RequestFlash("Description saved.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    _savingDescription = false;
                    if (_disposed)
                        return;
                    // Keep the editor open with the draft intact so the user can retry or copy it out.
                    RequestFlash($"Could not save description: {ShortError(ex)}");
                });
            }
        });
    }

    /// <summary>Sets the activity sort direction and re-renders <em>both</em> the Stream and Comments
    /// bodies in place (#106), so the one order applies to both tabs regardless of which is currently
    /// shown. No-op if unchanged. Re-arms the Stream auto-scroll edge (#107) so, e.g., "scroll to newest"
    /// keeps landing on the newest entry after the sort flips which end of the body that is (applied now
    /// if Stream is front-most, else deferred to the next time it's shown). The Comments pane re-renders
    /// from its top (<see cref="DetailPaneView.SetBody"/> homes the caret) — which also makes re-rendering
    /// a non-front-most pane safe, since a stale caret would otherwise index past the reordered content.</summary>
    private void SetStreamSort(StreamSort sort)
    {
        if (_streamSort == sort)
            return;
        _streamSort = sort;
        // Keep _streamText/_commentsText in sync so a later refresh's change-detection doesn't re-render
        // redundantly. Both panes reflect the one order; the Comments tab re-renders from its top.
        _streamText = TaskDetailFormatter.Stream(_task, _comments, _streamSort);
        _streamPane.SetBody(_streamText, TaskDetailFormatter.CommentSeparator);
        _commentsText = TaskDetailFormatter.Comments(_comments, _streamSort);
        _commentsPane.SetBody(_commentsText, TaskDetailFormatter.CommentSeparator);
        _streamAutoScrollPending = true;
        FlushStreamAutoScrollIfActive();
    }

    /// <summary>Applies a pending auto-scroll (#107) to the Stream pane, but only when it is the
    /// front-most tab — its viewport must be laid out for <c>MoveEnd()</c>/<c>MoveHome()</c> to take. A
    /// no-op otherwise; the next time Stream is shown (OnShown or CycleTab) flushes it. The scroll is
    /// posted via <see cref="Application.Invoke"/> so it runs after the framework has laid the pane out
    /// following a tab switch (a synchronous move right after <c>_tabs.Value = …</c> lands against a
    /// stale viewport). The pure <see cref="DetailScrollModel"/> resolves the content-relative
    /// preference + current sort to a concrete edge; the viewport move is the (untestable) TG glue.</summary>
    private void FlushStreamAutoScrollIfActive()
    {
        if (!_streamAutoScrollPending || !ReferenceEquals(_tabs.Value, _streamPane))
            return;
        _streamAutoScrollPending = false;
        Application.Invoke(() =>
        {
            switch (DetailScrollModel.ResolveEdge(_streamAutoScroll, _streamSort))
            {
                case DetailScrollModel.Edge.Bottom:
                    _streamPane.MoveEnd();
                    break;
                default:
                    _streamPane.MoveHome();
                    break;
            }
        });
    }

    /// <summary>Advances the selected tab and moves focus into its scroll target so ↑/↓ scroll it.</summary>
    private void CycleTab(bool forward)
    {
        var current = Array.IndexOf(_tabContents, _tabs.Value);
        if (current < 0)
            current = 0;
        var next = DetailTabNav.NextTab(current, _tabContents.Length, forward);
        _tabs.Value = _tabContents[next];
        _scrollTargets[next].SetFocus();
        // If the Stream tab wasn't the default, its auto-scroll (#107) was deferred until it's shown —
        // apply it now that its viewport is laid out.
        FlushStreamAutoScrollIfActive();
        // Lazy-load the Task Tree tab (#291) the first time it becomes front-most.
        EnsureTreeLoaded();
    }

    // ── Task Tree tab (#291) ───────────────────────────────────────────────────

    /// <summary>Kicks off the one-time, off-thread tree fetch the first time the Task Tree tab becomes
    /// front-most; the placeholder "Loading task tree…" shows until it lands. A failure renders as a
    /// single message row rather than an empty tab. Guarded so it fires at most once and only for the
    /// tree tab.</summary>
    private void EnsureTreeLoaded()
    {
        if (_treeList is null || _treeLoaded || _loadTaskTreeAsync is null)
            return;
        if (!ReferenceEquals(_tabs.Value, _treeList))
            return;
        _treeLoaded = true;
        var loader = _loadTaskTreeAsync;
        // Fully-qualified: this screen exposes a `Task` property (the shown TaskDetail), which would
        // otherwise shadow System.Threading.Tasks.Task here (mirrors the comment/description posts).
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var rows = await loader(CancellationToken.None).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    if (_disposed)
                        return;
                    PopulateTree(rows);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (_disposed)
                        return;
                    _treeList!.SetSource(new ObservableCollection<string>([$"Could not load task tree: {ShortError(ex)}"]));
                    _treeRows = [null];
                });
            }
        });
    }

    /// <summary>Renders the fetched tree rows into the tab's ListView via the shared
    /// <see cref="TaskRowRenderer"/> (in the current <see cref="_treeBadgeDisplay"/> mode, seeded from the
    /// main list and cycled by F6 #415) and overlays badge colours through <see cref="StatusBadgeListSource"/>,
    /// exactly as the main list does. Type-ahead searches by title (#12). Lands the cursor on the current
    /// task's row; retains the rows so an F6 badge cycle can re-render in place.</summary>
    private void PopulateTree(IReadOnlyList<TaskTreeRow> rows)
    {
        if (_treeList is null)
            return;
        _loadedTreeRows = rows;
        if (rows.Count == 0)
        {
            _treeList.SetSource(new ObservableCollection<string>(["(no task tree)"]));
            _treeRows = [null];
            return;
        }

        var currentIndex = RenderTreeRows(rows);
        _treeList.SelectedItem = currentIndex;
    }

    /// <summary>Reflects a new badge mode (F6, #415) after the host has cycled + persisted it, re-rendering
    /// the already-loaded tree rows in place — a pure display change (icons → text → hidden), no re-fetch.
    /// The cursor stays on the same row across the rebuild. No-op until the tree has loaded (the eventual
    /// <see cref="PopulateTree"/> then uses the updated mode). Must run on the UI thread.</summary>
    public void SetTreeBadgeDisplay(BadgeDisplay mode)
    {
        _treeBadgeDisplay = mode;
        if (_treeList is null || _loadedTreeRows.Count == 0)
            return;
        // Assigning .Source resets the selection, so capture and restore it around the rebuild.
        var previous = _treeList.SelectedItem;
        RenderTreeRows(_loadedTreeRows);
        if (previous >= 0 && previous < _treeRows.Count)
            _treeList.SelectedItem = previous;
    }

    /// <summary>Builds the tree ListView's colour-overlaying source (display text, badges, type-ahead keys)
    /// from <paramref name="rows"/> in the current badge mode and assigns it, refreshing the parallel
    /// <see cref="_treeRows"/> hit-test list. Returns the index of the current-task row (0 when absent).</summary>
    private int RenderTreeRows(IReadOnlyList<TaskTreeRow> rows)
    {
        var display = new ObservableCollection<string>();
        var badges = new List<IReadOnlyList<StatusBadgeListSource.Badge>>(rows.Count);
        var searchKeys = new List<string>(rows.Count);
        var taskRows = new List<TaskItem?>(rows.Count);
        var currentIndex = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // The tree renders fully expanded with no ▶/▼ fold arrows, so the renderer's marker-offset
            // fields (used only for fold-arrow hit-testing on the main list) are ignored here.
            var rendered = TaskRowRenderer.Render(row.Task, _treeBadgeDisplay, _currentUserId, row.Depth);
            display.Add(rendered.Text);
            badges.Add(rendered.Badges);
            searchKeys.Add(row.Task.Name);
            taskRows.Add(row.Task);
            if (row.IsCurrent)
                currentIndex = i;
        }

        _treeRows = taskRows;
        // Assigning .Source (not SetSource) lets us pass our colour-overlaying source; the ListView
        // disposes the previous one. Mirrors TodoApp.Render's main-list wiring.
        _treeList!.Source = new StatusBadgeListSource(display, badges, headerAttrs: null, searchKeys: searchKeys);
        return currentIndex;
    }

    // ── Checklists tab (C, #456) ────────────────────────────────────────────────

    /// <summary>Renders a checklist projection into the tab's ListView: group-header rows draw with the
    /// shared neutral header attribute (like the main list's group headers, <see cref="GroupHeaderPalette"/>),
    /// item rows carry <c>[x]</c>/<c>[ ]</c> glyphs, indentation and an assignee suffix — all from the
    /// pure <see cref="ChecklistTabModel"/>. Sets the tab title to the aggregate progress and caches the
    /// projected rows + a content <see cref="ChecklistTabModel.Signature"/> so a refresh can skip an
    /// unchanged rebuild (<see cref="UpdateData"/>) and re-anchor the selection by item id. A task with no
    /// checklists renders a single empty-state row. Type-ahead (#12) searches by row text.</summary>
    private void RenderChecklist(ChecklistProjection projection)
    {
        _checklistSignature = ChecklistTabModel.Signature(projection);
        _checklistList.Title = ChecklistTabModel.TabTitle(projection);

        if (projection.IsEmpty)
        {
            _checklistRows = [];
            _checklistList.SetSource(new ObservableCollection<string>([ChecklistTabModel.EmptyStateText]));
            return;
        }

        var display = new ObservableCollection<string>();
        var badges = new List<IReadOnlyList<StatusBadgeListSource.Badge>>(projection.Rows.Count);
        var headerAttrs = new List<Terminal.Gui.Drawing.Attribute?>(projection.Rows.Count);
        var searchKeys = new List<string>(projection.Rows.Count);
        foreach (var row in projection.Rows)
        {
            display.Add(ChecklistTabModel.RenderRow(row));
            badges.Add([]); // no status badges on checklist rows; headers colour via headerAttrs.
            headerAttrs.Add(row.IsHeader ? StatusBadgeListSource.NeutralHeaderAttr : null);
            searchKeys.Add(row.Text);
        }

        _checklistRows = [.. projection.Rows];
        // Assigning .Source (not SetSource) lets us pass our colour-overlaying source (header bars); the
        // ListView disposes the previous one. Mirrors the Task Tree tab's RenderTreeRows wiring.
        _checklistList.Source = new StatusBadgeListSource(display, badges, headerAttrs, searchKeys);
    }

    /// <summary>Enter on the tree tab: navigate to the highlighted row's task.</summary>
    private void NavigateTreeSelection()
    {
        if (_treeList?.SelectedItem is not int i || i < 0 || i >= _treeRows.Count)
            return;
        NavigateToTreeTask(_treeRows[i]);
    }

    /// <summary>Double-click a tree row → navigate to its task (the mouse equivalent of Enter), resolved
    /// via the shared <see cref="RowHitTester"/> (A, #286). A message/placeholder row (null task) or a
    /// click in the empty space beneath the rows no-ops. Single-click keeps native selection.</summary>
    private void OnTreeMouse(object? sender, Mouse e)
    {
        if (_treeList is null)
            return;
        if (!e.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked) || e.Position is not { } pos)
            return;
        var task = RowHitTester.TaskAt(pos.Y, _treeList.Viewport.Y, _treeRows);
        if (task is null)
            return;
        e.Handled = true;
        NavigateToTreeTask(task);
    }

    /// <summary>
    /// A link click in one of the text panes (D, #318) — forwarded to the host as
    /// <see cref="LinkActivationRequested"/>, except while an overlay is up. The Dispatch pane, the comment
    /// composer, the description editor and the reply-target picker (#330) each own input while open (the
    /// same rule <see cref="OnKey"/> applies to the screen's chords), and they only partially cover the
    /// panes — so without this guard a click on the still-visible part of a pane could navigate away from
    /// an open draft or picker.
    /// </summary>
    private void OnPaneLinkActivation(object? sender, LinkActivationRequest request)
    {
        if (_promptBox.Visible || _commentBox.Visible || _descriptionBox.Visible || _replyPickerBox.Visible)
            return;
        LinkActivationRequested?.Invoke(this, request);
    }

    /// <summary>Raises <see cref="OpenTaskRequested"/> for a non-current task; the current-task row is a
    /// no-op (flashed), so clicking the task you're already viewing does nothing surprising.</summary>
    private void NavigateToTreeTask(TaskItem? task)
    {
        if (task is null)
            return;
        if (string.Equals(task.Id, _task.Id, StringComparison.Ordinal))
        {
            RequestFlash("Already viewing this task.");
            return;
        }
        OpenTaskRequested?.Invoke(this, task.Id);
    }

    // A read-only, word-wrapped pane. DetailPaneView draws the inter-block separator rules
    // (TaskDetailFormatter.CommentSeparator) on the terminal-default background so they read as clear
    // breaks (Description has none, so it renders exactly as a stock TextView would).
    private static DetailPaneView NewPane(string title, string text)
    {
        var pane = new DetailPaneView
        {
            Title = title,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        pane.SetBody(text, TaskDetailFormatter.CommentSeparator);
        return pane;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Mark torn-down so a late comment-post continuation (#216) bails instead of updating
            // disposed tab views.
            _disposed = true;
            // Stop the 30s auto-refresh tick so it can't fire against a torn-down view (#114 follow-up).
            if (_autoRefreshToken is { } token)
            {
                Application.RemoveTimeout(token);
                _autoRefreshToken = null;
            }
        }
        base.Dispose(disposing);
    }
}
