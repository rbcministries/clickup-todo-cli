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
    /// The display line plus the char span of the leading mention badge and the decoupled type-ahead
    /// search key. When the comment doesn't mention the current user the badge is absent — its
    /// <see cref="MentionLength"/> is 0 and its <see cref="MentionStart"/> is -1, the "no badge"
    /// sentinel <see cref="StatusBadgeListSource.TryCreate"/> reads. <see cref="SearchKey"/> is the
    /// author only (the title analog), so type-ahead jumps by author even though the rendered line
    /// leads with the gutter (same decoupling task rows use for titles, #76).
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

        text += Separator + Preview(comment.Text);

        return new Row(text, mentionStart, mentionLength, author);
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

    private static string FormatDate(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MMM d, HH:mm");
}
