using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui;

/// <summary>
/// Pure decision for what a <c>Ctrl+B</c> (open-in-browser) gesture does to the detail view it was
/// pressed on (#518). The caller always launches the browser; this only says whether to <b>also</b>
/// close the view. Kept out of the Terminal.Gui hosts so the one place the invariant and the setting
/// compose is unit-testable.
/// </summary>
public static class OpenBrowserAction
{
    /// <summary>
    /// Whether the detail view should close (navigate back) after Ctrl+B, given the persisted
    /// <paramref name="setting"/> and whether the view is its host's <paramref name="isRoot"/> screen.
    /// <para>
    /// The invariant wins over the setting: a root view has no back to navigate to, so it never closes
    /// — Ctrl+B must never reach an exit path. For a non-root view, the setting decides.
    /// </para>
    /// </summary>
    public static bool ShouldCloseView(OpenBrowserBehavior setting, bool isRoot) =>
        !isRoot && setting == OpenBrowserBehavior.CloseView;
}
