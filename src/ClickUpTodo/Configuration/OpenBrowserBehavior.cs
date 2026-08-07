namespace ClickUpTodo.Configuration;

/// <summary>
/// What <c>Ctrl+B</c> (open the task in the browser) does to the detail view <em>when there is
/// somewhere to navigate back to</em> (#518). The browser always opens either way; this governs only
/// whether the detail view <b>also</b> closes.
/// <para>
/// At a host's root — the <c>--task</c> launch task — there is no back to navigate to, so the view
/// always stays regardless of this setting. That is the invariant: <c>Ctrl+B</c> must never exit the
/// application. This setting therefore only ever changes the <b>non-root</b> case; the two compose in
/// the pure <see cref="ClickUpTodo.Tui.OpenBrowserAction"/>.
/// </para>
/// <para>
/// Lives in <c>Configuration</c> (not the Tui layer that acts on it) so it is the single source of
/// truth shared by the persisted default (<see cref="DetailViewSettings.OpenBrowser"/>) and the pure
/// decision helper, without Configuration depending on Tui — mirroring
/// <see cref="TaskLinkCtrlClickDestination"/>.
/// </para>
/// </summary>
public enum OpenBrowserBehavior
{
    /// <summary>Open the browser and stay in the detail view (the default, #518). Matches the in-screen
    /// Ctrl+click-a-task-link precedent (#318/#320) and makes both hosts behave identically.</summary>
    KeepOpen,

    /// <summary>Open the browser and close the detail view — return to the main list (dashboard) or pop
    /// back to the parent detail (a Task Tree child, #374). The behaviour that shipped before this
    /// setting existed.</summary>
    CloseView,
}

/// <summary>Pure helper over <see cref="OpenBrowserBehavior"/>, kept out of the Terminal.Gui layer so
/// the F2 cycle button can share it (mirrors <see cref="TaskLinkCtrlClickDestinationExtensions"/>).</summary>
public static class OpenBrowserBehaviorExtensions
{
    /// <summary>The other value — cycles the two-value setting for the F2 button.</summary>
    public static OpenBrowserBehavior Next(this OpenBrowserBehavior behavior) =>
        behavior == OpenBrowserBehavior.KeepOpen
            ? OpenBrowserBehavior.CloseView
            : OpenBrowserBehavior.KeepOpen;
}
