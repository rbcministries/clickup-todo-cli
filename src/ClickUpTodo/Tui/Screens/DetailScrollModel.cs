using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure scroll-target logic for the task detail view's Stream tab (issue #107, S2 of the #102 epic),
/// factored out of the Terminal.Gui glue so it's unit-testable without a terminal — the same
/// pure-glue split as <see cref="DispatchPaneModel"/> / <see cref="StatusPickerModel"/>. It maps the
/// user's <see cref="StreamAutoScroll"/> preference (which is expressed relative to <em>content
/// meaning</em>) to a concrete viewport <see cref="Edge"/>, given the Stream's current sort
/// direction. The glue then calls <c>TextView.MoveEnd()</c> / <c>MoveHome()</c> for that edge.
/// </summary>
public static class DetailScrollModel
{
    /// <summary>A viewport edge to scroll a scrollable pane to.</summary>
    public enum Edge
    {
        /// <summary>The first line (top) — <c>TextView.MoveHome()</c>.</summary>
        Top,

        /// <summary>The last line (bottom) — <c>TextView.MoveEnd()</c>.</summary>
        Bottom,
    }

    /// <summary>
    /// Which viewport edge realises the auto-scroll <paramref name="preference"/> for the Stream body
    /// currently rendered in <paramref name="sort"/> order. The Stream lays out oldest→newest when
    /// <see cref="StreamSort.Ascending"/> (Description/oldest at the top, newest comment at the bottom)
    /// and newest→oldest when <see cref="StreamSort.Descending"/>, so "newest" and "oldest" flip which
    /// edge they land on with the sort:
    /// <list type="bullet">
    /// <item><c>Newest</c> + <c>Ascending</c> → <see cref="Edge.Bottom"/></item>
    /// <item><c>Newest</c> + <c>Descending</c> → <see cref="Edge.Top"/></item>
    /// <item><c>Oldest</c> + <c>Ascending</c> → <see cref="Edge.Top"/></item>
    /// <item><c>Oldest</c> + <c>Descending</c> → <see cref="Edge.Bottom"/></item>
    /// </list>
    /// Equivalently: the newest entry is at the top exactly when the sort is descending.
    /// </summary>
    public static Edge ResolveEdge(StreamAutoScroll preference, StreamSort sort)
    {
        var newestIsAtTop = sort == StreamSort.Descending;
        var wantNewest = preference == StreamAutoScroll.Newest;
        return wantNewest == newestIsAtTop ? Edge.Top : Edge.Bottom;
    }

    // ── Bare ↑/↓ line-scroll / row-move arithmetic (#452) ────────────────────────────────────────
    //
    // The glue (TaskDetailScreen.OnKey) claims a bare ↑/↓ on a front-most tab and calls these to
    // compute the clamped destination. They are pure and edge-saturating: at the top/bottom the
    // "next" value equals the input, so the glue can consume the key unconditionally and a boundary
    // press is a no-op that never falls through to NavSafeTabs (which would otherwise swallow it via
    // the crash-guard, or — pre-fix — let the ListView's Command bubble up and cancel its own move).

    /// <summary>
    /// The greatest top row a scrollable pane can show without wasting space below the content:
    /// <c>max(0, lineCount − max(1, viewportHeight))</c>. Matches the existing <c>MaxTopRow</c> glue,
    /// factored here so the one-line scroll and the refresh scroll-restore share it.
    /// </summary>
    public static int MaxTop(int lineCount, int viewportHeight) =>
        Math.Max(0, lineCount - Math.Max(1, viewportHeight));

    /// <summary>
    /// The next top row after scrolling <paramref name="delta"/> lines (±1 for ↑/↓) from
    /// <paramref name="currentTop"/>, clamped to <c>[0, <see cref="MaxTop"/>]</c>. Returns
    /// <paramref name="currentTop"/> unchanged at either edge, so the caller can tell "scrolled" from
    /// "already at the boundary" while still consuming the key.
    /// </summary>
    public static int NextTop(int currentTop, int viewportHeight, int lineCount, int delta) =>
        Math.Clamp(currentTop + delta, 0, MaxTop(lineCount, viewportHeight));

    /// <summary>
    /// The next selected index after moving <paramref name="delta"/> rows (±1 for ↑/↓) from
    /// <paramref name="currentIndex"/> in a list of <paramref name="count"/> items, clamped to
    /// <c>[0, count − 1]</c>. Returns <paramref name="currentIndex"/> for an empty list and at either
    /// end, so a boundary press is a no-op the caller still consumes.
    /// </summary>
    public static int NextIndex(int currentIndex, int count, int delta) =>
        count <= 0 ? currentIndex : Math.Clamp(currentIndex + delta, 0, count - 1);
}
