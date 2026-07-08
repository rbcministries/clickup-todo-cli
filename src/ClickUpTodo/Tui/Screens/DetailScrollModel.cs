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
}
