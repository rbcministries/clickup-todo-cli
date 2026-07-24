using ClickUpTodo.Tui;
using Terminal.Gui.Drawing;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for the pure separator-line classification in <see cref="DetailPaneView.BuildCells"/> — the
/// logic that tags the inter-block rule so the pane draws it on the terminal-default background. The
/// draw override itself is Terminal.Gui glue and isn't unit-testable in CI.
/// </summary>
public sealed class DetailPaneViewTests
{
    private const string Sep = TaskDetailFormatter.CommentSeparator;

    private static bool IsSeparatorTagged(IReadOnlyList<Cell> line)
        => line.Count > 0 && line.All(c => c.Attribute is { } a && a.Background == Color.None);

    private static bool IsUntagged(IReadOnlyList<Cell> line)
        => line.All(c => c.Attribute is null);

    [Fact]
    public void BuildCells_OneLinePerBodyLine()
    {
        var cells = DetailPaneView.BuildCells("a\nb\nc", Sep);
        Assert.Equal(3, cells.Count);
    }

    [Fact]
    public void BuildCells_TagsSeparatorLineCellsWithTerminalDefaultBackground()
    {
        var body = string.Join('\n', "Author  ·  today", "A comment.", "", Sep, "", "Author2", "Another.");
        var cells = DetailPaneView.BuildCells(body, Sep);

        var separatorRow = cells.Single(IsSeparatorTagged);
        Assert.Equal(Sep.Length, separatorRow.Count);
        // Color.None (alpha 0) is what the driver renders as the terminal's own default background.
        Assert.All(separatorRow, c => Assert.Equal(0, c.Attribute!.Value.Background.A));
    }

    [Fact]
    public void BuildCells_LeavesContentLinesUncoloured()
    {
        var body = string.Join('\n', "Author  ·  today", "A comment.", "", Sep, "", "Another.");
        var cells = DetailPaneView.BuildCells(body, Sep);

        // Every line except the rule is left with null attributes → drawn in the pane's normal colour.
        var tagged = cells.Count(IsSeparatorTagged);
        Assert.Equal(1, tagged);
        foreach (var line in cells.Where(l => !IsSeparatorTagged(l)))
            Assert.True(IsUntagged(line));
    }

    [Fact]
    public void BuildCells_TagsEverySeparatorInAMultiBlockBody()
    {
        // Three blocks → two separators.
        var body = string.Join("\n\n" + Sep + "\n\n", "block one", "block two", "block three");
        var cells = DetailPaneView.BuildCells(body, Sep);
        Assert.Equal(2, cells.Count(IsSeparatorTagged));
    }

    [Fact]
    public void BuildCells_DoesNotTagLinesThatMerelyContainTheRule()
    {
        // A body line that is longer than the bare rule must not be treated as a separator.
        var cells = DetailPaneView.BuildCells(Sep + " trailing", Sep);
        Assert.DoesNotContain(cells, IsSeparatorTagged);
    }

    // ── Link tagging (#317) ────────────────────────────────────────────────────────────────────────
    // BuildCells tags the cells covered by each detected link, by kind, so the draw override can style
    // them. These round-trip through the real Cell.ToCellList, so ClassifyCell reflects what actually
    // lands in the loaded cells (no driver needed to build the model).

    // The substring of `line` whose cells are tagged with `style`, contiguous — used to verify a link
    // tag covers exactly the URL characters and nothing else.
    private static string TaggedText(string line, DetailPaneView.DetailCellStyle style)
    {
        var cells = DetailPaneView.BuildCells(line, Sep).Single();
        var chars = cells.Where(c => DetailPaneView.ClassifyCell(c) == style)
                         .Select(c => c.Grapheme ?? "");
        return string.Concat(chars);
    }

    private static bool AllNormal(string line)
        => DetailPaneView.BuildCells(line, Sep).Single()
            .All(c => DetailPaneView.ClassifyCell(c) == DetailPaneView.DetailCellStyle.Normal);

    [Fact]
    public void BuildCells_TagsWebLinkCells()
    {
        const string line = "See https://example.com/docs for details.";
        Assert.Equal("https://example.com/docs", TaggedText(line, DetailPaneView.DetailCellStyle.WebLink));
        // Nothing spuriously tagged as a task link.
        Assert.Equal("", TaggedText(line, DetailPaneView.DetailCellStyle.TaskLink));
    }

    [Fact]
    public void BuildCells_TagsTaskLinkCells()
    {
        const string line = "Related: https://app.clickup.com/t/abc123 (context)";
        Assert.Equal("https://app.clickup.com/t/abc123", TaggedText(line, DetailPaneView.DetailCellStyle.TaskLink));
        Assert.Equal("", TaggedText(line, DetailPaneView.DetailCellStyle.WebLink));
    }

    [Fact]
    public void BuildCells_TagsMixedTaskAndWebLinksOnOneLine()
    {
        const string line = "task https://app.clickup.com/t/t1 and web https://example.com end";
        Assert.Equal("https://app.clickup.com/t/t1", TaggedText(line, DetailPaneView.DetailCellStyle.TaskLink));
        Assert.Equal("https://example.com", TaggedText(line, DetailPaneView.DetailCellStyle.WebLink));
    }

    [Fact]
    public void BuildCells_LeavesTheSurroundingTextAndLinkFreeLinesNormal()
    {
        // The non-URL characters around a link stay Normal…
        const string line = "See https://example.com now";
        var cells = DetailPaneView.BuildCells(line, Sep).Single();
        var normal = string.Concat(cells.Where(c => DetailPaneView.ClassifyCell(c) == DetailPaneView.DetailCellStyle.Normal)
                                        .Select(c => c.Grapheme ?? ""));
        Assert.Equal("See  now", normal);
        // …and a line with no URL is entirely Normal (guards the existing uncoloured-content invariant).
        Assert.True(AllNormal("A comment with no links at all."));
    }

    [Fact]
    public void BuildCells_DoesNotRunLinkDetectionOnSeparatorLines()
    {
        // The separator rule contains no URL, but assert explicitly it stays a Separator (never a link).
        var cells = DetailPaneView.BuildCells(Sep, Sep).Single();
        Assert.All(cells, c => Assert.Equal(DetailPaneView.DetailCellStyle.Separator, DetailPaneView.ClassifyCell(c)));
    }

    // Exercises the real SetBody → TextView.Load path (no driver needed to load the model) and inspects
    // the loaded cells. This is the reviewer's concern (PR #184): the terminal-default (Color.None)
    // background must stay on the separator line only, and must not carry forward to the comment/
    // description text that follows it. Two safeguards are asserted: every non-separator cell keeps a
    // non-None (or null → normal read-only) background, and attribute inheritance is off so a
    // null-attribute cell can't copy the previous cell's None background.
    [Fact]
    public void SetBody_ConfinesTerminalDefaultBackgroundToSeparatorLines()
    {
        // Three blocks (e.g. a description + two comments) → two separator rules between them.
        var body = string.Join("\n\n" + Sep + "\n\n", "First comment body.", "Second comment body.", "Third comment body.");
        var pane = new DetailPaneView();
        // Inspect the logical model, not wrapped display lines: word-wrap only splits a line into more
        // display rows (each preserving its cells' attributes), so it doesn't affect this confinement.
        pane.WordWrap = false;
        pane.SetBody(body, Sep);

        Assert.False(pane.InheritsPreviousAttribute);

        var lines = pane.GetAllLines();
        var separatorLines = 0;
        foreach (var line in lines)
        {
            if (Cell.ToString(line) == Sep)
            {
                separatorLines++;
                // The whole rule renders on the terminal's own default/transparent background.
                Assert.All(line, c => Assert.Equal(0, c.Attribute!.Value.Background.A));
            }
            else
            {
                // Everything else keeps an opaque background (or none), so it renders in the pane's
                // normal read-only colour — the reset never bleeds past the rule.
                Assert.All(line, c => Assert.NotEqual(0, (c.Attribute?.Background.A) ?? 255));
            }
        }

        Assert.Equal(2, separatorLines);
    }
}
