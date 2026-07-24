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
            // The icon column mirrors the glyphs on the bottom help bar (HelpItemSets) so a footer
            // icon can be matched to the concept it stands for. Keys come from the central Keybindings
            // table; keep the two in sync when a binding changes.
            Text =
                "\n"
                + "  Icons match the labels on the bottom help bar.\n"
                + "\n"
                + "  TASK LIST\n"
                + "  ↑ / ↓             Move between tasks\n"
                + "  (type)            Search tasks by title (type-ahead)\n"
                + "  Tab               Jump to the first task in the next section\n"
                + "  Enter             Open the task detail view (description, comments, attributes)\n"
                + "  Ctrl+U            Quick Updates for the focused task — status, priority, assignees\n"
                + "                    (Tab switches panes; ✓ marks the current value; Enter applies; Esc exits)\n"
                + "  Ctrl+O        🗁   Open a task by id, custom id, or URL\n"
                + "  Ctrl+Enter        Open the focused task in a new terminal tab (also Ctrl+Left-Click a row);\n"
                + "                    falls back to a new window, or copies the command if no terminal can open\n"
                + "  Ctrl+N        ➕  New task (filed in your primary list)\n"
                + "  Ctrl+B        🌐  Open the task in your browser\n"
                + "  Ctrl+P        📌  Pin / unpin (pinned tasks group at the top)\n"
                + "  Ctrl+E        🔔  Open the mentions & comments feed (Enter opens the task, F3 mentions\n"
                + "                    only, F6 toggles recent activity, F12 includes completed tickets' activity)\n"
                + "                    Mentions need a per-Space automation — see docs/mention-assignee-automation.md\n"
                + "  F1            ℹ   This help / shortcuts\n"
                + "  F2            ⚙   Settings (refresh rate, excluded statuses)\n"
                + "  F3            ⧩ ▼▲ ⛚  Filter / sort / group the list\n"
                + "  F4                Cycle subtasks: mine + unassigned → all → hidden (nested under parent)\n"
                + "  F5            ↻   Refresh now (also Ctrl+R; the detail & feed views also auto-refresh)\n"
                + "  F6                Cycle status/priority badges (icons ○ ⚑, text, hidden)\n"
                + "  F12           👁✅ Cycle completed: active only → + done → + done & closed (subtasks too)\n"
                + "  → / ←             Expand / collapse the selected parent's subtasks (▶ collapsed, ▼ expanded)\n"
                + "  Ctrl+→ / ←        Expand / collapse all parents at once\n"
                + "                    (F4's 'all' state also nests subtasks not assigned to you)\n"
                + "  Ctrl+Q/Esc        Quit — asks to confirm first (Y or Enter exits, N or Esc stays)\n"
                + "\n"
                + "  TASK DETAIL VIEW\n"
                + "  ↑ / ↓             Scroll (also PgUp / PgDn)\n"
                + "  Ctrl+← / →        Switch tab (Description / Comments / Other)\n"
                + "  Ctrl+PgUp/Dn  ▼▲  Order activity (sort the comments & activity feed)\n"
                + "  Ctrl+A        ✨  Dispatch a Claude session (a one-off run shows its output in the app;\n"
                + "                    press Esc there to cancel a run in progress)\n"
                + "  Ctrl+N        ➕  Add a comment\n"
                + "  Ctrl+E        ✏   Edit the description\n"
                + "  Ctrl+B        🌐  Open the task in your browser\n"
                + "  Ctrl+U            Quick Updates for this task (Esc returns to the detail)\n"
                + "  Ctrl+O        🗁   Open another task by id, custom id, or URL\n"
                + "  F5            ↻   Refresh\n"
                + "  F1            ℹ   This help\n"
                + "  Esc               Back to the list\n"
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
