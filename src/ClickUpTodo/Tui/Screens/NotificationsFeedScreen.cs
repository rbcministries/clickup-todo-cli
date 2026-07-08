using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The Mentions &amp; Comments feed screen (#109). This is the walking-skeleton scaffold (#110): a
/// full-window screen that opens from the dashboard, renders a static empty-state placeholder, and
/// closes back to the list — no comment fetching, no service calls, no real rows yet (those land in
/// #112–#116). Built on the shared screen-navigation seam (#38), not a nested <c>Dialog</c>/
/// <c>Application.Run</c> loop, so the single-toplevel #3/#38 invariants hold.
/// </summary>
public sealed class NotificationsFeedScreen : Screen
{
    /// <summary>
    /// The empty-state copy shown until real feed data is wired in (#114). Kept as a constant so the
    /// copy is unit-testable without instantiating the Terminal.Gui view (the test suite never calls
    /// <c>Application.Init</c>), mirroring the repo's pure-surface testing pattern.
    /// </summary>
    public const string EmptyStatePlaceholder =
        "No mentions or comments to show yet.\n"
        + "\n"
        + "This feed will list recent comments and @-mentions across the tasks assigned to you,\n"
        + "newest first, once the backend is wired in.\n"
        + "\n"
        + "Press Esc to return to your tasks.";

    public NotificationsFeedScreen()
    {
        Title = "Feed — mentions & comments";

        // The shared footer (#103) carries the shortcuts, so the body fills the whole screen area.
        var body = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            Text = "\n" + EmptyStatePlaceholder,
        };

        KeyDown += (_, key) =>
        {
            switch (key.KeyCode)
            {
                case KeyCode.F1:
                    key.Handled = true;
                    RequestHelp();
                    break;
                case KeyCode.Esc:
                    key.Handled = true;
                    Close();
                    break;
            }
        };

        Add(body);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.NotificationsFeed;

    public override void OnShown() => SetFocus();
}
