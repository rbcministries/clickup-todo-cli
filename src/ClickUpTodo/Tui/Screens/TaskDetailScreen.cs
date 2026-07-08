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
/// a tabbed, scrollable pane — Description / Comments / Other attributes. Built on the shared screen
/// seam (#38) — swapped into the dashboard's single toplevel, not a nested modal <c>Dialog</c>.
/// <para>
/// Esc returns to the list; Ctrl+B requests opening the task in the browser (the host reads
/// <see cref="OpenBrowserRequested"/> in its close handler and owns the launch). Tab cycles tabs;
/// ↑/↓/PgUp/PgDn scroll the focused pane; F1 opens Help. Tab bodies come from the unit-tested
/// <see cref="TaskDetailFormatter"/>, so this class is only the (CI-untestable) Terminal.Gui glue.
/// </para>
/// <para>
/// <b>Ctrl+A</b> opens the inline Dispatch pane (issue #93, D1 of the #90 epic; superseding the bare
/// <c>A</c> prompt of #26): a bottom-anchored <c>FrameView</c> hosting the prompt plus placeholder
/// controls for the options that land in #94/#95/#97. Tab/Shift+Tab cycle its controls, PgUp/PgDn keep
/// scrolling the tab above, Enter submits (raising <see cref="AgentDispatchRequested"/> with a
/// <see cref="DispatchRequest"/>) and Esc cancels — all routed through the pure
/// <see cref="DispatchPaneModel"/>. The pane is a transient child view — not a nested run-loop or a
/// second screen — so it stays within the single already-open screen; the dashboard's
/// single-<c>ListView</c> model (#3) is untouched.
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
    // The Dispatch pane's controls, in focus (Tab) order. Only the prompt feeds a dispatch today; the
    // stubs establish the pane's layout + focus order and are wired up in #94 (one-off/interactive),
    // #95 (working directory) and #97 (post-to-Comments).
    private readonly View[] _dispatchControls;
    private readonly CheckBox _oneOffToggle;
    private readonly TextField _workingDirField;
    private readonly CheckBox _postToCommentsToggle;

    /// <summary>True when the user pressed Ctrl+B to open the task in the browser.</summary>
    public bool OpenBrowserRequested { get; private set; }

    /// <summary>
    /// Raised when the user submits a non-empty prompt in the Dispatch pane (Ctrl+A). The argument
    /// carries the typed prompt (and, as #94/#95/#97 land, the pane's other options); the host composes
    /// it with the task detail + comments and launches an interactive <c>claude</c> session. The detail
    /// view stays open.
    /// </summary>
    public event EventHandler<DispatchRequest>? AgentDispatchRequested;

    public TaskDetailScreen(TaskDetail task, IReadOnlyList<CommentItem> comments)
    {
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

        var description = NewPane("Description", TaskDetailFormatter.Description(task));
        var commentsPane = NewPane($"Comments ({comments.Count})", TaskDetailFormatter.Comments(comments));

        // The Other tab colours its Priority/Status values (#66), which a plain TextView can't do. Its
        // content is a container: a coloured, fixed-height header view (List/Priority/Status/dates) on
        // top, and the scrollable, word-wrapped "Custom fields:" body beneath it.
        var headerLines = TaskDetailFormatter.HeaderAttributeLines(task);
        var attributesView = new DetailAttributesView(headerLines)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = headerLines.Count,
        };
        var customFields = new TextView
        {
            // Y leaves a blank gap row after the header attributes, mirroring the blank line the plain
            // OtherAttributes layout renders between them and the "Custom fields:" section.
            X = 0,
            Y = headerLines.Count + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = TaskDetailFormatter.CustomFieldsBody(task),
            ReadOnly = true,
            WordWrap = true,
        };
        // CanFocus so the container is in the focus chain — its scrollable custom-fields body (below)
        // receives focus via SetFocus; the coloured header view above it stays non-focusable.
        var other = new View { Title = "Other", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        other.Add(attributesView, customFields);

        _tabContents = [description, commentsPane, other];
        _scrollTargets = [description, commentsPane, customFields];

        for (var i = 0; i < _tabContents.Length; i++)
            _tabs.InsertTab(i, _tabContents[i]);
        _tabs.Value = description;

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
        // Stubs: present so the pane's layout + Tab/Shift+Tab focus order are real, but not yet read
        // into the DispatchRequest (that arrives with #94/#95/#97) — so dispatch behaviour is unchanged.
        // The working-dir field is read-only for now to read as inert. Defaults are the current
        // behaviour: interactive (one-off off), inherited working dir (blank), no comment post.
        _oneOffToggle = new CheckBox { X = 1, Y = 1, Text = "Run one-off instead of interactive (coming soon)" };
        var dirLabel = new Label { X = 1, Y = 2, Text = "Dir:" };
        _workingDirField = new TextField { X = 9, Y = 2, Width = Dim.Fill(1), ReadOnly = true };
        _postToCommentsToggle = new CheckBox { X = 1, Y = 3, Text = "Post results to Comments (coming soon)" };

        _dispatchControls = [_promptField, _oneOffToggle, _workingDirField, _postToCommentsToggle];

        var paneHeight = DispatchPaneModel.PreferredHeight(_dispatchControls.Length);
        _promptBox = new FrameView
        {
            Title = "Dispatch to Claude — Enter submit · Tab next · Esc cancel",
            X = 0,
            Y = Pos.AnchorEnd(paneHeight),
            Width = Dim.Fill(),
            Height = paneHeight,
            Visible = false,
        };
        _promptBox.Add(promptLabel, _promptField, _oneOffToggle, dirLabel, _workingDirField, _postToCommentsToggle);
        // One handler on every dispatch control routes the pane's keys (Enter/Esc/Tab/PgUp/PgDn) via
        // the pure DispatchPaneModel; other keys fall through so typing/Space-toggle keep working.
        foreach (var control in _dispatchControls)
            control.KeyDown += OnDispatchKey;

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

    /// <summary>Submits the pane: hides it, then (only for non-empty text) raises the dispatch event.</summary>
    private void SubmitDispatch()
    {
        var text = _promptField.Text?.ToString() ?? string.Empty;
        HidePrompt();
        // A stray Enter shouldn't launch a session — only dispatch when something was typed.
        if (!string.IsNullOrWhiteSpace(text))
            AgentDispatchRequested?.Invoke(this, new DispatchRequest(text));
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
        // Size the pane to the current tab body so it degrades gracefully on short terminals: the
        // prompt row + borders always survive; the bottom stub controls clip first.
        var height = DispatchPaneModel.ClampHeight(
            DispatchPaneModel.PreferredHeight(_dispatchControls.Length), Viewport.Height, minTabRows: 3);
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
