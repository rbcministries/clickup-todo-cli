using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// The pure rename-submit decision behind the main-list task-rename overlay (contextual chords H, #545):
/// blank stays open, an unchanged title is a no-op dismiss, a genuine edit renames — with the value
/// trimmed. The Terminal.Gui <c>RenameTaskScreen</c> is a thin shell over this, so this is where the
/// validation seam is proven (the screen itself can't run in CI).
/// </summary>
public sealed class RenameTaskModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Classify_BlankOrWhitespace_IsBlank(string input)
    {
        var result = RenameTaskModel.Classify(input, "Original");

        Assert.Equal(RenameTaskModel.Outcome.Blank, result.Outcome);
        Assert.Equal("", result.Name);
    }

    [Fact]
    public void Classify_Null_IsBlank()
        => Assert.Equal(RenameTaskModel.Outcome.Blank, RenameTaskModel.Classify(null, "Original").Outcome);

    [Fact]
    public void Classify_SameAsOriginal_IsUnchanged()
    {
        var result = RenameTaskModel.Classify("Ship the release", "Ship the release");

        Assert.Equal(RenameTaskModel.Outcome.Unchanged, result.Outcome);
    }

    // Only surrounding whitespace differs → the trimmed value equals the original, so it's a no-op, not a
    // pointless round-trip that rewrites the same title.
    [Fact]
    public void Classify_OnlyWhitespaceDiffers_IsUnchanged()
        => Assert.Equal(
            RenameTaskModel.Outcome.Unchanged,
            RenameTaskModel.Classify("  Ship the release  ", "Ship the release").Outcome);

    // The original is trimmed on the comparison too: pressing Enter on a title that itself carries stray
    // surrounding whitespace (unedited, or edited only in the padding) is a no-op, not a normalize-only write.
    [Theory]
    [InlineData("  Ship the release  ")]
    [InlineData("Ship the release")]
    public void Classify_PaddedOriginal_IsUnchanged_WhenTrimmedTextMatches(string input)
        => Assert.Equal(
            RenameTaskModel.Outcome.Unchanged,
            RenameTaskModel.Classify(input, "  Ship the release  ").Outcome);

    [Fact]
    public void Classify_DifferentText_IsRename_AndTrimmed()
    {
        var result = RenameTaskModel.Classify("  Draft the notes  ", "Ship the release");

        Assert.Equal(RenameTaskModel.Outcome.Rename, result.Outcome);
        Assert.Equal("Draft the notes", result.Name);
    }

    // Rename is case-sensitive: fixing a title's capitalization is a real edit, not a no-op.
    [Fact]
    public void Classify_CaseOnlyChange_IsRename()
    {
        var result = RenameTaskModel.Classify("ship the release", "Ship the release");

        Assert.Equal(RenameTaskModel.Outcome.Rename, result.Outcome);
        Assert.Equal("ship the release", result.Name);
    }
}
