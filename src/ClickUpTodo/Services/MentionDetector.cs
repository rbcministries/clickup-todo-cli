using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Detects whether a feed comment (#109) mentions the current user (#113). Detection operates on the
/// flattened <see cref="CommentItem.Text"/> (ClickUp's <c>comment_text</c>) — the substrate the whole
/// feed is built on — matching an <b>@-prefixed handle</b> against the authenticated user's identity
/// (<see cref="ClickUpUser.DisplayName"/> from <c>GetMeAsync</c>). Requiring the leading <c>@</c> avoids
/// false positives from a name appearing in prose ("Ben looks good" is not a mention).
/// </summary>
/// <remarks>
/// Structured user-id matching (parsing ClickUp's <c>comment</c> blocks for a mentioned numeric id)
/// needs the curated OpenAPI spec extended + a Kiota regen and is tracked as a follow-up; the flat
/// text path here is the reliable substrate available today and covers the acceptance criteria.
/// Pure and allocation-light so it can be unit-tested offline and run over a whole feed cheaply.
/// </remarks>
public static class MentionDetector
{
    /// <summary>
    /// Tests whether <paramref name="commentText"/> mentions the user described by
    /// <paramref name="spec"/>: true when the text contains <c>@{handle}</c> (case-insensitive) for any
    /// of the spec's handles, with a non-word boundary after the handle so <c>@Benny</c> does not match
    /// the handle <c>ben</c>. A blank text or a spec with no handles never matches.
    /// </summary>
    public static bool Mentions(string? commentText, MentionSpec spec)
    {
        if (string.IsNullOrEmpty(commentText) || spec.Handles.Count == 0)
            return false;

        foreach (var handle in spec.Handles)
        {
            if (ContainsHandleMention(commentText, handle))
                return true;
        }

        return false;
    }

    /// <summary>Convenience overload over <see cref="CommentItem.Text"/>.</summary>
    public static bool Mentions(CommentItem comment, MentionSpec spec) => Mentions(comment.Text, spec);

    // Finds "@{handle}" case-insensitively as a standalone @-mention, requiring a non-word boundary on
    // BOTH sides: the char before the "@" must be non-word (or start-of-string) so an email address like
    // "alice@ben.dev" doesn't match the local-part handle "ben", and the char after the handle must be
    // non-word (or end-of-string) so "@Benny" doesn't match handle "ben". The handle itself may contain
    // spaces (a display name like "Ben Seymour" renders as "@Ben Seymour") and, for an email identity,
    // an "@" (the full "@ben@odb.org" handle is still preceded by a boundary, so it matches).
    private static bool ContainsHandleMention(string text, string handle)
    {
        var needle = "@" + handle;
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;

            var beforeIsBoundary = at == 0 || !IsWordChar(text[at - 1]);
            var afterIndex = at + needle.Length;
            var afterIsBoundary = afterIndex >= text.Length || !IsWordChar(text[afterIndex]);
            if (beforeIsBoundary && afterIsBoundary)
                return true;

            from = at + 1; // overlapping matches are fine; advance past this "@" and keep looking
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

/// <summary>
/// The set of @-handles that identify the current user in a comment's text. Built from the
/// authenticated <see cref="ClickUpUser"/>; extensible later to workspace-member aliases. Handles are
/// trimmed, lowercased, and de-duplicated; an empty set never matches.
/// </summary>
public sealed record MentionSpec(IReadOnlyList<string> Handles)
{
    /// <summary>A spec that matches nothing (used when the user has no usable handle).</summary>
    public static MentionSpec None { get; } = new(Array.Empty<string>());

    /// <summary>
    /// Builds a spec from the signed-in user. The display name (the username → email → id the client
    /// resolves) is the primary handle; when it is an email, its local part is added too so <c>@ben</c>
    /// matches a <c>ben@odb.org</c> identity. Blank handles are dropped.
    /// </summary>
    public static MentionSpec ForUser(ClickUpUser user)
    {
        var handles = new List<string>();
        AddHandle(handles, user.DisplayName);

        var display = user.DisplayName?.Trim();
        if (!string.IsNullOrEmpty(display))
        {
            var at = display.IndexOf('@');
            if (at > 0)
                AddHandle(handles, display[..at]); // email local part
        }

        return handles.Count == 0 ? None : new MentionSpec(handles);
    }

    private static void AddHandle(List<string> handles, string? raw)
    {
        var handle = raw?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(handle) && !handles.Contains(handle))
            handles.Add(handle);
    }
}
