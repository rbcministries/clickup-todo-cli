namespace ClickUpTodo.Tui;

/// <summary>What activating a <see cref="LinkSpan"/> should do (#318).</summary>
public enum LinkAction
{
    /// <summary>Hand the link's URL to the system browser.</summary>
    OpenInBrowser,

    /// <summary>Open the linked ClickUp task's Task Detail in-app.</summary>
    OpenTaskDetail,
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
    /// The action for activating <paramref name="span"/>. <paramref name="ctrl"/> (Windows Terminal's own
    /// "open this link" gesture) always means the browser, whatever the link kind; an unmodified
    /// activation follows the kind — a ClickUp task link opens in-app, anything else in the browser.
    /// <para>
    /// A <see cref="LinkSpan.IsCustomTaskId"/> task link is no different here: it is still an in-app
    /// open, and resolving the custom id to a task is the host's job (it already does exactly that for a
    /// pasted custom-id URL, #353). #320 extends the plain-click arm with a configurable task
    /// destination (browser ↔ new terminal tab) and a Shift inversion; this is that arm's one caller.
    /// </para>
    /// </summary>
    public static LinkAction Resolve(LinkSpan span, bool ctrl)
        => !ctrl && span.Kind == LinkKind.Task ? LinkAction.OpenTaskDetail : LinkAction.OpenInBrowser;

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
