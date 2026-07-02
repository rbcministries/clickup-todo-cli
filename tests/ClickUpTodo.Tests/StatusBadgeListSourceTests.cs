using System.Text;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;
using Terminal.Gui.Text;

namespace ClickUpTodo.Tests;

/// <summary>
/// Column-math tests for <see cref="StatusBadgeListSource"/>. The draw path itself isn't unit-testable
/// (Terminal.Gui), but the pure <see cref="StatusBadgeListSource.LayOutGraphemes"/> the overlays use to
/// position colour is — and its correctness property is precisely #63: overlay columns must match where
/// the stock <see cref="Terminal.Gui.Views.ListWrapper{T}"/> draws the text, which advances by
/// grapheme-aware <see cref="StringExtensions.GetColumns(string, bool)"/>.
/// </summary>
public sealed class StatusBadgeListSourceTests
{
    // The pre-#63 (buggy) formula: walk runes, flooring each at one column. Diverges from the base
    // renderer for names with wide/combining/emoji clusters — which is the bug we're pinning as fixed.
    private static int OldPerRuneColumns(string text, int charEnd)
    {
        var col = 0;
        for (var i = 0; i < charEnd;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed);
            col += Math.Max(1, rune.GetColumns());
            i += consumed;
        }
        return col;
    }

    // The start display column of the grapheme at UTF-16 index charIndex, per the new layout.
    private static int LaidOutColumnAt(string text, int charIndex)
    {
        foreach (var g in StatusBadgeListSource.LayOutGraphemes(text))
            if (g.CharIndex == charIndex)
                return g.Column;
        // charIndex is at (or past) the end of the text.
        return text.GetColumns();
    }

    [Theory]
    [InlineData("Simple ASCII task")]                 // one rune == one grapheme == 1 column
    [InlineData("会议 review notes")]                   // wide CJK runes (2 columns each)
    [InlineData("café order")]                    // 'e' + combining acute (one grapheme, 1 column)
    [InlineData("ship it \U0001F600 now")]             // a lone emoji (single wide rune)
    [InlineData("family \U0001F468‍\U0001F469‍\U0001F467 sync")] // ZWJ family (one grapheme)
    public void LayOutGraphemes_ColumnMatchesBaseRendererWidth(string text)
    {
        // The invariant the fix guarantees: every grapheme starts at exactly the display column the
        // stock renderer places it, i.e. text[..CharIndex].GetColumns().
        foreach (var g in StatusBadgeListSource.LayOutGraphemes(text))
        {
            Assert.Equal(text[..g.CharIndex].GetColumns(), g.Column);
            Assert.Equal(g.Text.GetColumns(), g.Width);
        }
    }

    [Fact]
    public void LayOutGraphemes_CoversWholeString_InCharAndColumnOrder()
    {
        const string text = "á\U0001F600b"; // á(2 runes,1 grapheme) + emoji + b
        var graphemes = StatusBadgeListSource.LayOutGraphemes(text).ToList();

        // Char indices are strictly increasing and the widths sum to the whole line's columns.
        Assert.Equal(0, graphemes[0].CharIndex);
        for (var i = 1; i < graphemes.Count; i++)
            Assert.True(graphemes[i].CharIndex > graphemes[i - 1].CharIndex);
        Assert.Equal(text.GetColumns(), graphemes.Sum(g => g.Width));
    }

    [Fact]
    public void BadgeStartColumn_ForAsciiTitle_MatchesBothFormulas_NoRegression()
    {
        // The common case (ASCII names) must be unchanged: the new grapheme layout and the old per-rune
        // formula agree, and both equal the base renderer's width.
        var task = new TaskItem
        {
            Id = "t1",
            Name = "Write the weekly report",
            StatusName = "in progress",
            StatusColor = "#4194e0",
        };
        var row = TaskRowFormatter.Format(task);

        Assert.True(row.StatusStart > 0);
        var expected = row.Text[..row.StatusStart].GetColumns();
        Assert.Equal(expected, LaidOutColumnAt(row.Text, row.StatusStart));
        Assert.Equal(expected, OldPerRuneColumns(row.Text, row.StatusStart));
    }

    [Fact]
    public void BadgeStartColumn_ForMultiEmojiTitle_AlignsToText_AndFixesTheOldOffset()
    {
        // The observed #63 case: titles with more than one emoji. Skin-tone and variation-selector
        // sequences are multi-rune grapheme clusters the base renderer caps at 2 columns, so the old
        // per-rune sum drifted right of the text — and the drift compounded per emoji.
        var task = new TaskItem
        {
            Id = "t2",
            Name = "\U0001F44D\U0001F3FD ship ❤️ it",
            StatusName = "to do",
            StatusColor = "#87909e",
        };
        var row = TaskRowFormatter.Format(task);

        Assert.True(row.StatusStart > 0);
        var baseWidth = row.Text[..row.StatusStart].GetColumns();

        // Fixed: the overlay now lands exactly where the stock renderer drew the '[' of the badge.
        Assert.Equal(baseWidth, LaidOutColumnAt(row.Text, row.StatusStart));

        // Regression capture: the pre-fix formula genuinely disagreed (the offset was real).
        Assert.NotEqual(baseWidth, OldPerRuneColumns(row.Text, row.StatusStart));
    }
}
