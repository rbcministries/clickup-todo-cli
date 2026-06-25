using ClickUpTodo.ClickUp;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// A modal task detail view (issue #17): a header (title, tags, assignees) above a tabbed, scrollable
/// pane — Description / Comments / Other attributes. Opened with Enter from the dashboard.
/// <para>
/// This is a separate modal <see cref="Dialog"/>, NOT a second focusable pane in the dashboard, so
/// the single-<see cref="ListView"/> model and the #3 input-latency fix are unaffected. The body text
/// for each tab comes from the unit-tested <see cref="TaskDetailFormatter"/>; this class is only the
/// (CI-untestable) Terminal.Gui glue.
/// </para>
/// </summary>
public static class TaskDetailView
{
    /// <summary>
    /// Shows the detail modal. Returns <c>true</c> if the user asked to open the task in the browser
    /// (Ctrl+B); the caller owns the actual process launch (as it does for the dashboard's Ctrl+B).
    /// </summary>
    public static bool Show(TaskDetail task, IReadOnlyList<CommentItem> comments)
    {
        var dialog = new Dialog
        {
            Title = Truncate(task.Name, 60),
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
        };

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

        var tabs = new Tabs
        {
            X = 0,
            Y = headerHeight + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };

        var description = NewPane("Description", TaskDetailFormatter.Description(task));
        var commentsPane = NewPane($"Comments ({comments.Count})", TaskDetailFormatter.Comments(comments));
        var other = NewPane("Other", TaskDetailFormatter.OtherAttributes(task));
        TextView[] panes = [description, commentsPane, other];

        for (var i = 0; i < panes.Length; i++)
            tabs.InsertTab(i, panes[i]);
        tabs.Value = description;

        var hint = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
            Text = " Tab switch tab · ↑/↓ PgUp/PgDn scroll · Ctrl+B open in browser · Esc/Enter close",
        };

        var openBrowser = false;
        void OnKey(object? sender, Key key)
        {
            if (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.B)
            {
                key.Handled = true;
                openBrowser = true;
                Application.RequestStop();
                return;
            }

            switch (key.KeyCode)
            {
                case KeyCode.Tab:
                    key.Handled = true;
                    CycleTab(tabs, panes, forward: !key.IsShift);
                    break;
                case KeyCode.Esc:
                case KeyCode.Enter:
                    key.Handled = true;
                    Application.RequestStop();
                    break;
            }
        }

        // Focus lives in whichever pane (TextView) is front-most, so the key handler is wired to each
        // pane to reliably intercept Tab/Esc/Enter/Ctrl+B before the read-only TextView sees them.
        foreach (var pane in panes)
            pane.KeyDown += OnKey;
        dialog.KeyDown += OnKey;

        dialog.Add(header, tabs, hint);
        description.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
        return openBrowser;
    }

    /// <summary>Advances the selected tab and moves focus into it so ↑/↓ scroll its content.</summary>
    private static void CycleTab(Tabs tabs, TextView[] panes, bool forward)
    {
        var current = Array.IndexOf(panes, tabs.Value as TextView);
        if (current < 0)
            current = 0;
        var next = ((current + (forward ? 1 : -1)) % panes.Length + panes.Length) % panes.Length;
        tabs.Value = panes[next];
        panes[next].SetFocus();
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

    private static string Truncate(string value, int max)
        => value.Length > max ? value[..(max - 1)] + "…" : value;
}
