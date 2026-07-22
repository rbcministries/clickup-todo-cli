namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure key-routing for Task Detail tab navigation (issue #315), factored out of the Terminal.Gui
/// glue in <see cref="TaskDetailScreen"/> so the binding and its guard are unit-testable without a
/// terminal — the same pure-glue split as <see cref="DispatchPaneModel"/>. Tab switching moved off
/// bare <c>Tab</c>/<c>Shift+Tab</c> onto <c>Ctrl+→</c>/<c>Ctrl+←</c> so bare <c>Tab</c>/<c>Shift+Tab</c>
/// free up for in-pane link focus traversal (#319, <b>E</b>).
/// </summary>
public static class DetailTabNav
{
    /// <summary>The tab-navigation chords this screen intercepts; the glue classifies a Terminal.Gui
    /// key into one of these, and anything else is <see cref="NavKey.Other"/> and falls through to the
    /// focused control (so a pane's own cursor movement keeps working).</summary>
    public enum NavKey
    {
        CtrlRight,
        CtrlLeft,
        Other,
    }

    /// <summary>What the glue should do for a classified key.</summary>
    public enum NavAction
    {
        CycleForward,
        CycleBackward,
        None,
    }

    /// <summary>
    /// Maps a tab-nav chord to its action. Inert (<see cref="NavAction.None"/>) while the Dispatch
    /// prompt is open: its dir-browser owns bare <c>←</c>/<c>→</c> and its text fields own cursor
    /// movement, so the screen must not steal the Ctrl chord to cycle tabs underneath. This also
    /// preserves the pre-#315 behaviour where the open Dispatch pane consumed <c>Tab</c> before it
    /// could ever reach the tab cycle.
    /// </summary>
    public static NavAction Route(NavKey key, bool promptOpen)
    {
        if (promptOpen)
            return NavAction.None;
        return key switch
        {
            NavKey.CtrlRight => NavAction.CycleForward,
            NavKey.CtrlLeft => NavAction.CycleBackward,
            _ => NavAction.None,
        };
    }

    /// <summary>
    /// The next tab index when cycling with <c>Ctrl+→</c> (<paramref name="forward"/>) / <c>Ctrl+←</c>,
    /// wrapping at both ends. Returns 0 for a non-positive <paramref name="count"/>. Delegates to
    /// <see cref="DispatchPaneModel.NextFocus"/> so tab cycling and the Dispatch pane's focus cycling
    /// share one wraparound implementation.
    /// </summary>
    public static int NextTab(int current, int count, bool forward)
        => DispatchPaneModel.NextFocus(current, count, forward);
}
