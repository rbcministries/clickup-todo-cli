namespace ClickUpTodo.Tui;

/// <summary>
/// One clickable link located in a task-detail text pane: which <b>source line</b> it sits on
/// (<see cref="LineIndex"/>, an index into the body split on <c>'\n'</c>) and its <see cref="LinkSpan"/>
/// within that line. Produced in document order by <see cref="DetailPaneView.ExtractPaneLinks"/> so the
/// keyboard focus traversal (#319) has a stable, ordered set of links to step through — the keyboard
/// counterpart of the per-line hit test the mouse path (#318) runs on a click.
/// </summary>
public readonly record struct PaneLink(int LineIndex, LinkSpan Span);

/// <summary>
/// The pure focus-index math for stepping <c>Tab</c>/<c>Shift+Tab</c> across a pane's links (#319).
/// Terminal.Gui-free and unit-tested, so the (untestable) pane draw/scroll glue in
/// <see cref="DetailPaneView"/> stays thin — the repo pattern shared with <see cref="DetailTabNav"/> and
/// <see cref="DispatchPaneModel"/>.
/// </summary>
public static class LinkFocus
{
    /// <summary>The sentinel <see cref="DetailPaneView"/> focus index for "no link focused".</summary>
    public const int None = -1;

    /// <summary>
    /// The next focused link index after a <c>Tab</c> (<paramref name="forward"/>) or <c>Shift+Tab</c>
    /// (<c>!forward</c>) over <paramref name="count"/> links, given the <paramref name="current"/> index
    /// (<see cref="None"/> when nothing is focused yet). Wraps around at both ends — there is nothing else
    /// on a text tab to move focus to, so cycling is the least-surprising behavior (#319 leaves the choice
    /// open). From <see cref="None"/> the first <c>Tab</c> lands on the first link and the first
    /// <c>Shift+Tab</c> on the last. Returns <see cref="None"/> when there are no links.
    /// </summary>
    public static int Step(int current, int count, bool forward)
    {
        if (count <= 0)
            return None;
        if (current < 0)
            return forward ? 0 : count - 1;
        return forward ? (current + 1) % count : (current - 1 + count) % count;
    }
}
