using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace ClickUpTodo.Tui.Screens;

/// <summary>A full-window screen listing the keyboard shortcuts. Esc or Enter returns to the list.</summary>
public sealed class HelpScreen : Screen
{
    public HelpScreen()
    {
        Title = "Keyboard shortcuts";

        var body = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            Text =
                "\n"
                + "  ↑ / ↓       Move between tasks\n"
                + "  (type)      Search tasks by title (type-ahead)\n"
                + "  Tab         Jump to the first task in the next section\n"
                + "  Space       Quick Updates for the focused task — Enter applies its status or priority\n"
                + "              (Tab switches panes; ✓ marks the current value; Assignees pane; Esc exits)\n"
                + "  Enter       Open the task detail view (description, comments, attributes)\n"
                + "  Ctrl+A      In the detail view: dispatch a Claude session (a one-off run shows its\n"
                + "              output in the app; press Esc there to cancel a run in progress)\n"
                + "  Ctrl+B      Open the task in your browser\n"
                + "  Ctrl+P      Pin / unpin (pinned tasks group at the top)\n"
                + "  Ctrl+E      Open the mentions & comments feed (in the feed: Enter opens the task, F3 shows mentions only)\n"
                + "              Mentions need a per-Space automation — see docs/mention-assignee-automation.md\n"
                + "  F1          This help\n"
                + "  F2          Settings (refresh rate, excluded statuses)\n"
                + "  F3          Filter / sort / group the list\n"
                + "  F4          Cycle subtasks: mine + unassigned → all → hidden (nested under their parent)\n"
                + "  F5          Refresh now (also Ctrl+R; the detail & feed views also auto-refresh)\n"
                + "  F6          Cycle status/priority badges (icons ○ ⚑, text, hidden)\n"
                + "  F12         Show / hide completed tasks (closed-type; applies to subtasks too)\n"
                + "  → / ←       Expand / collapse the selected parent's subtasks (▶ collapsed, ▼ expanded)\n"
                + "  Ctrl+→ / ←  Expand / collapse all parents at once\n"
                + "              (F4's 'all' state also nests subtasks not assigned to you)\n"
                + "  Ctrl+Q/Esc  Quit\n"
                + "\n"
                + "  Settings, Quick Updates, the task detail, and this help open as full-window\n"
                + "  screens; Esc returns to the task list (your cursor stays on the same task).\n"
                + "\n"
                + "  Esc or Enter to close this help.",
        };

        KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Esc or KeyCode.Enter)
            {
                key.Handled = true;
                Close();
            }
        };

        Add(body);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.Help;

    public override void OnShown() => SetFocus();
}
