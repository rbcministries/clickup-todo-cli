using System.Collections.ObjectModel;
using System.Drawing;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
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

    // The Dispatch pane's working-dir layout (#95): rows above the browser (prompt, one-off, dir
    // field, key hint), the browser's own rows, and rows below (post-to-Comments). Used to size the
    // pane via DispatchPaneModel.PreferredHeightWithBrowser and to place the ListView.
    private const int DispatchRowsAboveBrowser = 4;
    private const int DispatchBrowserRows = 5;
    private const int DispatchRowsBelowBrowser = 1;

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

    /// <param name="defaultSessionMode">
    /// Seeds the pane's one-off/interactive toggle (#94) from the persisted default (#101); the user
    /// can flip it per dispatch. Defaults to <see cref="AgentSessionMode.Interactive"/>.
    /// </param>
    /// <param name="defaultPostToComments">
    /// Seeds the pane's post-results-to-Comments toggle (#97) from the persisted default; the user can
    /// flip it per dispatch. Defaults to off.
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
    public TaskDetailScreen(
        TaskDetail task,
        IReadOnlyList<CommentItem> comments,
        string baseWorkingDirectory,
        DetailViewSettings? settings = null,
        AgentSessionMode defaultSessionMode = AgentSessionMode.Interactive,
        bool defaultPostToComments = false,
        Func<string>? workingDirectoryPreFill = null,
        Func<string, CancellationToken, Task<CommentItem>>? postCommentAsync = null)
    {
        var prefs = settings ?? new DetailViewSettings();
        _task = task;
        _comments = comments;
        _workingDirectoryPreFill = workingDirectoryPreFill;
        _postCommentAsync = postCommentAsync;
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

        _tabs = new Tabs
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

        // The Other tab colours its Priority/Status values (#66), which a plain TextView can't do. Its
        // content is a container (a coloured, fixed-height header view on top of the scrollable,
        // word-wrapped "Custom fields:" body). DetailOtherTabView owns that split and adapts it so both
        // the header attributes and the custom-fields section stay reachable on a very short window (#81).
        var headerLines = TaskDetailFormatter.HeaderAttributeLines(task);
        var customFieldsBody = TaskDetailFormatter.CustomFieldsBody(task);
        _otherSignature = OtherTabSignature(headerLines, customFieldsBody);
        _otherTab = new DetailOtherTabView(headerLines, customFieldsBody);

        _tabContents = [_streamPane, _descriptionPane, _commentsPane, _otherTab];
        _scrollTargets = [_streamPane, _descriptionPane, _commentsPane, _otherTab.ScrollTarget];

        for (var i = 0; i < _tabContents.Length; i++)
            _tabs.InsertTab(i, _tabContents[i]);
        // Open on the configured default tab (#108); Stream unless the user changed it in F2. The
        // selection is (re)asserted in OnShown — setting it here alone doesn't survive first show.
        _defaultTabIndex = prefs.DefaultTab.ToTabIndex();
        _tabs.Value = _tabContents[_defaultTabIndex];

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

        _dispatchControls = [_promptField, _oneOffToggle, _workingDirField, _dirBrowser, _postToCommentsToggle];

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
        _promptBox.Add(promptLabel, _promptField, _oneOffToggle, dirLabel, _workingDirField, browserHint, _dirBrowser, _postToCommentsToggle);
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

        // Focus lives in whichever scroll target (TextView) is front-most, so the key handler is wired
        // to each to reliably intercept Tab/Esc/Ctrl+B/Ctrl+A/F1 before the read-only TextView sees them.
        foreach (var target in _scrollTargets)
            target.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([_headerView, _tabs, _promptBox, _commentBox]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.Detail;

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
        // While the comment composer (#216) is open it owns the keyboard: its own handler (OnCommentKey)
        // processes Ctrl+Enter/Esc/Tab and lets the rest fall through to the editor. Don't let the
        // screen's chords (Ctrl+B close, Ctrl+A/U/N openers, Tab tab-cycle, F5 refresh) fire underneath
        // and disrupt (or discard) the draft.
        if (_commentBox.Visible)
            return;

        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.B)
        {
            key.Handled = true;
            OpenBrowserRequested = true;
            Close();
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

        // Ctrl+U opens Quick Updates for this task (#159), stacked over the detail view; Esc there pops
        // back here. Same chord shape as Ctrl+A/B above and inert while the Dispatch prompt is open, so
        // it never interferes with typing a prompt or a read-only pane.
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.U && !_promptBox.Visible)
        {
            key.Handled = true;
            QuickUpdatesRequested?.Invoke(this, EventArgs.Empty);
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

        switch (key.KeyCode)
        {
            case KeyCode.Tab:
                key.Handled = true;
                CycleTab(forward: !key.IsShift);
                break;
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
    /// (#95; blank ⇒ null ⇒ default dir), and the post-to-Comments flag (#97).</summary>
    private void SubmitDispatch()
    {
        var text = _promptField.Text?.ToString() ?? string.Empty;
        var sessionMode = _oneOffToggle.Value == CheckState.Checked
            ? AgentSessionMode.OneOff
            : AgentSessionMode.Interactive;
        var dir = _workingDirField.Text?.ToString();
        var postToComments = _postToCommentsToggle.Value == CheckState.Checked;
        HidePrompt();
        // A stray Enter shouldn't launch a session — only dispatch when something was typed.
        if (!string.IsNullOrWhiteSpace(text))
            AgentDispatchRequested?.Invoke(this, new DispatchRequest(text, sessionMode, dir, postToComments));
    }

    /// <summary>Moves focus to the next/previous dispatch control, wrapping at both ends.</summary>
    private void MoveDispatchFocus(bool forward)
    {
        var current = Array.FindIndex(_dispatchControls, static c => c.HasFocus);
        if (current < 0)
            current = 0;
        _dispatchControls[DispatchPaneModel.NextFocus(current, _dispatchControls.Length, forward)].SetFocus();
    }

    /// <summary>Scrolls the front-most tab's body while the pane holds keyboard focus (PgUp/PgDn pass
    /// through to it rather than being trapped in the pane), so the user can review it while composing.</summary>
    private void ScrollActiveTab(Command command)
    {
        var current = Array.IndexOf(_tabContents, _tabs.Value);
        if (current < 0)
            current = 0;
        _scrollTargets[current].InvokeCommand(command);
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
    /// clamp), shows it and focuses the editor.</summary>
    private void ShowCommentComposer()
    {
        if (_commentBox.Visible)
            return;
        _commentEditor.Text = string.Empty;
        var height = DispatchPaneModel.ClampHeight(CommentComposerPreferredHeight, Viewport.Height, minTabRows: 3);
        _commentBox.Height = height;
        _commentBox.Y = Pos.AnchorEnd(height);
        _commentBox.Visible = true;
        _commentEditor.SetFocus();
    }

    /// <summary>Closes the composer and returns focus to the front-most tab (mirrors HidePrompt).</summary>
    private void HideCommentComposer()
    {
        if (!_commentBox.Visible)
            return;
        _commentBox.Visible = false;
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
        if (!CommentComposerModel.IsPostable(raw) || _postCommentAsync is null)
        {
            HideCommentComposer();
            return;
        }
        var text = CommentComposerModel.Normalize(raw);
        HideCommentComposer();

        // Optimistic append: a provisional comment with a client sentinel id (so reconcile/revert can
        // find it) and a client "now" stamp (so it sorts as the newest entry). Re-render via UpdateData,
        // which recomputes the Stream/Comments panes in place with scroll preservation.
        var pendingId = $"__pending__{++_pendingCommentSeq}";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var provisional = CommentComposerModel.Provisional(pendingId, text, nowMs);
        UpdateData(_task, CommentComposerModel.Append(_comments, provisional));
        RequestFlash("Posting comment…");

        // Fully-qualified: this screen exposes a `Task` property (the shown TaskDetail), which would
        // otherwise shadow System.Threading.Tasks.Task in this expression.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var confirmed = await _postCommentAsync(text, CancellationToken.None).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    UpdateData(_task, CommentComposerModel.Reconcile(_comments, pendingId, confirmed));
                    RequestFlash("Comment posted.");
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    UpdateData(_task, CommentComposerModel.Revert(_comments, pendingId));
                    RequestFlash($"Could not post comment: {ShortError(ex)}");
                });
            }
        });
    }

    /// <summary>A one-line, length-capped rendering of an exception for the status flash.</summary>
    private static string ShortError(Exception ex)
    {
        var msg = ex.Message.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return msg.Length > 80 ? msg[..79] + "…" : msg;
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
        var next = ((current + (forward ? 1 : -1)) % _tabContents.Length + _tabContents.Length) % _tabContents.Length;
        _tabs.Value = _tabContents[next];
        _scrollTargets[next].SetFocus();
        // If the Stream tab wasn't the default, its auto-scroll (#107) was deferred until it's shown —
        // apply it now that its viewport is laid out.
        FlushStreamAutoScrollIfActive();
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
        // Stop the 30s auto-refresh tick so it can't fire against a torn-down view (#114 follow-up).
        if (disposing && _autoRefreshToken is { } token)
        {
            Application.RemoveTimeout(token);
            _autoRefreshToken = null;
        }
        base.Dispose(disposing);
    }
}
