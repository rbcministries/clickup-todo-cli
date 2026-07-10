using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pins the pure feed-row layout (#114): the gutter/mention chip, the author/date/preview shape, the
/// mention badge span, and the decoupled type-ahead search key. Terminal.Gui-free (the badge attribute
/// is built in the view layer), matching the repo's <c>TaskRowFormatter</c> testing pattern.
/// </summary>
public sealed class FeedRowFormatterTests
{
    private static CommentItem Comment(
        string author = "Ben Seymour", long? dateMs = null, string text = "hello", bool mentionsMe = false)
        => new("c1", author, dateMs, text, Resolved: false, TaskId: "t1", MentionsMe: mentionsMe);

    [Fact]
    public void Mention_LeadsWithTheChip_AndReportsItsSpan()
    {
        var row = FeedRowFormatter.Format(Comment(mentionsMe: true));

        Assert.StartsWith(FeedRowFormatter.MentionChip, row.Text);
        Assert.Equal(0, row.MentionStart);
        Assert.Equal(FeedRowFormatter.MentionChip.Length, row.MentionLength);
        // The span covers exactly the chip.
        Assert.Equal(
            FeedRowFormatter.MentionChip,
            row.Text.Substring(row.MentionStart, row.MentionLength));
    }

    [Fact]
    public void NonMention_LeadsWithTheBlankGutter_AndReportsNoSpan()
    {
        var row = FeedRowFormatter.Format(Comment(mentionsMe: false));

        Assert.StartsWith(FeedRowFormatter.BlankGutter, row.Text);
        Assert.Equal(-1, row.MentionStart);
        Assert.Equal(0, row.MentionLength);
    }

    [Fact]
    public void GutterAndChip_AreTheSameWidth_SoAuthorsLineUp()
        => Assert.Equal(FeedRowFormatter.BlankGutter.Length, FeedRowFormatter.MentionChip.Length);

    [Fact]
    public void Author_IsShown_AfterTheGutter()
    {
        var row = FeedRowFormatter.Format(Comment(author: "Ben Seymour"));

        Assert.Contains("Ben Seymour", row.Text);
        Assert.Equal("Ben Seymour", row.SearchKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankAuthor_FallsBackToUnknown(string? author)
    {
        var row = FeedRowFormatter.Format(Comment(author: author!));

        Assert.Contains(FeedRowFormatter.UnknownAuthor, row.Text);
        Assert.Equal(FeedRowFormatter.UnknownAuthor, row.SearchKey);
    }

    [Fact]
    public void Author_IsTrimmed()
    {
        var row = FeedRowFormatter.Format(Comment(author: "  Alice  "));

        Assert.Equal("Alice", row.SearchKey);
    }

    [Fact]
    public void Date_WhenPresent_IsRendered()
    {
        const long ms = 1_751_476_320_000; // a fixed instant
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MMM d, HH:mm");

        var row = FeedRowFormatter.Format(Comment(dateMs: ms));

        Assert.Contains(expected, row.Text);
    }

    [Fact]
    public void Date_WhenAbsent_IsOmittedWithItsSeparator()
    {
        var withDate = FeedRowFormatter.Format(Comment(dateMs: 1_751_476_320_000, text: "body"));
        var without = FeedRowFormatter.Format(Comment(dateMs: null, text: "body"));

        // Author · date · preview  →  author · preview: one fewer separator when there's no date.
        Assert.Equal(2, CountOccurrences(withDate.Text, FeedRowFormatter.Separator));
        Assert.Equal(1, CountOccurrences(without.Text, FeedRowFormatter.Separator));
    }

    [Fact]
    public void Preview_FlattensNewlinesAndCollapsesWhitespace()
    {
        var row = FeedRowFormatter.Format(Comment(text: "line one\n\n  line   two\tthree"));

        Assert.Contains("line one line two three", row.Text);
        Assert.DoesNotContain("\n", row.Text);
    }

    [Fact]
    public void Preview_TruncatesLongTextWithEllipsis()
    {
        var longText = new string('x', FeedRowFormatter.MaxPreviewLength + 50);

        var row = FeedRowFormatter.Format(Comment(text: longText));

        Assert.Contains(new string('x', FeedRowFormatter.MaxPreviewLength) + "…", row.Text);
        Assert.DoesNotContain(new string('x', FeedRowFormatter.MaxPreviewLength + 1), row.Text);
    }

    [Fact]
    public void Preview_ShortTextIsNotTruncated()
    {
        var text = new string('y', FeedRowFormatter.MaxPreviewLength);

        var row = FeedRowFormatter.Format(Comment(text: text));

        Assert.Contains(text, row.Text);
        Assert.DoesNotContain("…", row.Text);
    }

    [Fact]
    public void Preview_TruncationDoesNotSplitASurrogatePair()
    {
        // (Max-1) ASCII chars then an astral emoji (a surrogate pair): a naïve cut at Max would land
        // between the two surrogates, leaving a lone high surrogate before the ellipsis.
        const string emoji = "\U0001F6E0"; // 🛠 — two UTF-16 chars
        var text = new string('a', FeedRowFormatter.MaxPreviewLength - 1) + emoji + "tail";

        var row = FeedRowFormatter.Format(Comment(text: text));

        // No unpaired surrogate anywhere in the rendered row (a split pair would leave one).
        for (var i = 0; i < row.Text.Length; i++)
        {
            if (char.IsHighSurrogate(row.Text[i]))
                Assert.True(i + 1 < row.Text.Length && char.IsLowSurrogate(row.Text[i + 1]));
            if (char.IsLowSurrogate(row.Text[i]))
                Assert.True(i > 0 && char.IsHighSurrogate(row.Text[i - 1]));
        }
        Assert.EndsWith("…", row.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData(null)]
    public void BlankText_FallsBackToEmptyCommentPlaceholder(string? text)
    {
        var row = FeedRowFormatter.Format(Comment(text: text!));

        Assert.Contains(FeedRowFormatter.EmptyComment, row.Text);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var from = 0;
        while ((from = haystack.IndexOf(needle, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from += needle.Length;
        }
        return count;
    }
}
