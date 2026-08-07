using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure description-editor model (issue #217): key→action routing, the seed from the
/// current description, whitespace normalization (with empty-clears-the-field semantics), and the
/// dirty-check that gates the Esc unsaved-changes confirm and the no-op-Save short-circuit. The
/// Terminal.Gui glue in <c>TaskDetailScreen</c> is verified by build + reasoning + <c>tui-validate</c>
/// per the repo's TUI rule; this locks the decisions it delegates.
/// </summary>
public sealed class DescriptionEditorModelTests
{
    [Theory]
    [InlineData(DescriptionEditorModel.EditorKey.Save, DescriptionEditorModel.EditorAction.Save)]
    [InlineData(DescriptionEditorModel.EditorKey.Cancel, DescriptionEditorModel.EditorAction.Cancel)]
    [InlineData(DescriptionEditorModel.EditorKey.Other, DescriptionEditorModel.EditorAction.PassThrough)]
    public void Route_MapsEachKeyToItsAction(
        DescriptionEditorModel.EditorKey key, DescriptionEditorModel.EditorAction expected)
        => Assert.Equal(expected, DescriptionEditorModel.Route(key));

    [Theory]
    [InlineData("current description", "current description")]
    [InlineData("", "")]
    [InlineData(null, "")] // a task with no description seeds an empty editor
    public void Seed_UsesCurrentDescriptionOrEmpty(string? current, string expected)
        => Assert.Equal(expected, DescriptionEditorModel.Seed(current));

    [Theory]
    [InlineData("  hi  ", "hi")]
    [InlineData("\n\nline\n\n", "line")]
    [InlineData(null, "")]
    [InlineData("", "")] // empty stays empty — a valid clear
    [InlineData("   ", "")] // all-whitespace clears to empty
    [InlineData("keep\ninner\nnewlines", "keep\ninner\nnewlines")]
    public void Normalize_TrimsSurroundingWhitespaceOnly(string? text, string expected)
        => Assert.Equal(expected, DescriptionEditorModel.Normalize(text));

    [Theory]
    // Unchanged text (incl. trailing-whitespace-only edits) is not dirty.
    [InlineData("hello", "hello", false)]
    [InlineData("hello", "hello   ", false)]
    [InlineData("hello", "  hello", false)]
    // A real content change is dirty.
    [InlineData("hello", "hello world", true)]
    [InlineData("hello", "", true)] // clearing an existing description is a change
    // Null original (no description) vs empty/whitespace editor: both clear to nothing → not dirty.
    [InlineData(null, "", false)]
    [InlineData(null, "   ", false)]
    [InlineData(null, "typed", true)] // adding a description where there was none is a change
    public void IsDirty_ComparesNormalizedText(string? original, string current, bool expected)
        => Assert.Equal(expected, DescriptionEditorModel.IsDirty(original, current));

    // ── @-mention insertion (#326, sub-issue L of #313) ───────────────────────────────────────────
    // A description mention is *plain literal text* — the #321 spike (Finding 2) established ClickUp
    // descriptions carry no structured mention payload, so the picker just splices "@Name " and the
    // unchanged plain-string write path sends it verbatim.

    [Theory]
    [InlineData("Ada Lovelace", "@Ada Lovelace ")]
    [InlineData("Ada", "@Ada ")]
    [InlineData("", "@ ")]           // a blank display name still inserts a trailing space so the caret advances
    [InlineData(null, "@ ")]
    public void MentionInsertion_IsAtNamePlusTrailingSpace(string? displayName, string expected)
        => Assert.Equal(expected, DescriptionEditorModel.MentionInsertion(displayName));

    // The description editor and the comment composer must splice an *identical* "@Name " literal so the
    // two authoring surfaces read the same — locked here rather than by a code dependency between the two
    // (unrelated) pure models. If the composer's token shape ever changes, this catches the drift.
    [Theory]
    [InlineData("Ada Lovelace")]
    [InlineData("Grace")]
    public void MentionInsertion_MatchesTheComposerTokenLiteral(string displayName)
        => Assert.Equal(
            new CommentComposerModel.MentionToken(101, displayName).Token + " ",
            DescriptionEditorModel.MentionInsertion(displayName));

    // An @name reference is ordinary text to the write path: it isn't trimmed away, and it makes a
    // previously-empty (or different) description dirty exactly like any other typed content — proving
    // the plain-string save path (Normalize/IsDirty) is untouched by the mention feature.
    [Fact]
    public void MentionText_IsOrdinaryContentForTheWritePath()
    {
        var body = "see " + DescriptionEditorModel.MentionInsertion("Ada Lovelace") + "for details";
        Assert.Equal("see @Ada Lovelace for details", DescriptionEditorModel.Normalize(body));
        Assert.True(DescriptionEditorModel.IsDirty(original: null, current: body));
        Assert.True(DescriptionEditorModel.IsDirty(original: "see  for details", current: body));
    }
}
