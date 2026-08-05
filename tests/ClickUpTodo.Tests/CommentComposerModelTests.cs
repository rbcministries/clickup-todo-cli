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

    // ── @-mention authoring (#325) ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ben Seymour", "@Ben Seymour")]
    [InlineData("", "@")]
    public void MentionToken_TokenIsAtPlusDisplayName(string name, string expected)
        => Assert.Equal(expected, new CommentComposerModel.MentionToken(1, name).Token);

    [Fact]
    public void InsertMention_SplicesTokenWithTrailingSpace_AndReturnsCaretAfterIt()
    {
        var (text, caret) = CommentComposerModel.InsertMention("hi ", 3, "Ada");

        Assert.Equal("hi @Ada ", text);
        Assert.Equal(8, caret); // just past "@Ada "
    }

    [Fact]
    public void InsertMention_InsertsMidText_AtTheCaret()
    {
        var (text, caret) = CommentComposerModel.InsertMention("ab", 1, "Bo");

        Assert.Equal("a@Bo b", text);
        Assert.Equal(5, caret); // after "a@Bo "
    }

    [Theory]
    [InlineData(-4)]  // clamps to 0
    [InlineData(999)] // clamps to end
    public void InsertMention_ClampsOutOfRangeCaret(int caret)
    {
        var (text, _) = CommentComposerModel.InsertMention("xy", caret, "Q");

        Assert.Contains("@Q ", text);
        Assert.True(text.Length == "xy".Length + "@Q ".Length);
    }

    [Fact]
    public void InsertMention_HandlesNullText()
    {
        var (text, caret) = CommentComposerModel.InsertMention(null, 0, "N");

        Assert.Equal("@N ", text);
        Assert.Equal(3, caret);
    }

    [Fact]
    public void BuildRuns_NoTokens_IsASingleTextRun()
    {
        var runs = CommentComposerModel.BuildRuns("just text", []);

        var run = Assert.IsType<CommentRun.Text>(Assert.Single(runs));
        Assert.Equal("just text", run.Value);
    }

    [Fact]
    public void BuildRuns_EmptyText_IsNoRuns()
        => Assert.Empty(CommentComposerModel.BuildRuns("", [new CommentComposerModel.MentionToken(1, "A")]));

    [Fact]
    public void BuildRuns_MentionInTheMiddle_SplitsTextAroundTheTag()
    {
        var tokens = new[] { new CommentComposerModel.MentionToken(42, "Ada") };

        var runs = CommentComposerModel.BuildRuns("hey @Ada thanks", tokens);

        Assert.Collection(runs,
            r => Assert.Equal("hey ", Assert.IsType<CommentRun.Text>(r).Value),
            r => Assert.Equal(42, Assert.IsType<CommentRun.Mention>(r).UserId),
            r => Assert.Equal(" thanks", Assert.IsType<CommentRun.Text>(r).Value));
    }

    [Fact]
    public void BuildRuns_MentionAtStartAndEnd()
    {
        var tokens = new[] { new CommentComposerModel.MentionToken(1, "A") };
        var start = CommentComposerModel.BuildRuns("@A hi", tokens);
        var end = CommentComposerModel.BuildRuns("hi @A", tokens);

        Assert.Collection(start,
            r => Assert.Equal(1, Assert.IsType<CommentRun.Mention>(r).UserId),
            r => Assert.Equal(" hi", Assert.IsType<CommentRun.Text>(r).Value));
        Assert.Collection(end,
            r => Assert.Equal("hi ", Assert.IsType<CommentRun.Text>(r).Value),
            r => Assert.Equal(1, Assert.IsType<CommentRun.Mention>(r).UserId));
    }

    [Fact]
    public void BuildRuns_TwoDistinctMentions_AreBothTagged()
    {
        var tokens = new[]
        {
            new CommentComposerModel.MentionToken(1, "Ada"),
            new CommentComposerModel.MentionToken(2, "Bo"),
        };

        var runs = CommentComposerModel.BuildRuns("@Ada @Bo", tokens);

        Assert.Collection(runs,
            r => Assert.Equal(1, Assert.IsType<CommentRun.Mention>(r).UserId),
            r => Assert.Equal(" ", Assert.IsType<CommentRun.Text>(r).Value),
            r => Assert.Equal(2, Assert.IsType<CommentRun.Mention>(r).UserId));
    }

    [Fact]
    public void BuildRuns_SameMemberTwice_ConsumesBothTokens()
    {
        var tokens = new[]
        {
            new CommentComposerModel.MentionToken(7, "Ada"),
            new CommentComposerModel.MentionToken(7, "Ada"),
        };

        var runs = CommentComposerModel.BuildRuns("@Ada and @Ada", tokens);

        Assert.Collection(runs,
            r => Assert.Equal(7, Assert.IsType<CommentRun.Mention>(r).UserId),
            r => Assert.Equal(" and ", Assert.IsType<CommentRun.Text>(r).Value),
            r => Assert.Equal(7, Assert.IsType<CommentRun.Mention>(r).UserId));
    }

    [Fact]
    public void BuildRuns_PrefersLongestMatch_WhenOneNameIsAPrefixOfAnother()
    {
        // Both members mentioned; "@Ann" must not greedily swallow the "@Ann Marie" occurrence.
        var tokens = new[]
        {
            new CommentComposerModel.MentionToken(1, "Ann"),
            new CommentComposerModel.MentionToken(2, "Ann Marie"),
        };

        var runs = CommentComposerModel.BuildRuns("@Ann Marie", tokens);

        var mention = Assert.IsType<CommentRun.Mention>(Assert.Single(runs));
        Assert.Equal(2, mention.UserId); // the longer "@Ann Marie" wins
    }

    [Fact]
    public void BuildRuns_TokenNoLongerInText_DegradesToLiteralText()
    {
        // The user deleted the inserted "@Ada" token: it must not tag anyone — the body is plain text.
        var tokens = new[] { new CommentComposerModel.MentionToken(1, "Ada") };

        var runs = CommentComposerModel.BuildRuns("no mention here", tokens);

        var run = Assert.IsType<CommentRun.Text>(Assert.Single(runs));
        Assert.Equal("no mention here", run.Value);
        Assert.False(CommentComposerModel.HasMention(runs));
    }

    [Fact]
    public void BuildRuns_ExtraLiteralBeyondTrackedCount_StaysText()
    {
        // Only one "@Ada" was inserted; a second literal "@Ada" the user typed by hand isn't a tag.
        var tokens = new[] { new CommentComposerModel.MentionToken(1, "Ada") };

        var runs = CommentComposerModel.BuildRuns("@Ada @Ada", tokens);

        Assert.Collection(runs,
            r => Assert.Equal(1, Assert.IsType<CommentRun.Mention>(r).UserId),
            r => Assert.Equal(" @Ada", Assert.IsType<CommentRun.Text>(r).Value));
    }

    [Fact]
    public void TrimRuns_TrimsOuterTextEdges_KeepsInteriorSpacing()
    {
        var runs = new List<CommentRun>
        {
            new CommentRun.Text("  hey "),
            new CommentRun.Mention(1),
            new CommentRun.Text(" there  "),
        };

        var trimmed = CommentComposerModel.TrimRuns(runs);

        Assert.Collection(trimmed,
            r => Assert.Equal("hey ", Assert.IsType<CommentRun.Text>(r).Value),
            r => Assert.Equal(1, Assert.IsType<CommentRun.Mention>(r).UserId),
            r => Assert.Equal(" there", Assert.IsType<CommentRun.Text>(r).Value));
    }

    [Fact]
    public void TrimRuns_DropsEdgeRunsThatBecomeEmpty()
    {
        var runs = new List<CommentRun>
        {
            new CommentRun.Text("   "),
            new CommentRun.Mention(9),
            new CommentRun.Text("  "),
        };

        var trimmed = CommentComposerModel.TrimRuns(runs);

        var only = Assert.IsType<CommentRun.Mention>(Assert.Single(trimmed));
        Assert.Equal(9, only.UserId);
    }

    [Fact]
    public void HasMention_TrueOnlyWhenATagIsPresent()
    {
        Assert.True(CommentComposerModel.HasMention([new CommentRun.Text("a"), new CommentRun.Mention(1)]));
        Assert.False(CommentComposerModel.HasMention([new CommentRun.Text("a")]));
        Assert.False(CommentComposerModel.HasMention(null));
    }

    [Theory]
    [InlineData("hello", 0, 0, 0)]
    [InlineData("hello", 0, 3, 3)]
    [InlineData("hello", 0, 99, 5)]   // col past line end clamps to line end
    [InlineData("ab\ncd", 1, 1, 4)]   // second line, col 1 → index of 'd'
    [InlineData("ab\ncd", 1, 0, 3)]   // start of second line
    [InlineData("ab\ncd", 9, 0, 5)]   // row past end clamps to text end
    public void CaretIndex_MapsRowColToAbsoluteIndex(string text, int row, int col, int expected)
        => Assert.Equal(expected, CommentComposerModel.CaretIndex(text, row, col));

    [Theory]
    [InlineData("hello", 3, 0, 3)]
    [InlineData("ab\ncd", 4, 1, 1)]
    [InlineData("ab\ncd", 3, 1, 0)]
    [InlineData("ab\ncd", 0, 0, 0)]
    public void CaretRowCol_MapsIndexToRowCol(string text, int index, int expectedRow, int expectedCol)
    {
        var (row, col) = CommentComposerModel.CaretRowCol(text, index);
        Assert.Equal(expectedRow, row);
        Assert.Equal(expectedCol, col);
    }

    [Fact]
    public void CaretIndex_And_CaretRowCol_RoundTrip()
    {
        const string text = "first line\nsecond\n\nfourth";
        for (var i = 0; i <= text.Length; i++)
        {
            var (row, col) = CommentComposerModel.CaretRowCol(text, i);
            Assert.Equal(i, CommentComposerModel.CaretIndex(text, row, col));
        }
    }
}
