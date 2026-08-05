using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure logic for the reply-target picker (issue #330, sub-issue D of the Threaded comments epic #314):
/// projects the detail view's <b>top-level</b> comment list into the pick list the transient picker
/// overlay shows, so a user can choose which comment's thread to reply into. Factored out of the
/// Terminal.Gui glue so it's unit-testable without a terminal — the same pure-glue split as
/// <see cref="CommentComposerModel"/>.
/// </summary>
/// <remarks>
/// Only top-level comments are offered: ClickUp threads are one level deep (the reply endpoint is keyed
/// by the parent comment), and a reply nests directly under its parent (#329). A <b>pending</b>
/// (optimistic, not-yet-confirmed) comment is excluded — you can't reply to a comment the server hasn't
/// acknowledged. Targets are ordered <b>newest-first</b> (id tiebreak) so the most recent comments — the
/// likely reply targets — sit at the top.
/// </remarks>
public static class CommentReplyModel
{
    /// <summary>Max length of the comment snippet shown in a picker row before it's ellipsised.</summary>
    public const int SnippetMaxLength = 48;

    /// <summary>A pickable reply target: the parent comment's <paramref name="CommentId"/> (what the
    /// reply is posted to), its <paramref name="Author"/> (for the composer's "Reply to …" title), and
    /// the one-line <paramref name="Label"/> rendered in the picker.</summary>
    public readonly record struct ReplyTarget(string CommentId, string Author, string Label);

    /// <summary>The reply targets for <paramref name="comments"/>: top-level, non-pending, real-id
    /// comments, newest-first.</summary>
    public static IReadOnlyList<ReplyTarget> Targets(IReadOnlyList<CommentItem> comments)
        => [.. comments
            .Where(c => !string.IsNullOrEmpty(c.Id) && !CommentComposerModel.IsPending(c.Id))
            .OrderByDescending(c => c.DateMs ?? 0)
            .ThenByDescending(c => c.Id, StringComparer.Ordinal)
            .Select(ToTarget)];

    /// <summary>True when at least one comment can be replied to (the chord is inert otherwise).</summary>
    public static bool HasTargets(IReadOnlyList<CommentItem> comments) => Targets(comments).Count > 0;

    private static ReplyTarget ToTarget(CommentItem c) => new(c.Id, DisplayAuthor(c.Author), Label(c));

    /// <summary>A picker row label: <c>"{author} · {snippet}"</c>, with a <c>" (N replies)"</c> suffix
    /// when the thread is non-empty; just the author when the body is blank.</summary>
    internal static string Label(CommentItem c)
    {
        var author = DisplayAuthor(c.Author);
        var snippet = Snippet(c.Text);
        var head = snippet.Length == 0 ? author : $"{author} · {snippet}";
        return c.ReplyCount > 0
            ? $"{head} ({c.ReplyCount} {(c.ReplyCount == 1 ? "reply" : "replies")})"
            : head;
    }

    /// <summary>The author shown for a comment — <c>"(you)"</c> for a blank author, which in this app is
    /// the just-posted optimistic comment before the 30s refresh fills the authoritative name.</summary>
    internal static string DisplayAuthor(string? author)
        => string.IsNullOrWhiteSpace(author) ? "(you)" : author.Trim();

    /// <summary>A single-line, length-capped preview of a comment body: newlines collapsed to spaces,
    /// trimmed, and ellipsised past <see cref="SnippetMaxLength"/>. Empty for a blank body.</summary>
    internal static string Snippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var oneLine = string.Join(" ", text
            .Split('\n', '\r')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
        return oneLine.Length <= SnippetMaxLength
            ? oneLine
            : string.Concat(oneLine.AsSpan(0, SnippetMaxLength - 1).TrimEnd(), "…");
    }
}
