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
/// ↑/↓/PgUp/PgDn scroll the focused pane. Tab bodies come from the unit-tested
/// <see cref="TaskDetailFormatter"/>, so this class is only the (CI-untestable) Terminal.Gui glue.
/// </para>
/// <para>
/// <b>A</b> opens an inline "Prompt for Claude:" input (issue #26, S3 of the #23 epic); submitting it
/// raises <see cref="AgentDispatchRequested"/> with the typed prompt (the host composes + launches an
/// interactive <c>claude</c> session) and keeps the detail view open with focus back on the pane. The
/// input is a transient child view — not a nested run-loop or a second screen — so it stays within the
/// single already-open screen; the dashboard's single-<c>ListView</c> model (#3) is untouched.
/// </para>
/// </summary>
public sealed class TaskDetailScreen : Screen
{
    private readonly Tabs _tabs;
    private readonly TextView[] _panes;
    private readonly FrameView _promptBox;
    private readonly TextField _promptField;

    /// <summary>True when the user pressed Ctrl+B to open the task in the browser.</summary>
    public bool OpenBrowserRequested { get; private set; }

    /// <summary>
    /// Raised when the user submits a non-empty prompt in the "Prompt for Claude:" input (A). The
    /// argument is the typed prompt; the host composes it with the task detail + comments and launches
    /// an interactive <c>claude</c> session. The detail view stays open.
    /// </summary>
    public event EventHandler<string>? AgentDispatchRequested;

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
        var other = NewPane("Other", TaskDetailFormatter.OtherAttributes(task));
        _panes = [description, commentsPane, other];

        for (var i = 0; i < _panes.Length; i++)
            _tabs.InsertTab(i, _panes[i]);
        _tabs.Value = description;

        var hint = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
            Text = " Tab switch tab · ↑/↓ PgUp/PgDn scroll · A dispatch to Claude · Ctrl+B browser · Esc back",
        };

        // The "Prompt for Claude:" input (#26): a transient single-line field, hidden until A is
        // pressed. Single-line so Enter unambiguously submits (Esc cancels); the composer trims the
        // text. Overlaid near the bottom and added last so it draws on top of the panes/hint.
        _promptField = new TextField { X = 0, Y = 0, Width = Dim.Fill() };
        _promptBox = new FrameView
        {
            Title = "Prompt for Claude:",
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            Height = 3,
            Visible = false,
        };
        _promptBox.Add(_promptField);
        _promptField.KeyDown += OnPromptKey;

        // Focus lives in whichever pane (TextView) is front-most, so the key handler is wired to each
        // pane to reliably intercept Tab/Esc/Ctrl+B/A before the read-only TextView sees them.
        foreach (var pane in _panes)
            pane.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([header, _tabs, hint, _promptBox]);
    }

    public override void OnShown() => _panes[0].SetFocus();

    private void OnKey(object? sender, Key key)
    {
        if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.B)
        {
            key.Handled = true;
            OpenBrowserRequested = true;
            Close();
            return;
        }

        // A opens the "Prompt for Claude:" input (#26). A bare letter is safe here — the detail panes
        // are read-only (no type-ahead, unlike the dashboard list #12). Guarded so it's inert while the
        // input is already open (there, A just types into the field). Masks Shift so a/A both trigger.
        if (!key.IsCtrl && !key.IsAlt && !_promptBox.Visible
            && (key.KeyCode & ~KeyCode.ShiftMask) == KeyCode.A)
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
            case KeyCode.Esc:
                key.Handled = true;
                Close();
                break;
        }
    }

    /// <summary>Handles keys while the "Prompt for Claude:" field has focus: Enter submits a non-empty
    /// prompt, Esc cancels; both hide the box and return focus to the pane. Tab is trapped so focus
    /// stays in the field until the user submits or cancels.</summary>
    private void OnPromptKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Enter:
                key.Handled = true;
                var text = _promptField.Text?.ToString() ?? string.Empty;
                HidePrompt();
                // A stray Enter shouldn't launch a session — only dispatch when something was typed.
                if (!string.IsNullOrWhiteSpace(text))
                    AgentDispatchRequested?.Invoke(this, text);
                break;
            case KeyCode.Esc:
                key.Handled = true;
                HidePrompt();
                break;
            case KeyCode.Tab:
                key.Handled = true;
                break;
        }
    }

    private void ShowPrompt()
    {
        if (_promptBox.Visible)
            return;
        _promptField.Text = string.Empty;
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

    /// <summary>Returns focus to the front-most tab pane (after the prompt box closes).</summary>
    private void FocusCurrentPane()
    {
        var current = Array.IndexOf(_panes, _tabs.Value as TextView);
        if (current < 0)
            current = 0;
        _panes[current].SetFocus();
    }

    /// <summary>Advances the selected tab and moves focus into it so ↑/↓ scroll its content.</summary>
    private void CycleTab(bool forward)
    {
        var current = Array.IndexOf(_panes, _tabs.Value as TextView);
        if (current < 0)
            current = 0;
        var next = ((current + (forward ? 1 : -1)) % _panes.Length + _panes.Length) % _panes.Length;
        _tabs.Value = _panes[next];
        _panes[next].SetFocus();
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
