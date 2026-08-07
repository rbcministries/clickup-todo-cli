namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure logic for the Task Detail description editor (issue #217, H of the Writing New Content epic
/// #208), factored out of the Terminal.Gui glue so it's unit-testable without a terminal — the same
/// pure-glue split as <see cref="CommentComposerModel"/> / <c>DispatchPaneModel</c>. It decides which
/// action a key maps to while the editor is open, seeds the editor from the current description,
/// normalizes the text as it is written, and reports whether saving would actually change anything
/// (which drives both the Esc unsaved-changes confirm and the no-op-Save short-circuit).
/// </summary>
public static class DescriptionEditorModel
{
    /// <summary>The keys the editor intercepts. The glue classifies a Terminal.Gui <c>Key</c> into one
    /// of these; anything else is <see cref="EditorKey.Other"/> and passes through to the editor (so
    /// multi-line typing, incl. <c>Enter</c>-inserts-a-newline, keeps working).</summary>
    public enum EditorKey
    {
        Save,
        Cancel,
        Other,
    }

    /// <summary>What the glue should do for a classified key.</summary>
    public enum EditorAction
    {
        Save,
        Cancel,
        PassThrough,
    }

    /// <summary>Maps a key to its action: save persists, escape cancels, everything else falls through
    /// to the focused editor so multi-line typing is undisturbed.</summary>
    public static EditorAction Route(EditorKey key) => key switch
    {
        EditorKey.Save => EditorAction.Save,
        EditorKey.Cancel => EditorAction.Cancel,
        _ => EditorAction.PassThrough,
    };

    /// <summary>The editor's initial text: the current description, or empty when the task has none.</summary>
    public static string Seed(string? currentDescription) => currentDescription ?? string.Empty;

    /// <summary>
    /// The description as it is written — surrounding whitespace/newlines trimmed (ClickUp stores the
    /// value verbatim otherwise). An empty string is a <b>valid</b> value: it clears the description
    /// (the #211 facade sends <c>""</c> and ClickUp clears the field).
    /// </summary>
    public static string Normalize(string? text) => (text ?? string.Empty).Trim();

    /// <summary>
    /// True when saving would change the stored description — the normalized editor text differs from
    /// the normalized original. Drives the Esc unsaved-changes confirm and lets Save skip a no-op write
    /// (so re-opening the editor and pressing Save/Esc without edits neither writes nor prompts). A null
    /// original and an empty/whitespace editor are equal (both clear to nothing).
    /// </summary>
    public static bool IsDirty(string? original, string? current)
        => !string.Equals(Normalize(original), Normalize(current), StringComparison.Ordinal);

    /// <summary>
    /// The literal text spliced into the editor when a member is @-mentioned (#326): <c>@</c> + the
    /// display name + a trailing space (the same <c>@{name} </c> literal the comment composer inserts, so
    /// both authoring surfaces read identically). Unlike the comment composer (#325), a description
    /// mention is <b>only</b> this literal text: ClickUp descriptions carry no structured mention payload
    /// (the #321 spike, Finding 2), so the saved <c>@name</c> is a plain textual reference — never a
    /// live/notifying mention — and travels the unchanged plain-string write path (<see cref="Normalize"/>).
    /// </summary>
    public static string MentionInsertion(string? displayName) => "@" + (displayName ?? string.Empty) + " ";
}
