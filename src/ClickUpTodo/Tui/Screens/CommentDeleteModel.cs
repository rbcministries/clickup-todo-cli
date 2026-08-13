using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure logic for the Task Detail comment-delete picker (issue #594, the deferred comment half of the
/// contextual-Delete slice #543): projects the detail view's comment list into the pick list the transient
/// delete-picker overlay shows, and owns the optimistic <see cref="Remove"/> transform the screen applies
/// while the <see cref="IClickUpClient.DeleteCommentAsync"/> write is in flight. Factored out of the
/// Terminal.Gui glue so it's unit-testable without a terminal — the same pure-glue split as
/// <see cref="CommentReplyModel"/> / <see cref="CommentComposerModel"/>.
/// </summary>
/// <remarks>
/// Unlike the reply picker (which offers <b>top-level</b> comments only, since a reply is keyed by its
/// parent), the delete picker offers <b>every</b> deletable comment — top-level and the loaded replies
/// nested under them — because <c>DELETE /comment/{id}</c> works on any comment id (#594). A <b>pending</b>
/// (optimistic, not-yet-confirmed) comment is excluded — you can't delete one the server hasn't
/// acknowledged. Top-level comments are ordered <b>newest-first</b> (id tiebreak) to match the reply
/// picker; each thread's replies follow their parent in their loaded (oldest-first) order, <c>↳</c>-prefixed
/// so the nesting reads at a glance.
/// </remarks>
public static class CommentDeleteModel
{
    /// <summary>A pickable delete target: the comment's <paramref name="CommentId"/> (what
    /// <c>DELETE /comment/{id}</c> is issued for), its display <paramref name="Author"/> (for the confirm
    /// prompt), the one-line <paramref name="Label"/> rendered in the picker, and whether it is a
    /// <paramref name="IsReply"/> (a nested reply rather than a top-level comment).</summary>
    public readonly record struct DeleteTarget(string CommentId, string Author, string Label, bool IsReply);

    /// <summary>The delete targets for <paramref name="comments"/>: every non-pending, real-id comment —
    /// top-level newest-first, each thread's replies nested under their parent (oldest-first,
    /// <c>↳</c>-prefixed).</summary>
    public static IReadOnlyList<DeleteTarget> Targets(IReadOnlyList<CommentItem> comments)
    {
        var targets = new List<DeleteTarget>();
        var ordered = comments
            .Where(IsDeletable)
            .OrderByDescending(c => c.DateMs ?? 0)
            .ThenByDescending(c => c.Id, StringComparer.Ordinal);
        foreach (var top in ordered)
        {
            targets.Add(new DeleteTarget(top.Id, CommentReplyModel.DisplayAuthor(top.Author),
                CommentReplyModel.Label(top), IsReply: false));
            foreach (var reply in top.Replies.Where(IsDeletable))
                targets.Add(new DeleteTarget(reply.Id, CommentReplyModel.DisplayAuthor(reply.Author),
                    ReplyLabel(reply), IsReply: true));
        }
        return targets;
    }

    /// <summary>True when at least one comment (top-level or reply) can be deleted (the chord flashes
    /// "nothing to delete" otherwise).</summary>
    public static bool HasTargets(IReadOnlyList<CommentItem> comments) => Targets(comments).Count > 0;

    /// <summary>
    /// The optimistic-removal transform: drops the comment identified by <paramref name="commentId"/> from
    /// <paramref name="comments"/> (immutably). A <b>top-level</b> match removes the comment and its whole
    /// thread; otherwise the id is a <b>reply</b>, removed from its parent's <see cref="CommentItem.Replies"/>
    /// with the parent's <see cref="CommentItem.ReplyCount"/> kept in step. A no-op returning the list
    /// unchanged when the id is no longer present (a background refresh replaced it mid-write) — self-healing,
    /// like the composer's reconcile/revert.
    /// </summary>
    public static IReadOnlyList<CommentItem> Remove(IReadOnlyList<CommentItem> comments, string commentId)
    {
        // A top-level match drops the comment (and its nested thread).
        if (comments.Any(c => IdEquals(c.Id, commentId)))
            return [.. comments.Where(c => !IdEquals(c.Id, commentId))];

        // Otherwise the id names a reply: drop it from its parent's Replies, keeping ReplyCount in step.
        return [.. comments.Select(c =>
        {
            var kept = c.Replies.Where(r => !IdEquals(r.Id, commentId)).ToList();
            return kept.Count == c.Replies.Count
                ? c
                : c with { Replies = kept, ReplyCount = Math.Max(0, c.ReplyCount - 1) };
        })];
    }

    private static bool IsDeletable(CommentItem c)
        => !string.IsNullOrEmpty(c.Id) && !CommentComposerModel.IsPending(c.Id);

    private static bool IdEquals(string? a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>A reply's picker row: the reply-nesting glyph <c>↳</c> then the same
    /// <c>"{author} · {snippet}"</c> head the reply picker uses (a reply has no sub-thread of its own, so no
    /// reply-count suffix).</summary>
    private static string ReplyLabel(CommentItem reply)
    {
        var author = CommentReplyModel.DisplayAuthor(reply.Author);
        var snippet = CommentReplyModel.Snippet(reply.Text);
        return snippet.Length == 0 ? $"↳ {author}" : $"↳ {author} · {snippet}";
    }
}
