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
                + "  Space       Set the focused task's status\n"
                + "  Enter       Open the task detail view (description, comments, attributes)\n"
                + "  A           In the detail view: dispatch an interactive Claude session\n"
                + "  Ctrl+B      Open the task in your browser\n"
                + "  Ctrl+P      Pin / unpin (pinned tasks group at the top)\n"
                + "  Ctrl+R      Refresh now\n"
                + "  F1          This help\n"
                + "  F2          Settings (refresh rate, excluded statuses)\n"
                + "  F3          Filter / sort / group the list\n"
                + "  F4          Show / hide subtasks (shown nested under their parent)\n"
                + "  Ctrl+Q/Esc  Quit\n"
                + "\n"
                + "  Settings, the status picker, the task detail, and this help open as full-window\n"
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

    public override void OnShown() => SetFocus();
}
