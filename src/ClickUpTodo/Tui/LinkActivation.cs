using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui;

/// <summary>What activating a <see cref="LinkSpan"/> should do (#318, extended in #320).</summary>
public enum LinkAction
{
    /// <summary>Hand the link's URL to the system browser.</summary>
    OpenInBrowser,

    /// <summary>Open the linked ClickUp task's Task Detail in-app.</summary>
    OpenTaskDetail,

    /// <summary>Open the linked task in a new terminal tab (<c>clickup-todo --task</c>, #320).</summary>
    OpenTaskInNewTab,
}

/// <summary>
/// One resolved link activation: the <see cref="LinkSpan"/> the user acted on and the
/// <see cref="LinkAction"/> that <see cref="LinkActivator.Resolve"/> chose for it. Raised by
/// <see cref="DetailPaneView"/> (mouse, #318) and — once it lands — the keyboard path (#319), so both
/// gestures reach a host through one payload.
/// </summary>
public readonly record struct LinkActivationRequest(LinkSpan Span, LinkAction Action)
{
    /// <summary>The link target, i.e. <see cref="LinkSpan.Url"/>.</summary>
    public string Url => Span.Url;
}

/// <summary>
/// The pure link-activation dispatcher for the task detail panes (#318): it maps a
/// <see cref="LinkSpan"/> plus the gesture's modifiers to a <see cref="LinkAction"/>, and provides the
/// two coordinate helpers a hit test needs. Terminal.Gui-free and unit-tested, so the click glue in
/// <see cref="DetailPaneView"/> stays thin and the keyboard path (#319) resolves links through the exact
/// same rules — click and <c>Enter</c> can't drift apart.
/// </summary>
public static class LinkActivator
{
    /// <summary>
    /// The action for activating <paramref name="span"/>.
    /// <list type="bullet">
    /// <item><description>A <b>web</b> link always opens in the browser, whatever the modifiers.</description></item>
    /// <item><description>A <b>task</b> link with <b>no</b> <paramref name="ctrl"/> opens in-app
    /// (the plain-click / <c>Enter</c> gesture); <paramref name="shift"/> is irrelevant here — a plain
    /// <c>Shift</c> activation isn't a gesture the pane admits.</description></item>
    /// <item><description>A <b>task</b> link with <paramref name="ctrl"/> follows the configured
    /// <paramref name="ctrlDestination"/> (#320) — <see cref="TaskLinkCtrlClickDestination.Browser"/> →
    /// browser, <see cref="TaskLinkCtrlClickDestination.NewTerminalTab"/> → a new terminal tab — and
    /// <paramref name="shift"/> <b>inverts</b> that choice (<c>Ctrl+Shift</c> does the other one).</description></item>
    /// </list>
    /// <para>
    /// A <see cref="LinkSpan.IsCustomTaskId"/> task link is no different here: resolving the custom id to
    /// a task is the host's job for every arm (it already does exactly that for a pasted custom-id URL,
    /// #353, and for the new-tab arm hands the id to <c>clickup-todo --task</c>).
    /// </para>
    /// </summary>
    public static LinkAction Resolve(
        LinkSpan span,
        bool ctrl,
        bool shift = false,
        TaskLinkCtrlClickDestination ctrlDestination = TaskLinkCtrlClickDestination.Browser)
    {
        if (span.Kind != LinkKind.Task)
            return LinkAction.OpenInBrowser;
        if (!ctrl)
            return LinkAction.OpenTaskDetail;
        // Ctrl on a task link follows the configured destination; Ctrl+Shift inverts it.
        var destination = shift ? ctrlDestination.Next() : ctrlDestination;
        return destination == TaskLinkCtrlClickDestination.NewTerminalTab
            ? LinkAction.OpenTaskInNewTab
            : LinkAction.OpenInBrowser;
    }

    /// <summary>
    /// The span in <paramref name="spans"/> covering <paramref name="charOffset"/>, or <c>null</c> when
    /// the offset falls between links (or outside them all). The test is
    /// <c>Start &lt;= offset &lt; End</c>, so the exclusive end — the position just past a link's last
    /// character, which is where a click right of the text clamps to — is deliberately <em>not</em> a hit.
    /// </summary>
    public static LinkSpan? SpanAt(IReadOnlyList<LinkSpan> spans, int charOffset)
    {
        if (charOffset < 0)
            return null;

        foreach (var span in spans)
        {
            if (charOffset < span.Start)
                break; // spans are in document order, so nothing later can contain the offset
            if (charOffset < span.End)
                return span;
        }

        return null;
    }

    /// <summary>
    /// Converts a <b>cell</b> (grapheme) index into the UTF-16 <b>char</b> offset that
    /// <see cref="LinkSpan"/> offsets are measured in, by summing the lengths of the
    /// <paramref name="graphemes"/> before <paramref name="cellIndex"/>. Terminal.Gui reports a clicked
    /// text position as a cell index, and a cell holds a whole grapheme cluster — so for
    /// <c>"ab 😀😀 https://…"</c> the URL's first cell is index 6 while its char offset is 8. Out-of-range
    /// indices clamp (negative → <c>0</c>; past the end → the total length), matching how the view
    /// clamps a click to the text it lands on.
    /// </summary>
    public static int CharOffsetAtCell(IReadOnlyList<string> graphemes, int cellIndex)
    {
        var offset = 0;
        var end = Math.Min(cellIndex, graphemes.Count);
        for (var i = 0; i < end; i++)
            offset += graphemes[i].Length;
        return offset;
    }
}
