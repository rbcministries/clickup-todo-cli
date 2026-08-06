using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure logic for the Task Detail comment composer (issue #216, G of the #90-style Writing New
/// Content epic #208), factored out of the Terminal.Gui glue so it's unit-testable without a
/// terminal — the same pure-glue split as <see cref="DispatchPaneModel"/>. It decides which action a
/// key maps to while the composer is open, gates an empty submission, and owns the optimistic
/// append / reconcile / revert transforms over the detail view's comment list (mirroring the
/// <c>ApplyStatus</c> optimistic/revert discipline in <c>TodoApp</c>).
/// </summary>
public static class CommentComposerModel
{
    /// <summary>The keys the composer intercepts. The glue classifies a Terminal.Gui <c>Key</c> into
    /// one of these; anything else is <see cref="ComposerKey.Other"/> and passes through to the editor
    /// (so typing and <c>Enter</c>-inserts-a-newline keep working in the multi-line body).</summary>
    public enum ComposerKey
    {
        Submit,
        Cancel,
        Other,
    }

    /// <summary>What the glue should do for a classified key.</summary>
    public enum ComposerAction
    {
        Post,
        Cancel,
        PassThrough,
    }

    /// <summary>Maps a key to its action: submit posts, escape cancels, everything else falls through
    /// to the focused editor so multi-line typing is undisturbed.</summary>
    public static ComposerAction Route(ComposerKey key) => key switch
    {
        ComposerKey.Submit => ComposerAction.Post,
        ComposerKey.Cancel => ComposerAction.Cancel,
        _ => ComposerAction.PassThrough,
    };

    /// <summary>True when <paramref name="text"/> has content worth posting (ClickUp rejects an empty
    /// <c>comment_text</c>, so an all-whitespace body is a no-op, not a failed round-trip).</summary>
    public static bool IsPostable(string? text) => !string.IsNullOrWhiteSpace(text);

    /// <summary>The comment body as it is sent — trimmed of surrounding whitespace/newlines.</summary>
    public static string Normalize(string? text) => (text ?? string.Empty).Trim();

    /// <summary>The prefix of a provisional (optimistic, not-yet-confirmed) comment/reply id. Shared so
    /// the reply-target picker can exclude a pending comment (you can't reply to one the server hasn't
    /// confirmed) and the screen builds ids the reconcile/revert transforms can find.</summary>
    public const string PendingIdPrefix = "__pending__";

    /// <summary>True when <paramref name="id"/> is a client-side provisional sentinel (see
    /// <see cref="PendingIdPrefix"/>), not a server-assigned comment id.</summary>
    public static bool IsPending(string? id)
        => id is not null && id.StartsWith(PendingIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Builds the optimistic (provisional) comment appended the instant the user posts, before the
    /// server confirms. <paramref name="id"/> is a client-side sentinel so <see cref="Reconcile"/> /
    /// <see cref="Revert"/> can find it; the author is left blank (the create-comment facade #210
    /// returns no author either, and the 30s auto-refresh fills the authoritative one); the caller
    /// stamps <paramref name="dateMs"/> with a client "now" so it sorts as the newest entry.
    /// </summary>
    public static CommentItem Provisional(string id, string? text, long? dateMs)
        => new(Id: id, Author: "", DateMs: dateMs, Text: Normalize(text), Resolved: false);

    /// <summary>Appends <paramref name="provisional"/> to the current comment list (immutably).</summary>
    public static IReadOnlyList<CommentItem> Append(
        IReadOnlyList<CommentItem> comments, CommentItem provisional)
        => [.. comments, provisional];

    /// <summary>Replaces the provisional comment (matched by <paramref name="provisionalId"/>) with the
    /// server-<paramref name="confirmed"/> one on a successful post. A no-op if the provisional is no
    /// longer present (e.g. a background refresh replaced the list mid-post — the next refresh re-pulls
    /// the real comment).</summary>
    public static IReadOnlyList<CommentItem> Reconcile(
        IReadOnlyList<CommentItem> comments, string provisionalId, CommentItem confirmed)
        => [.. comments.Select(c => string.Equals(c.Id, provisionalId, StringComparison.Ordinal) ? confirmed : c)];

    /// <summary>Drops the provisional comment (matched by <paramref name="provisionalId"/>) when the
    /// post fails, reverting the optimistic append.</summary>
    public static IReadOnlyList<CommentItem> Revert(
        IReadOnlyList<CommentItem> comments, string provisionalId)
        => [.. comments.Where(c => !string.Equals(c.Id, provisionalId, StringComparison.Ordinal))];

    // ── @-mention authoring (#325, sub-issue K of #313) ───────────────────────────────────────
    // The composer lets the user @-mention workspace members via the #324 picker. Each pick splices a
    // visible "@{DisplayName}" literal into the editor and records a MentionToken; on post, BuildRuns
    // re-derives the structured runs from the *final* editor text + the recorded tokens, so a token the
    // user later edits/deletes safely degrades to literal text (never a wrong tag). The runs go to the
    // #322 structured facade; a body with no mention falls back to the unchanged plain-text path.

    /// <summary>An @-mention inserted into the composer: the picked member's numeric ClickUp
    /// <c>userId</c> and the display name whose <c>@{name}</c> literal was spliced into the editor (from
    /// the #324 picker's <c>MentionTarget</c>).</summary>
    public readonly record struct MentionToken(long UserId, string DisplayName)
    {
        /// <summary>The literal text the composer inserts into the editor for this mention: <c>@</c> +
        /// the display name (the glue adds a trailing space). On post, <see cref="BuildRuns"/> matches
        /// this literal back out of the final text to re-derive the mention run.</summary>
        public string Token => "@" + (DisplayName ?? string.Empty);
    }

    /// <summary>
    /// Re-derives the structured comment runs from the final editor <paramref name="text"/> and the
    /// <paramref name="tokens"/> the user inserted. A greedy left-to-right scan: at each position, among
    /// the not-yet-consumed tokens whose <c>@{DisplayName}</c> literal matches there, it takes the
    /// <b>longest</b> (so <c>@Ann</c> and <c>@Ann Marie</c> disambiguate), flushes the accumulated
    /// literal text as a <see cref="CommentRun.Text"/>, emits a <see cref="CommentRun.Mention"/>, and
    /// advances — each token consumed at most once. A token whose literal is no longer in the text
    /// (the user deleted or edited it) simply never matches, so the mention degrades to whatever literal
    /// text remains — never a wrong tag. Adjacent literal text is coalesced into one run.
    /// </summary>
    public static IReadOnlyList<CommentRun> BuildRuns(string? text, IReadOnlyList<MentionToken>? tokens)
    {
        var s = text ?? string.Empty;
        var remaining = new List<MentionToken>(tokens ?? []);
        var runs = new List<CommentRun>();
        var literal = new System.Text.StringBuilder();

        var i = 0;
        while (i < s.Length)
        {
            // The longest remaining token literal that matches at i (strict >, so among equal-length /
            // duplicate literals the first recorded one is consumed first, then the next occurrence).
            var bestIndex = -1;
            var bestLength = 0;
            for (var k = 0; k < remaining.Count; k++)
            {
                var lit = remaining[k].Token;
                if (lit.Length > bestLength
                    && i + lit.Length <= s.Length
                    && string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0)
                {
                    bestIndex = k;
                    bestLength = lit.Length;
                }
            }

            if (bestIndex >= 0)
            {
                if (literal.Length > 0)
                {
                    runs.Add(new CommentRun.Text(literal.ToString()));
                    literal.Clear();
                }
                runs.Add(new CommentRun.Mention(remaining[bestIndex].UserId));
                remaining.RemoveAt(bestIndex);
                i += bestLength;
            }
            else
            {
                literal.Append(s[i]);
                i++;
            }
        }

        if (literal.Length > 0)
            runs.Add(new CommentRun.Text(literal.ToString()));
        return runs;
    }

    /// <summary>Trims the leading <see cref="CommentRun.Text"/> run's start and the trailing one's end
    /// (dropping any run that becomes empty) — the structured analogue of the plain path's
    /// <see cref="Normalize"/>. Interior whitespace (e.g. the space between two adjacent mentions) is
    /// preserved.</summary>
    public static IReadOnlyList<CommentRun> TrimRuns(IReadOnlyList<CommentRun> runs)
    {
        var list = new List<CommentRun>(runs ?? []);
        if (list.Count > 0 && list[0] is CommentRun.Text head)
        {
            var trimmed = head.Value.TrimStart();
            if (trimmed.Length == 0)
                list.RemoveAt(0);
            else
                list[0] = new CommentRun.Text(trimmed);
        }
        if (list.Count > 0 && list[^1] is CommentRun.Text tail)
        {
            var trimmed = tail.Value.TrimEnd();
            if (trimmed.Length == 0)
                list.RemoveAt(list.Count - 1);
            else
                list[^1] = new CommentRun.Text(trimmed);
        }
        return list;
    }

    /// <summary>True when <paramref name="runs"/> carries at least one @-mention — the gate for taking
    /// the structured write path (a mention-free body posts via the unchanged plain-text path).</summary>
    public static bool HasMention(IReadOnlyList<CommentRun>? runs)
        => runs is not null && runs.Any(r => r is CommentRun.Mention);

    // ── Reply into a thread (#330) ─────────────────────────────────────────────
    // The list is top-level comments only; a reply lives nested in its parent's Replies (#328/#329), so
    // the reply transforms mirror the flat ones above but operate one level down, on the parent matched
    // by id. Each keeps ReplyCount in step with Replies.Count (the invariant the thread loader #328
    // establishes) so any consumer reading the count stays consistent through the optimistic window.

    /// <summary>The provisional reply appended the instant the user posts, before the server confirms.
    /// Like <see cref="Provisional"/> (blank author, client sentinel <paramref name="id"/>, client-"now"
    /// <paramref name="dateMs"/>) but stamped with the <paramref name="parentCommentId"/> it answers and
    /// the <paramref name="taskId"/> it belongs to — the same linkage the thread loader stamps on a
    /// fetched reply — so it renders nested under its parent (#329).</summary>
    public static CommentItem ProvisionalReply(
        string id, string? text, long? dateMs, string parentCommentId, string? taskId)
        => new(Id: id, Author: "", DateMs: dateMs, Text: Normalize(text), Resolved: false, TaskId: taskId,
            ParentCommentId: parentCommentId);

    /// <summary>Appends <paramref name="provisional"/> to the <see cref="CommentItem.Replies"/> of the
    /// parent matched by <paramref name="parentCommentId"/> (immutably), bumping its
    /// <see cref="CommentItem.ReplyCount"/>. A no-op returning the list unchanged if that parent is no
    /// longer present (a background refresh replaced it mid-post) — self-healing, like <see cref="Reconcile"/>.</summary>
    public static IReadOnlyList<CommentItem> AppendReply(
        IReadOnlyList<CommentItem> comments, string parentCommentId, CommentItem provisional)
        => [.. comments.Select(c => string.Equals(c.Id, parentCommentId, StringComparison.Ordinal)
            ? c with { Replies = [.. c.Replies, provisional], ReplyCount = c.ReplyCount + 1 }
            : c)];

    /// <summary>Replaces the provisional reply (matched by <paramref name="provisionalId"/>) inside its
    /// parent's <see cref="CommentItem.Replies"/> with the server-<paramref name="confirmed"/> one,
    /// re-stamping the parent id and task (the create-reply facade #327 returns both null) so it stays
    /// nested. A no-op if the parent or the provisional is gone.</summary>
    public static IReadOnlyList<CommentItem> ReconcileReply(
        IReadOnlyList<CommentItem> comments, string parentCommentId, string provisionalId, CommentItem confirmed)
        => [.. comments.Select(c => string.Equals(c.Id, parentCommentId, StringComparison.Ordinal)
            ? c with
            {
                Replies = [.. c.Replies.Select(r => string.Equals(r.Id, provisionalId, StringComparison.Ordinal)
                    ? confirmed with { ParentCommentId = parentCommentId, TaskId = confirmed.TaskId ?? r.TaskId }
                    : r)],
            }
            : c)];

    /// <summary>Drops the provisional reply (matched by <paramref name="provisionalId"/>) from its
    /// parent's <see cref="CommentItem.Replies"/> when the post fails, decrementing
    /// <see cref="CommentItem.ReplyCount"/>. A no-op if the parent or the provisional is already gone.</summary>
    public static IReadOnlyList<CommentItem> RevertReply(
        IReadOnlyList<CommentItem> comments, string parentCommentId, string provisionalId)
        => [.. comments.Select(c =>
        {
            if (!string.Equals(c.Id, parentCommentId, StringComparison.Ordinal))
                return c;
            var kept = c.Replies.Where(r => !string.Equals(r.Id, provisionalId, StringComparison.Ordinal)).ToList();
            return kept.Count == c.Replies.Count
                ? c // the provisional wasn't here — leave the parent untouched
                : c with { Replies = kept, ReplyCount = Math.Max(0, c.ReplyCount - 1) };
        })];
}
