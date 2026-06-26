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
/// </summary>
public sealed class TaskDetailScreen : Screen
{
    private readonly Tabs _tabs;
    private readonly TextView[] _panes;

    /// <summary>True when the user pressed Ctrl+B to open the task in the browser.</summary>
    public bool OpenBrowserRequested { get; private set; }

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
            Text = " Tab switch tab · ↑/↓ PgUp/PgDn scroll · Ctrl+B open in browser · Esc back to list",
        };

        // Focus lives in whichever pane (TextView) is front-most, so the key handler is wired to each
        // pane to reliably intercept Tab/Esc/Ctrl+B before the read-only TextView sees them.
        foreach (var pane in _panes)
            pane.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([header, _tabs, hint]);
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
