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
        /// <summary>The literal text spliced into the editor for this mention: <c>@</c> + the display
        /// name (no trailing space — <see cref="InsertMention"/> adds the separating space).</summary>
        public string Token => "@" + (DisplayName ?? string.Empty);
    }

    /// <summary>Splices <paramref name="displayName"/>'s <c>@{name} </c> token (with a trailing space so
    /// the user keeps typing after it) into <paramref name="text"/> at the clamped
    /// <paramref name="caret"/>. Returns the new text and the caret position just after the inserted
    /// token. Pure so the Terminal.Gui glue keeps no string arithmetic of its own.</summary>
    public static (string Text, int Caret) InsertMention(string? text, int caret, string displayName)
    {
        var s = text ?? string.Empty;
        var at = Math.Clamp(caret, 0, s.Length);
        var token = "@" + (displayName ?? string.Empty) + " ";
        return (s.Insert(at, token), at + token.Length);
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

    /// <summary>The absolute index into <paramref name="text"/> of the caret at logical
    /// <paramref name="row"/>/<paramref name="col"/> (a Terminal.Gui <c>TextView.CursorPosition</c>,
    /// model coordinates over <c>\n</c>-delimited lines). A row past the end clamps to the text end; a
    /// column past its line's end clamps to that line's end. Pure, so the insertion glue owns no
    /// arithmetic.</summary>
    public static int CaretIndex(string? text, int row, int col)
    {
        var s = text ?? string.Empty;
        if (row < 0)
            row = 0;
        if (col < 0)
            col = 0;

        var idx = 0;
        for (var line = 0; line < row; line++)
        {
            var nl = s.IndexOf('\n', idx);
            if (nl < 0)
                return s.Length;
            idx = nl + 1;
        }
        var lineEnd = s.IndexOf('\n', idx);
        if (lineEnd < 0)
            lineEnd = s.Length;
        return Math.Min(idx + col, lineEnd);
    }

    /// <summary>The logical <c>(row, col)</c> caret (model coordinates over <c>\n</c>-delimited lines)
    /// for an absolute <paramref name="index"/> into <paramref name="text"/> — the inverse of
    /// <see cref="CaretIndex"/>, used to place the editor caret after an inserted mention token.</summary>
    public static (int Row, int Col) CaretRowCol(string? text, int index)
    {
        var s = text ?? string.Empty;
        var i = Math.Clamp(index, 0, s.Length);
        int row = 0, lineStart = 0;
        for (var p = 0; p < i; p++)
        {
            if (s[p] == '\n')
            {
                row++;
                lineStart = p + 1;
            }
        }
        return (row, i - lineStart);
    }
}
