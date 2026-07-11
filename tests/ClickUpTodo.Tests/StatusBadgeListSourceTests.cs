using System.Collections.ObjectModel;
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
            Assert.Equal(text[..g.CharIndex].GetColumns(), g.Column);
    }

    [Theory]
    [InlineData("a", 1)]                                          // ASCII — 1 column
    [InlineData("会", 2)]                                          // wide CJK ideograph — 2 columns
    [InlineData("\U0001F600", 2)]                                 // emoji — 2 columns
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467", 2)]           // ZWJ family cluster — capped at 2
    public void LayOutGraphemes_ClusterWidth_IsGraphemeAware_CappedAtTwo(string grapheme, int expectedWidth)
    {
        // Independent (hardcoded) widths — not derived from GetColumns — pinning that a whole cluster is
        // one unit whose width is capped at two columns, which is what makes the overlay align (#63).
        var g = Assert.Single(StatusBadgeListSource.LayOutGraphemes(grapheme));
        Assert.Equal(grapheme, g.Text);
        Assert.Equal(0, g.Column);
        Assert.Equal(0, g.CharIndex);
        Assert.Equal(expectedWidth, g.Width);
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
    public void LayOutColumns_ForMultiEmojiTitle_MatchBaseRenderer_AndFixTheOldOffset()
    {
        // The observed #63 case: titles with more than one emoji. Skin-tone and variation-selector
        // sequences are multi-rune grapheme clusters the base renderer caps at 2 columns, so the old
        // per-rune sum drifted right of the text — and the drift compounded per emoji. Badges now lead
        // the row (before the title), but the same grapheme-aware layout still positions everything the
        // header/overlay paints, so pin the invariant on the title text itself.
        const string title = "\U0001F44D\U0001F3FD ship ❤️ it";
        var end = title.Length;

        // Fixed: the layout agrees with the stock renderer's cumulative width at the end of the title.
        Assert.Equal(title.GetColumns(), LaidOutColumnAt(title, end));

        // Regression capture: the pre-fix per-rune formula genuinely disagreed (the offset was real).
        Assert.NotEqual(title.GetColumns(), OldPerRuneColumns(title, end));
    }

    // ── Leading icon-chip gutter ─────────────────────────────────────────────

    [Fact]
    public void IconChips_And_Gutters_OccupyTheirColumnWidths()
    {
        // The grid lines up per column: the ⚑ priority chip and its blank gutter are three display columns
        // (single-column glyph flanked by spaces); the Status abbrev chip "(XX)" and its wider gutter are
        // four (#181). Widths are measured the same grapheme-aware way the overlay positions colour.
        var statusChipWidth = StatusBadgeListSource.LayOutGraphemes(TaskRowFormatter.StatusAbbreviation("In Progress")).Sum(g => g.Width);
        var statusGutterWidth = StatusBadgeListSource.LayOutGraphemes(TaskRowFormatter.StatusGutter).Sum(g => g.Width);
        var priorityWidth = StatusBadgeListSource.LayOutGraphemes(TaskRowFormatter.PriorityIcon).Sum(g => g.Width);
        var blankWidth = StatusBadgeListSource.LayOutGraphemes(TaskRowFormatter.BlankGutter).Sum(g => g.Width);

        Assert.Equal(4, statusChipWidth);
        Assert.Equal(4, statusGutterWidth);
        Assert.Equal(3, priorityWidth);
        Assert.Equal(3, blankWidth);
    }

    [Fact]
    public void StatusChip_ColumnsMatchBaseRenderer()
    {
        // The status chip follows the leading id chip; its overlay must paint exactly the base
        // renderer's column width for that span, so the colour lands on the full four-column "(XX)" abbrev.
        var task = new TaskItem { Id = "1", Name = "Ship it", StatusName = "to do", StatusColor = "#87909e" };
        var row = TaskRowFormatter.Format(task);

        // The id chip ("1 ") precedes the status chip (id text + its trailing separator space).
        Assert.Equal(row.CustomIdLength + 1, row.StatusStart);
        Assert.Equal(row.Text[..row.StatusStart].GetColumns(), LaidOutColumnAt(row.Text, row.StatusStart));
        var end = row.StatusStart + row.StatusLength;
        Assert.Equal(row.Text[..end].GetColumns(), LaidOutColumnAt(row.Text, end));
    }

    [Fact]
    public void PriorityChip_FollowingStatusChip_ColumnsMatchBaseRenderer()
    {
        // The priority chip sits immediately after the status chip (which itself follows the leading id
        // chip); both its start and end overlay columns must equal the base renderer's cumulative width
        // there, or the tint drifts.
        var task = new TaskItem
        {
            Id = "1",
            Name = "Ship it",
            StatusName = "to do",
            StatusColor = "#87909e",
            PriorityName = "Urgent",
            PriorityColor = "#f50000",
        };
        var row = TaskRowFormatter.Format(task);

        Assert.Equal(row.StatusStart + row.StatusLength, row.PriorityStart);
        Assert.Equal(row.Text[..row.PriorityStart].GetColumns(), LaidOutColumnAt(row.Text, row.PriorityStart));
        var end = row.PriorityStart + row.PriorityLength;
        Assert.Equal(row.Text[..end].GetColumns(), LaidOutColumnAt(row.Text, end));
    }

    // ── Type-ahead search key decoupling (#76) ───────────────────────────────

    private static readonly IReadOnlyList<IReadOnlyList<StatusBadgeListSource.Badge>> NoBadges =
        Array.Empty<IReadOnlyList<StatusBadgeListSource.Badge>>();

    [Fact]
    public void ToList_WithSearchKeys_ReturnsTitleOnlyKeys_NotDecoratedDisplayText()
    {
        // The rendered lines carry the ▶/▼ marker + badges; the type-ahead navigator (#12) must search
        // the parallel title-only keys instead, so typing a title's first letters still jumps.
        var display = new ObservableCollection<string> { "▶ Write report  [in progress]", "  ▼ Fix bug  [to do]" };
        var keys = new[] { "Write report", "Fix bug" };

        var source = new StatusBadgeListSource(display, NoBadges, headerAttrs: null, searchKeys: keys);
        var list = source.ToList();

        Assert.Equal(["Write report", "Fix bug"], list.Cast<string>());
        // None of the keys leak the fold marker glyphs.
        Assert.All(list.Cast<string>(), k => Assert.DoesNotContain('▶', k));
        Assert.All(list.Cast<string>(), k => Assert.DoesNotContain('▼', k));
    }

    [Fact]
    public void ToList_WithoutSearchKeys_DelegatesToDisplayText()
    {
        // Backward compatibility: with no keys supplied, ToList() returns the stock display strings.
        var display = new ObservableCollection<string> { "Write report", "Fix bug" };

        var source = new StatusBadgeListSource(display, NoBadges);
        var list = source.ToList();

        Assert.Equal(["Write report", "Fix bug"], list.Cast<string>());
    }
}
