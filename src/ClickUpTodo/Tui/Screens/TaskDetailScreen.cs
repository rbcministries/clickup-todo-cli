using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
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
/// ↑/↓/PgUp/PgDn scroll the focused pane; F1 opens Help. The Stream tab (#106) is the default;
/// Ctrl+PgUp/Ctrl+PgDn sort it oldest-first / newest-first and re-render it in place. Tab bodies come
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
/// <see cref="DirectoryBrowserModel"/>). ↑/↓ move; → descends into the highlighted dir; ← goes up;
/// <b>Enter selects</b> the highlighted dir (writes its path into the field and advances focus) —
/// on <c>..</c>, Enter goes up so it never submits from the browser. A blank field falls through to
/// the configured-default / task-derived working dir (#98).
/// </para>
/// </summary>
public sealed class TaskDetailScreen : Screen
{
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
    // The Dispatch pane's controls, in focus (Tab) order. The prompt and the working-dir control
    // (#95) feed a dispatch; the one-off (#94) and post-to-Comments (#97) toggles are still stubs that
    // establish the pane's layout + focus order until those features land.
    private readonly View[] _dispatchControls;
    private readonly CheckBox _oneOffToggle;
    private readonly TextField _workingDirField;
    private readonly ListView _dirBrowser;
    private readonly DirectoryBrowserModel _browser;
    private readonly CheckBox _postToCommentsToggle;

    // The Dispatch pane's working-dir layout (#95): rows above the browser (prompt, one-off, dir
    // field, key hint), the browser's own rows, and rows below (post-to-Comments). Used to size the
    // pane via DispatchPaneModel.PreferredHeightWithBrowser and to place the ListView.
    private const int DispatchRowsAboveBrowser = 4;
    private const int DispatchBrowserRows = 5;
    private const int DispatchRowsBelowBrowser = 1;

    // The Stream tab (#106) and the data it re-renders from on a sort toggle. Default oldest-first
    // (Description then comments ascending) — the issue's "Description followed by the comments in
    // order" reading; #S3 (#108) makes the default configurable.
    private readonly TextView _streamPane;
    private readonly TaskDetail _task;
    private readonly IReadOnlyList<CommentItem> _comments;
    private StreamSort _streamSort = StreamSort.Ascending;

    /// <summary>True when the user pressed Ctrl+B to open the task in the browser.</summary>
    public bool OpenBrowserRequested { get; private set; }

    /// <summary>
    /// Raised when the user submits a non-empty prompt in the Dispatch pane (Ctrl+A). The argument
    /// carries the typed prompt (and, as #94/#95/#97 land, the pane's other options); the host composes
    /// it with the task detail + comments and launches an interactive <c>claude</c> session. The detail
    /// view stays open.
    /// </summary>
    public event EventHandler<DispatchRequest>? AgentDispatchRequested;

    public TaskDetailScreen(TaskDetail task, IReadOnlyList<CommentItem> comments, string baseWorkingDirectory)
    {
        _task = task;
        _comments = comments;
        _browser = new DirectoryBrowserModel(baseWorkingDirectory);
        Title = task.Name.Length > 60 ? task.Name[..59] + "…" : task.Name;

        var headerText = TaskDetailFormatter.Header(task);
        var headerHeight = headerText.Split('\n').Length;
        var header = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = headerHeight,
            Text = headerText,
        };

        _tabs = new Tabs
        {
            X = 0,
            Y = headerHeight + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };

        // The Stream tab (#106): Description + comments as one timeline, sortable in place. Built first
        // so it's the default selected tab below.
        _streamPane = NewPane("Stream", TaskDetailFormatter.Stream(task, comments, _streamSort));
        var description = NewPane("Description", TaskDetailFormatter.Description(task));
        var commentsPane = NewPane($"Comments ({comments.Count})", TaskDetailFormatter.Comments(comments));

        // The Other tab colours its Priority/Status values (#66), which a plain TextView can't do. Its
        // content is a container (a coloured, fixed-height header view on top of the scrollable,
        // word-wrapped "Custom fields:" body). DetailOtherTabView owns that split and adapts it so both
        // the header attributes and the custom-fields section stay reachable on a very short window (#81).
        var headerLines = TaskDetailFormatter.HeaderAttributeLines(task);
        var other = new DetailOtherTabView(headerLines, TaskDetailFormatter.CustomFieldsBody(task));

        _tabContents = [_streamPane, description, commentsPane, other];
        _scrollTargets = [_streamPane, description, commentsPane, other.ScrollTarget];

        for (var i = 0; i < _tabContents.Length; i++)
            _tabs.InsertTab(i, _tabContents[i]);
        _tabs.Value = _streamPane;

        // The Dispatch pane (#93, D1 of the #90 epic; superseding the single-line #26 prompt): a
        // bottom-anchored FrameView hosting the prompt plus placeholder controls for the one-off/
        // interactive (#94), working-dir (#95) and post-to-Comments (#97) options. Hidden until Ctrl+A.
        // A transient child view within the single already-open screen — not a nested run-loop or a
        // second toplevel (the #26 design note) — so the dashboard's single-ListView model (#3) is
        // untouched. Its height is computed on show (ShowPrompt) so it degrades gracefully on short
        // terminals: the prompt stays visible; the bottom stub controls clip first. The screen's own
        // shortcuts (incl. Ctrl+A) show in the window-owned contextual help footer via HelpItems (#103).
        var promptLabel = new Label { X = 1, Y = 0, Text = "Prompt:" };
        _promptField = new TextField { X = 9, Y = 0, Width = Dim.Fill(1) };
        // #94 (one-off) and #97 (post-to-Comments) are still stubs — focusable so the pane's layout +
        // Tab/Shift+Tab focus order are real, but not read into the DispatchRequest, so their default
        // (interactive, no comment post) keeps dispatch behaviour unchanged. The working-dir control
        // (#95) below is live: an editable field plus a file-tree browser; blank ⇒ default working dir.
        _oneOffToggle = new CheckBox { X = 1, Y = 1, Text = "Run one-off instead of interactive (coming soon)" };
        var dirLabel = new Label { X = 1, Y = 2, Text = "Dir:" };
        _workingDirField = new TextField { X = 9, Y = 2, Width = Dim.Fill(1) };
        var browserHint = new Label
        {
            X = 1,
            Y = 3,
            Text = "↑↓ move · → open · ← up · Enter select (blank ⇒ default dir)",
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
        _postToCommentsToggle = new CheckBox
        {
            X = 1,
            Y = DispatchRowsAboveBrowser + DispatchBrowserRows,
            Text = "Post results to Comments (coming soon)",
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

        // Focus lives in whichever scroll target (TextView) is front-most, so the key handler is wired
        // to each to reliably intercept Tab/Esc/Ctrl+B/Ctrl+A/F1 before the read-only TextView sees them.
        foreach (var target in _scrollTargets)
            target.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([header, _tabs, _promptBox]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.Detail;

    public override void OnShown() => _scrollTargets[0].SetFocus();

    private void OnKey(object? sender, Key key)
    {
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
    /// Handles keys while the working-dir file-tree browser (#95) has focus. Enter selects the
    /// highlighted directory (→ fills the field and advances focus), → descends into it, ← / a "select"
    /// on ".." goes up; everything else (↑/↓ list navigation, Tab, Esc, PgUp/PgDn) routes through the
    /// same <see cref="DispatchPaneModel"/> path as the other controls. Intercepting Enter here keeps it
    /// from submitting the dispatch while browsing.
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
                _browser.NavigateUp();
                RefreshBrowser();
                break;
            default:
                // Tab/Esc/PgUp/PgDn and pass-through keys (↑/↓ list navigation) behave as elsewhere.
                OnDispatchKey(sender, key);
                break;
        }
    }

    /// <summary>The highlighted browser row (0 = ".."), clamped to a valid index.</summary>
    private int SelectedBrowserIndex() => _dirBrowser.SelectedItem is int i && i >= 0 ? i : 0;

    /// <summary>Refreshes the ListView from the model's current listing, re-selecting the first row.</summary>
    private void RefreshBrowser()
    {
        _dirBrowser.SetSource(new ObservableCollection<string>(_browser.Entries));
        _dirBrowser.SelectedItem = 0;
    }

    /// <summary>Enter: select the highlighted directory into the field and advance focus; ".." goes up.</summary>
    private void SelectBrowserEntry()
    {
        var index = SelectedBrowserIndex();
        if (_browser.IsParent(index))
        {
            _browser.NavigateUp();
            RefreshBrowser();
            return;
        }
        _workingDirField.Text = _browser.PathAt(index);
        MoveDispatchFocus(forward: true);
    }

    /// <summary>→: descend into the highlighted directory (or up, for "..") to browse deeper.</summary>
    private void DescendBrowserEntry()
    {
        _browser.Descend(SelectedBrowserIndex());
        RefreshBrowser();
    }

    /// <summary>Submits the pane: hides it, then (only for non-empty text) raises the dispatch event
    /// carrying the prompt and the chosen working directory (#95; blank ⇒ null ⇒ default dir).</summary>
    private void SubmitDispatch()
    {
        var text = _promptField.Text?.ToString() ?? string.Empty;
        var dir = _workingDirField.Text?.ToString();
        HidePrompt();
        // A stray Enter shouldn't launch a session — only dispatch when something was typed.
        if (!string.IsNullOrWhiteSpace(text))
            AgentDispatchRequested?.Invoke(this, new DispatchRequest(text, dir));
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
        // Start each dispatch with a blank working dir (⇒ default dir #98) and the browser back at its
        // root (the base working dir #92); #96 will later pre-fill the field from a per-task cache.
        _workingDirField.Text = string.Empty;
        _browser.Reset();
        RefreshBrowser();
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

    /// <summary>Sets the Stream sort direction and re-renders the Stream body in place (#106). No-op if
    /// unchanged. Works regardless of the active tab — the new order is visible when the Stream tab is
    /// shown. (Auto-scroll on toggle is the follow-up #S2 / #107.)</summary>
    private void SetStreamSort(StreamSort sort)
    {
        if (_streamSort == sort)
            return;
        _streamSort = sort;
        _streamPane.Text = TaskDetailFormatter.Stream(_task, _comments, _streamSort);
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
    }

    private static TextView NewPane(string title, string text) => new()
    {
        Title = title,
        Text = text,
        ReadOnly = true,
        WordWrap = true,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
    };
}
