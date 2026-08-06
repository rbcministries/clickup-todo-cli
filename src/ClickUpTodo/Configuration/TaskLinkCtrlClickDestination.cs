namespace ClickUpTodo.Configuration;

/// <summary>
/// Where a <c>Ctrl</c>+click on a <b>task</b> link in a detail pane goes (#320, epic #313): the
/// system <see cref="Browser"/> (the fixed behaviour #318 shipped) or a
/// <see cref="NewTerminalTab"/> opening that task via <c>clickup-todo --task</c>.
/// <c>Ctrl+Shift</c>+click performs the other one. A web link always opens in the browser, whatever
/// the modifiers — this setting governs task links only.
/// <para>
/// Lives in <c>Configuration</c> (not the Tui layer that acts on it) so it's the single source of
/// truth shared by the persisted detail-view default
/// (<see cref="DetailViewSettings.TaskLinkCtrlClick"/>) and the pure Tui dispatcher
/// (<c>LinkActivator.Resolve</c>) without Configuration depending on Tui.
/// </para>
/// </summary>
public enum TaskLinkCtrlClickDestination
{
    /// <summary>Open the task link in the system browser (the default, matching #318).</summary>
    Browser,

    /// <summary>Open the task in a new terminal tab (<c>clickup-todo --task &lt;id&gt;</c>).</summary>
    NewTerminalTab,
}

/// <summary>Pure helper over <see cref="TaskLinkCtrlClickDestination"/>, kept out of the Terminal.Gui
/// layer so it's unit-testable (the F2 cycle button and the dispatcher's Shift inversion share
/// it).</summary>
public static class TaskLinkCtrlClickDestinationExtensions
{
    /// <summary>The other destination — cycles the two-value setting for the F2 button, and inverts
    /// the configured default for a <c>Ctrl+Shift</c>+click (#320).</summary>
    public static TaskLinkCtrlClickDestination Next(this TaskLinkCtrlClickDestination destination) =>
        destination == TaskLinkCtrlClickDestination.Browser
            ? TaskLinkCtrlClickDestination.NewTerminalTab
            : TaskLinkCtrlClickDestination.Browser;
}
