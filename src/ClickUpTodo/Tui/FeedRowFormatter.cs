using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// Builds the one-line display text for a feed comment row (#114, epic #109) and reports where the
/// mention badge sits within it, so the list renderer can colour exactly that span. A row leads with a
/// fixed-width gutter — a coloured <c> @ </c> chip when the comment mentions the current user, else a
/// blank gutter so authors still line up — followed by the author, the date, and a one-line preview of
/// the comment text. Pure (no Terminal.Gui), so the layout and the badge span are unit-testable;
/// mirrors <see cref="TaskRowFormatter"/> (which does the same for task rows).
/// </summary>
public static class FeedRowFormatter
{
    /// <summary>
    /// The display line plus the char span of the leading gutter chip and the decoupled type-ahead
    /// search key. The chip is a comment's mention badge (#113) or an activity row's chip (#117); both
    /// use the same span fields, coloured per row-kind by the renderer. When there is no chip (a
    /// non-mention comment) the span is absent — its <see cref="MentionLength"/> is 0 and its
    /// <see cref="MentionStart"/> is -1, the "no badge" sentinel <see cref="StatusBadgeListSource.TryCreate"/>
    /// reads. <see cref="SearchKey"/> is the row's primary text (comment author / task name), so
    /// type-ahead jumps by it even though the rendered line leads with the gutter (same decoupling task
    /// rows use for titles, #76).
    /// </summary>
    public readonly record struct Row(string Text, int MentionStart, int MentionLength, string SearchKey);

    /// <summary>The mention chip: an <c>@</c> glyph flanked by a space on each side (coloured with
    /// <see cref="MentionBadgeColor"/> by the renderer). Three display columns — the same width as the
    /// <see cref="BlankGutter"/> and the dashboard's <see cref="TaskRowFormatter.PriorityIcon"/> — so
    /// authors line up across mention and non-mention rows.</summary>
    public const string MentionChip = " @ ";

    /// <summary>The blank gutter shown on a non-mention row — same width as <see cref="MentionChip"/>
    /// so titles still line up.</summary>
    public const string BlankGutter = "   ";

    /// <summary>The recent-activity chip (#117): a <c>~</c> glyph flanked by a space on each side,
    /// coloured with <see cref="ActivityBadgeColor"/> by the renderer. Three display columns — the same
    /// width as <see cref="MentionChip"/> / <see cref="BlankGutter"/> — so task-activity rows align with
    /// comment rows in the merged feed.</summary>
    public const string ActivityChip = " ~ ";

    /// <summary>The fixed colour of the recent-activity chip: a cool blue accent, deliberately distinct
    /// from the amber <see cref="MentionBadgeColor"/> so an activity row reads as "a task changed" rather
    /// than a mention or a status/priority badge. Passed to
    /// <see cref="StatusBadgeListSource.TryCreate"/> by the view layer.</summary>
    public const string ActivityBadgeColor = "#4aa3df";

    /// <summary>Placeholder shown for an activity entry whose task has no name.</summary>
    public const string UntitledTask = "(untitled task)";

    /// <summary>Separator between the row's fields (author · date · preview), matching the detail
    /// view's comment-header separator.</summary>
    public const string Separator = "  ·  ";

    /// <summary>The fixed colour of the mention chip: a warm amber accent, deliberately not a ClickUp
    /// field colour, so a mention reads as "you were mentioned" rather than as a status/priority
    /// badge. Passed to <see cref="StatusBadgeListSource.TryCreate"/> by the view layer.</summary>
    public const string MentionBadgeColor = "#f5c518";

    /// <summary>Placeholder shown for a comment with no author.</summary>
    public const string UnknownAuthor = "(unknown)";

    /// <summary>Placeholder shown for a comment with no text.</summary>
    public const string EmptyComment = "(empty comment)";

    /// <summary>Longest comment preview rendered on the row before it's truncated with an ellipsis.</summary>
    public const int MaxPreviewLength = 100;

    /// <summary>
    /// Formats one feed row. The mention chip (when the comment mentions the current user) leads the
    /// line and is the only coloured span; a non-mention row leads with the blank gutter and reports
    /// no span. The badge offset is captured from the running text length so the span stays exact.
    /// </summary>
    public static Row Format(CommentItem comment)
    {
        var text = "";
        var (mentionStart, mentionLength) = (-1, 0);

        if (comment.MentionsMe)
        {
            mentionStart = text.Length; // 0 — the chip leads the row
            mentionLength = MentionChip.Length;
            text += MentionChip;
        }
        else
        {
            text += BlankGutter;
        }

        var author = string.IsNullOrWhiteSpace(comment.Author) ? UnknownAuthor : comment.Author.Trim();
        text += author;

        if (comment.DateMs is { } ms)
            text += Separator + FormatDate(ms);

        // Threaded comments (#329): the feed collapses a thread to a reply count rather than nesting the
        // replies — the feed never loads reply bodies (a feed-wide fan-out would be an unbounded storm, so
        // #328 deliberately skips it), but every comment carries an accurate ReplyCount from MapComment. Placed
        // before the (truncatable) preview so a long preview can't clip it off the end of the row.
        if (comment.ReplyCount > 0)
            text += Separator + ReplyCountLabel(comment.ReplyCount);

        text += Separator + Preview(comment.Text);

        return new Row(text, mentionStart, mentionLength, author);
    }

    /// <summary>
    /// Formats one recent-activity row (#117). The activity chip always leads the line (unlike a comment
    /// row's conditional mention chip) and is the only coloured span — reported the same way as the
    /// mention chip so the renderer colours exactly that gutter (with <see cref="ActivityBadgeColor"/>).
    /// The chip is followed by the task name, its last-updated date, and its current status.
    /// <see cref="Row.SearchKey"/> is the task name, so type-ahead jumps by task title.
    /// </summary>
    public static Row Format(ActivityItem activity)
    {
        var text = ActivityChip; // always leads — the chip start is 0, its length the chip width
        var (chipStart, chipLength) = (0, ActivityChip.Length);

        var name = string.IsNullOrWhiteSpace(activity.TaskName) ? UntitledTask : activity.TaskName.Trim();
        text += name;

        if (activity.UpdatedMs is { } ms)
            text += Separator + FormatDate(ms);

        if (!string.IsNullOrWhiteSpace(activity.StatusName))
            text += Separator + activity.StatusName.Trim();

        return new Row(text, chipStart, chipLength, name);
    }

    /// <summary>Flattens a comment's (possibly multi-line) text to a single line — runs of whitespace
    /// collapse to one space — and truncates to <see cref="MaxPreviewLength"/> chars with an ellipsis.
    /// A blank comment yields <see cref="EmptyComment"/>.</summary>
    private static string Preview(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return EmptyComment;

        var flattened = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flattened.Length <= MaxPreviewLength)
            return flattened;

        // Truncate by UTF-16 length, but never mid-surrogate-pair — a lone high surrogate before the
        // ellipsis would render as a replacement glyph (comment text often carries emoji).
        var cut = MaxPreviewLength;
        if (char.IsHighSurrogate(flattened[cut - 1]))
            cut--;
        return flattened[..cut] + "…";
    }

    /// <summary>The reply-count field for a feed row with a thread (#329): <c>"1 reply"</c> for a single
    /// reply, <c>"N replies"</c> otherwise. Callers guard on <c>ReplyCount &gt; 0</c>, so this never renders
    /// a zero count.</summary>
    public static string ReplyCountLabel(int count)
        => count == 1 ? "1 reply" : $"{count} replies";

    private static string FormatDate(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MMM d, HH:mm");
}
