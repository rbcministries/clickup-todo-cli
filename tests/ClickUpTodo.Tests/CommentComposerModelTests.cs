using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure comment-composer model (issue #216): key→action routing, the empty-body
/// gate + normalization, and the optimistic append / reconcile / revert list transforms. The
/// Terminal.Gui glue in <c>TaskDetailScreen</c> is verified by build + reasoning + <c>tui-validate</c>
/// per the repo's TUI rule; this locks the decisions it delegates.
/// </summary>
public sealed class CommentComposerModelTests
{
    [Theory]
    [InlineData(CommentComposerModel.ComposerKey.Submit, CommentComposerModel.ComposerAction.Post)]
    [InlineData(CommentComposerModel.ComposerKey.Cancel, CommentComposerModel.ComposerAction.Cancel)]
    [InlineData(CommentComposerModel.ComposerKey.Other, CommentComposerModel.ComposerAction.PassThrough)]
    public void Route_MapsEachKeyToItsAction(
        CommentComposerModel.ComposerKey key, CommentComposerModel.ComposerAction expected)
        => Assert.Equal(expected, CommentComposerModel.Route(key));

    [Theory]
    [InlineData("hello", true)]
    [InlineData("  trimmed later  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\n\t  \n", false)]
    [InlineData(null, false)]
    public void IsPostable_GatesEmptyAndWhitespaceBodies(string? text, bool expected)
        => Assert.Equal(expected, CommentComposerModel.IsPostable(text));

    [Theory]
    [InlineData("  hi  ", "hi")]
    [InlineData("\n\nline\n\n", "line")]
    [InlineData(null, "")]
    [InlineData("keep\ninner\nnewlines", "keep\ninner\nnewlines")]
    public void Normalize_TrimsSurroundingWhitespaceOnly(string? text, string expected)
        => Assert.Equal(expected, CommentComposerModel.Normalize(text));

    [Fact]
    public void Provisional_CarriesTrimmedTextEmptyAuthorAndStampedDate()
    {
        var p = CommentComposerModel.Provisional("__pending__1", "  a comment  ", 1_700_000_000_000);

        Assert.Equal("__pending__1", p.Id);
        Assert.Equal("", p.Author);
        Assert.Equal(1_700_000_000_000, p.DateMs);
        Assert.Equal("a comment", p.Text); // normalized
        Assert.False(p.Resolved);
    }

    [Fact]
    public void Append_AddsProvisionalAtTheEnd_WithoutMutatingInput()
    {
        var existing = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false),
            new("c2", "Alan", 200, "second", false),
        };
        var provisional = CommentComposerModel.Provisional("__pending__1", "third", 300);

        var result = CommentComposerModel.Append(existing, provisional);

        Assert.Equal(3, result.Count);
        Assert.Equal("__pending__1", result[^1].Id);
        Assert.Equal(2, existing.Count); // input untouched
    }

    [Fact]
    public void Reconcile_ReplacesProvisionalWithConfirmed_InPlace()
    {
        var provisional = CommentComposerModel.Provisional("__pending__1", "hi", 300);
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false),
            provisional,
        };
        var confirmed = new CommentItem("real-99", "", 305, "hi", false, "task1");

        var result = CommentComposerModel.Reconcile(comments, "__pending__1", confirmed);

        Assert.Equal(2, result.Count);
        Assert.Equal("c1", result[0].Id); // untouched
        Assert.Equal("real-99", result[1].Id); // provisional replaced by server-confirmed
        Assert.Equal(2, comments.Count); // input untouched
    }

    [Fact]
    public void Reconcile_IsNoOp_WhenProvisionalNoLongerPresent()
    {
        // A background refresh replaced the list mid-post, so the provisional is gone: reconcile must
        // not resurrect or duplicate it — the next refresh re-pulls the real posted comment.
        var comments = new List<CommentItem> { new("c1", "Ada", 100, "first", false) };
        var confirmed = new CommentItem("real-99", "", 305, "hi", false);

        var result = CommentComposerModel.Reconcile(comments, "__pending__1", confirmed);

        Assert.Single(result);
        Assert.Equal("c1", result[0].Id);
        Assert.DoesNotContain(result, c => c.Id == "real-99");
    }

    [Fact]
    public void Revert_DropsOnlyTheProvisional()
    {
        var provisional = CommentComposerModel.Provisional("__pending__1", "hi", 300);
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false),
            provisional,
            new("c2", "Alan", 200, "second", false),
        };

        var result = CommentComposerModel.Revert(comments, "__pending__1");

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, c => c.Id == "__pending__1");
        Assert.Equal(3, comments.Count); // input untouched
    }

    [Fact]
    public void Revert_IsNoOp_WhenProvisionalAbsent()
    {
        var comments = new List<CommentItem> { new("c1", "Ada", 100, "first", false) };

        var result = CommentComposerModel.Revert(comments, "__pending__1");

        Assert.Single(result);
        Assert.Equal("c1", result[0].Id);
    }
}
