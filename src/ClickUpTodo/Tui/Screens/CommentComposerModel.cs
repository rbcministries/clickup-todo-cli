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
}
