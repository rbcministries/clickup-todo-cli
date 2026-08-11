using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

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

    // ── Wrapped-row link classification (#413) ───────────────────────────────────────────────────────
    // The draw override recomputes a rendered row's link cells from the row's OWN graphemes — not the
    // per-cell tags BuildCells applied — because Terminal.Gui 2.4.10's word wrap keeps a wrapped row's
    // graphemes (from the wrap offset) but rebuilds its attributes from index 0 of the source line, so a
    // continuation row's tags land on the wrong cells and the underline is drawn in the wrong columns.
    // ClassifyRowLinkCells is offset-free (re-extracts per row), so a URL that does NOT start at column 0
    // is still styled on exactly its own cells. These build a row's cells the same way the pane loads them
    // (BuildCells → one line) and read only the graphemes back, which is all the helper consumes.

    // The substring of `row` whose cells ClassifyRowLinkCells classifies as `style`.
    private static string RowStyledText(string row, DetailPaneView.DetailCellStyle style)
    {
        var cells = DetailPaneView.BuildCells(row, Sep).Single();
        var styles = DetailPaneView.ClassifyRowLinkCells(cells);
        var chars = new List<string>();
        for (var i = 0; i < cells.Count; i++)
            if (styles[i] == style)
                chars.Add(cells[i].Grapheme ?? "");
        return string.Concat(chars);
    }

    [Fact]
    public void ClassifyRowLinkCells_StylesATaskUrlThatDoesNotStartAtColumnZero()
    {
        // The exact shape from the bug report: a wrapped continuation row where prose precedes the URL.
        const string row = "Parent ticket: https://app.clickup.com/t/86a1b2c3d for the full";
        Assert.Equal("https://app.clickup.com/t/86a1b2c3d",
            RowStyledText(row, DetailPaneView.DetailCellStyle.TaskLink));
        Assert.Equal("", RowStyledText(row, DetailPaneView.DetailCellStyle.WebLink));
    }

    [Fact]
    public void ClassifyRowLinkCells_StylesAWebUrlThatDoesNotStartAtColumnZero()
    {
        const string row = "PR: https://github.com/rbcministries/ODBM.Secure/pull/64 — Ready";
        Assert.Equal("https://github.com/rbcministries/ODBM.Secure/pull/64",
            RowStyledText(row, DetailPaneView.DetailCellStyle.WebLink));
        Assert.Equal("", RowStyledText(row, DetailPaneView.DetailCellStyle.TaskLink));
    }

    [Fact]
    public void ClassifyRowLinkCells_LeavesSurroundingProseNormal()
    {
        const string row = "See https://example.com/docs now";
        var cells = DetailPaneView.BuildCells(row, Sep).Single();
        var styles = DetailPaneView.ClassifyRowLinkCells(cells);
        var normal = new List<string>();
        for (var i = 0; i < cells.Count; i++)
            if (styles[i] == DetailPaneView.DetailCellStyle.Normal)
                normal.Add(cells[i].Grapheme ?? "");
        Assert.Equal("See  now", string.Concat(normal));
    }

    [Fact]
    public void ClassifyRowLinkCells_ReturnsAllNormalForALinkFreeRow()
    {
        var cells = DetailPaneView.BuildCells("A continuation row with no link at all", Sep).Single();
        var styles = DetailPaneView.ClassifyRowLinkCells(cells);
        Assert.All(styles, s => Assert.Equal(DetailPaneView.DetailCellStyle.Normal, s));
    }

    [Fact]
    public void ClassifyRowLinkCells_ReturnsAllNormalForASeparatorLine()
    {
        // A separator line carries no URL, so the link classifier leaves it Normal — the draw path styles
        // the separator from its (uniform, wrap-safe) tag, not from this classifier.
        var cells = DetailPaneView.BuildCells(Sep, Sep).Single();
        var styles = DetailPaneView.ClassifyRowLinkCells(cells);
        Assert.All(styles, s => Assert.Equal(DetailPaneView.DetailCellStyle.Normal, s));
    }

    [Fact]
    public void ClassifyRowLinkCells_MapsOffsetsAcrossAWideGraphemeBeforeTheUrl()
    {
        // A surrogate-pair grapheme precedes the URL, so the URL's char offset exceeds its cell index —
        // the classifier must map by accumulated grapheme length, not cell index, to stay aligned.
        const string row = "𝕏 https://example.com end";
        Assert.Equal("https://example.com", RowStyledText(row, DetailPaneView.DetailCellStyle.WebLink));
    }

    [Fact]
    public void ClassifyRowLinkCells_StylesTwoLinksOnOneRow()
    {
        const string row = "task https://app.clickup.com/t/t1 and web https://example.com end";
        Assert.Equal("https://app.clickup.com/t/t1", RowStyledText(row, DetailPaneView.DetailCellStyle.TaskLink));
        Assert.Equal("https://example.com", RowStyledText(row, DetailPaneView.DetailCellStyle.WebLink));
    }

    // ── OSC-8 hyperlink target (#380/#430) ───────────────────────────────────────────────────────────
    // LinkUrlForCell returns the resolved LinkSpan.Url for a link cell (via RowLinkUrls' per-row
    // re-extraction) so the draw override can feed it to IDriver.CurrentUrl — a bare link's own URL, and a
    // markdown link's true target on its visible-text cells (#430). Pure — exercised through the real
    // Cell.ToCellList model, no driver.

    // The single loaded line for `line`, as a cell list.
    private static IReadOnlyList<Cell> Line(string line) => DetailPaneView.BuildCells(line, Sep).Single();

    [Fact]
    public void LinkUrlForCell_ReturnsUrl_ForEveryCellOfABareWebLink()
    {
        const string url = "https://example.com/docs";
        const string line = "See " + url + " for details.";
        var cells = Line(line);
        var first = line.IndexOf(url, StringComparison.Ordinal);

        // First, a middle, and the last cell of the URL run all resolve to the whole URL.
        Assert.Equal(url, DetailPaneView.LinkUrlForCell(cells, first));
        Assert.Equal(url, DetailPaneView.LinkUrlForCell(cells, first + url.Length / 2));
        Assert.Equal(url, DetailPaneView.LinkUrlForCell(cells, first + url.Length - 1));
    }

    [Fact]
    public void LinkUrlForCell_ReturnsUrl_ForABareTaskLink()
    {
        const string url = "https://app.clickup.com/t/abc123";
        const string line = "Related: " + url + " (context)";
        var cells = Line(line);
        var at = line.IndexOf(url, StringComparison.Ordinal);
        Assert.Equal(url, DetailPaneView.LinkUrlForCell(cells, at));
    }

    [Fact]
    public void LinkUrlForCell_IsNull_ForNonLinkCells()
    {
        const string line = "See https://example.com now";
        var cells = Line(line);
        // Column 0 ('S') is prose, and the trailing " now" is prose too — neither is a link cell.
        Assert.Null(DetailPaneView.LinkUrlForCell(cells, 0));
        Assert.Null(DetailPaneView.LinkUrlForCell(cells, line.Length - 1));
    }

    [Fact]
    public void LinkUrlForCell_ResolvesEachLink_WhenTwoShareALine()
    {
        const string task = "https://app.clickup.com/t/t1";
        const string web = "https://example.com";
        const string line = "task " + task + " and web " + web + " end";
        var cells = Line(line);
        Assert.Equal(task, DetailPaneView.LinkUrlForCell(cells, line.IndexOf(task, StringComparison.Ordinal)));
        Assert.Equal(web, DetailPaneView.LinkUrlForCell(cells, line.IndexOf(web, StringComparison.Ordinal)));
    }

    [Fact]
    public void LinkUrlForCell_IsNull_OutOfRange()
    {
        var cells = Line("See https://example.com now");
        Assert.Null(DetailPaneView.LinkUrlForCell(cells, -1));
        Assert.Null(DetailPaneView.LinkUrlForCell(cells, cells.Count));
        Assert.Null(DetailPaneView.LinkUrlForCell(cells, 999));
    }

    [Fact]
    public void LinkUrlForCell_ReturnsTheResolvedTarget_ForAMarkdownLink()
    {
        // A markdown link renders its visible text ("click here"); its true target lives in the (url) markup.
        // #430: because the whole "[click here](url)" sits on one rendered row, re-extracting the row recovers
        // the LinkSpan and its resolved Url, so every visible-text cell carries the true destination.
        const string target = "https://example.com/deep";
        const string line = "See [click here](" + target + ") now";
        var cells = Line(line);
        const string visible = "click here";
        var first = line.IndexOf(visible, StringComparison.Ordinal);
        // The visible text is tagged as a (web) link by #317's rendering…
        Assert.Equal(DetailPaneView.DetailCellStyle.WebLink, DetailPaneView.ClassifyCell(cells[first]));
        // …and every cell of it now resolves to the markdown's RESOLVED target (not the visible prose).
        Assert.Equal(target, DetailPaneView.LinkUrlForCell(cells, first));
        Assert.Equal(target, DetailPaneView.LinkUrlForCell(cells, first + visible.Length / 2));
        Assert.Equal(target, DetailPaneView.LinkUrlForCell(cells, first + visible.Length - 1));
    }

    [Fact]
    public void LinkUrlForCell_ReturnsTheTarget_NotTheVisibleUrl_ForAMarkdownLinkWhoseTextIsAUrl()
    {
        // When a markdown link's VISIBLE text is itself a URL, the OSC-8 target is still the markdown's true
        // destination, not the displayed URL (#430) — the reader's Ctrl+click follows where the link points.
        const string target = "https://example.com/b";
        const string visibleUrl = "https://example.com/a";
        const string line = "See [" + visibleUrl + "](" + target + ") now";
        var cells = Line(line);
        var at = line.IndexOf(visibleUrl, StringComparison.Ordinal);
        Assert.Equal(target, DetailPaneView.LinkUrlForCell(cells, at));
    }

    [Fact]
    public void LinkUrlForCell_StillResolvesTheUrl_WhenTheLinkIsKeyboardFocused()
    {
        // A keyboard-focused link (#319) carries FocusedLinkMarker instead of its kind marker, so its cells
        // classify as FocusedLink — LinkUrlForCell must still recognise the run and emit its OSC-8 target,
        // else focusing a link with Tab would drop its native hyperlink (#380 × #319 interaction).
        const string url = "https://app.clickup.com/t/abc123";
        const string line = "Related: " + url + " (context)";
        var focused = DetailPaneView.ExtractPaneLinks(line, Sep)[0];
        var cells = DetailPaneView.BuildCells(line, Sep, focused).Single();
        var at = line.IndexOf(url, StringComparison.Ordinal);
        Assert.Equal(DetailPaneView.DetailCellStyle.FocusedLink, DetailPaneView.ClassifyCell(cells[at]));
        Assert.Equal(url, DetailPaneView.LinkUrlForCell(cells, at));
    }

    // ── Per-row OSC-8 targets (#380/#430): RowLinkUrls ───────────────────────────────────────────────
    // RowLinkUrls is the per-row projection LinkUrlForCell + the draw path read: one resolved URL per cell,
    // from the row's own graphemes (offset-correct on wrapped rows) and the extracted LinkSpan (so a markdown
    // link carries its true target, not its prose). Pure — no driver.

    [Fact]
    public void RowLinkUrls_CarriesTheResolvedTarget_OnlyOnAMarkdownLinksVisibleTextCells()
    {
        const string target = "https://example.com/deep";
        const string visible = "click here";
        const string line = "See [" + visible + "](" + target + ") end";
        var urls = DetailPaneView.RowLinkUrls(Line(line));

        var vis = line.IndexOf(visible, StringComparison.Ordinal);
        // Every visible-text cell carries the resolved target…
        for (var i = vis; i < vis + visible.Length; i++)
            Assert.Equal(target, urls[i]);
        // …while the surrounding prose, the '[' / '](' markup, and the raw URL inside the parens carry none
        // (only the visible text is the hyperlink; the markup is not separately tagged).
        Assert.Null(urls[0]);                                                       // "S" of "See"
        Assert.Null(urls[vis - 1]);                                                 // the '[' before the text
        Assert.Null(urls[line.IndexOf(target, StringComparison.Ordinal)]);          // inside the (url) markup
        Assert.Null(urls[^1]);                                                      // trailing "end"
    }

    [Fact]
    public void RowLinkUrls_CarriesTheUrl_OnEachOfTwoBareLinksSharingARow()
    {
        const string task = "https://app.clickup.com/t/t1";
        const string web = "https://example.com";
        const string line = "task " + task + " and web " + web + " end";
        var urls = DetailPaneView.RowLinkUrls(Line(line));
        Assert.Equal(task, urls[line.IndexOf(task, StringComparison.Ordinal)]);
        Assert.Equal(web, urls[line.IndexOf(web, StringComparison.Ordinal)]);
        Assert.Null(urls[0]);
    }

    [Fact]
    public void RowLinkUrls_IsAllNull_ForALinkFreeRowAndASeparator()
    {
        Assert.All(DetailPaneView.RowLinkUrls(Line("no links on this row at all")), Assert.Null);
        Assert.All(DetailPaneView.RowLinkUrls(Line(Sep)), Assert.Null);
    }

    [Fact]
    public void RowLinkUrls_IsNull_OnTheVisibleTextFragmentOfASplitMarkdownLink()
    {
        // #443 safe-degradation: when a markdown link's "[text]" and "(url)" wrap onto different rendered
        // rows, the visible-text row has no complete markdown link (and no bare http), so re-extraction
        // finds nothing and no wrong OSC-8 target is invented on the prose.
        const string fragment = "See [click here";   // the "(https://example.com/deep)" wrapped to the next row
        var urls = DetailPaneView.RowLinkUrls(Line(fragment));
        Assert.All(urls, Assert.Null);
    }

    [Fact]
    public void RowLinkUrls_LinksTheUrlFragmentOfASplitMarkdownLinkAsABareUrl()
    {
        // The other half of the #443 split case: when "(url)" wraps onto its own row, that fragment
        // re-extracts as an ordinary BARE link to the URL itself (the trailing ')' trimmed) — the safe
        // degradation (correct URL, never a wrong/hidden target), not the markdown resolution. Pinned so the
        // behaviour is explicit rather than incidental.
        const string target = "https://example.com/deep";
        const string fragment = "(" + target + ")";   // the "See [click here]" wrapped to the previous row
        var urls = DetailPaneView.RowLinkUrls(Line(fragment));
        Assert.Equal(target, urls[fragment.IndexOf(target, StringComparison.Ordinal)]);
        Assert.Null(urls[0]);    // the leading '(' is prose, outside the link span
        Assert.Null(urls[^1]);   // the trailing ')' is trimmed off the URL, so it carries no target
    }

    // ── Wrap-split link styling (#443): BuildRowSourceMap + ClassifyRowFromSource ────────────────────
    // The per-row re-extraction above (#413/#430) is blind across a wrap boundary, so a link token word
    // wrap splits across two rendered rows is styled on only the fragment(s) that still parse in isolation.
    // BuildRowSourceMap reconciles Terminal.Gui's published wrapped output (GetAllLines) against the source
    // lines, and ClassifyRowFromSource styles each row from its SOURCE line's spans — so a split link is
    // styled contiguously on every row it touches. These drive a REAL headless pane (SetBody + Rewrap, as
    // the click tests do) so the reconciliation is exercised against genuine wrap output, not hand-built rows.

    // Wraps `body` in a real pane at `width` and returns its wrapped rows plus the display→source map.
    private static (List<List<Cell>> Rows, IReadOnlyList<DetailPaneView.RowSource> Map) WrapAndMap(
        string body, int width, string separator = Sep)
    {
        var pane = new DetailPaneView { Frame = new Rectangle(0, 0, width, 40) };
        pane.SetBody(body, separator);
        Rewrap(pane);
        var rows = pane.GetAllLines();
        return (rows, DetailPaneView.BuildRowSourceMap(body.Split('\n'), rows));
    }

    private static string GraphemeText(IReadOnlyList<Cell> row) => string.Concat(row.Select(c => c.Grapheme ?? ""));

    // The graphemes ClassifyRowFromSource styles as a link across all rows (in row/cell order), the distinct
    // resolved targets it assigns them, and how many distinct rows carried a styled cell (so a test can prove
    // the link actually spanned >1 row). Asserts every row reconciled — a -1 would mean the map failed.
    private static (string Styled, HashSet<string?> Targets, int RowsWithStyle) StyledAcrossRows(
        IReadOnlyList<List<Cell>> rows, IReadOnlyList<DetailPaneView.RowSource> map, string[] sourceLines)
    {
        var styled = new List<string>();
        var targets = new HashSet<string?>();
        var rowsWithStyle = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var src = map[r];
            Assert.True(src.SourceLineIndex >= 0, $"row {r} '{GraphemeText(rows[r])}' did not reconcile to a source line");
            var (styles, urls) = DetailPaneView.ClassifyRowFromSource(rows[r], sourceLines[src.SourceLineIndex], src.StartOffset);
            var any = false;
            for (var i = 0; i < rows[r].Count; i++)
                if (styles[i] is DetailPaneView.DetailCellStyle.TaskLink or DetailPaneView.DetailCellStyle.WebLink)
                {
                    styled.Add(rows[r][i].Grapheme ?? "");
                    targets.Add(urls[i]);
                    any = true;
                }
            if (any)
                rowsWithStyle++;
        }
        return (string.Concat(styled), targets, rowsWithStyle);
    }

    [Fact]
    public void BuildRowSourceMap_EveryRowReconstructsExactlyItsSourceSubstring()
    {
        // A hard-wrapping URL, a blank line, a wrapping separator rule, and a short line — every wrapped row
        // must map to a (source line, offset) whose substring is byte-for-byte the row's own text.
        const string longUrl = "https://example.com/a/very/long/path/that/hard/wraps/xyz";
        var body = string.Join('\n',
            "Parent ticket: " + TaskUrl + " for the full thread",
            "",
            Sep,
            "See " + longUrl + " tail",
            "plain line");
        var (rows, map) = WrapAndMap(body, width: 22);
        var sourceLines = body.Split('\n');

        Assert.Equal(rows.Count, map.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var src = map[i];
            var rowText = GraphemeText(rows[i]);
            Assert.True(src.SourceLineIndex >= 0, $"row {i} '{rowText}' did not reconcile");
            var line = sourceLines[src.SourceLineIndex];
            Assert.True(src.StartOffset + rowText.Length <= line.Length,
                $"row {i} '{rowText}' offset {src.StartOffset} overruns source line {src.SourceLineIndex} (len {line.Length})");
            Assert.Equal(rowText, line.Substring(src.StartOffset, rowText.Length));
        }
    }

    [Fact]
    public void ClassifyRowFromSource_UnderlinesABareUrlContiguouslyAcrossAHardWrap()
    {
        // Case 1 of #443: a URL longer than the pane inner width hard-wraps mid-URL. Per-row re-extraction
        // styles only the fragments that still parse; the source-driven path styles the WHOLE url on every
        // row it lands on, and nothing else.
        const string url = "https://example.com/a/very/long/path/that/hard/wraps/across/rows";
        var body = "See " + url + " end";
        var (rows, map) = WrapAndMap(body, width: 20);

        var (styled, targets, rowsWithStyle) = StyledAcrossRows(rows, map, body.Split('\n'));
        Assert.True(rowsWithStyle >= 2, $"the URL should span >= 2 rows at this width (spanned {rowsWithStyle})");
        Assert.Equal(url, styled);                 // exactly the URL cells, contiguously, across the wrap
        Assert.Equal(url, Assert.Single(targets)); // one resolved target — the whole URL — on every cell
    }

    [Fact]
    public void ClassifyRowFromSource_UnderlinesMarkdownVisibleTextAcrossAWrap_ButNotTheMarkup()
    {
        // Case 2 of #443: a markdown link whose visible text wraps across rows. Every visible-text cell is
        // styled with the RESOLVED target on whichever row it lands on; the [ ] ( url ) markup stays unstyled.
        const string target = "https://example.com/runbook";
        const string visible = "the operations runbook and deploy guide";
        var body = "See [" + visible + "](" + target + ") now";
        var (rows, map) = WrapAndMap(body, width: 18);

        var (styled, targets, rowsWithStyle) = StyledAcrossRows(rows, map, body.Split('\n'));
        Assert.True(rowsWithStyle >= 2, $"the visible text should span >= 2 rows at this width (spanned {rowsWithStyle})");
        Assert.Equal(visible, styled);                // exactly the visible text — not the '[', ']', or (url)
        Assert.Equal(target, Assert.Single(targets)); // the resolved target, not the visible prose
    }

    [Fact]
    public void ClassifyRowFromSource_StylesOnlyTheSourceSpan_NotTrailingProseOnTheRow()
    {
        // A continuation fragment that starts mid-URL and runs into trailing prose: the URL tail is styled
        // with the whole URL as target; the prose past the span is not.
        const string url = "https://example.com/some/deep/path";
        var source = "ref " + url + " end";
        const int startOffset = 4 + 15;                       // a row starting 15 chars into the URL (offset 4)
        var rowText = url[15..] + " end";
        var row = DetailPaneView.BuildCells(rowText, Sep).Single();

        var (styles, urls) = DetailPaneView.ClassifyRowFromSource(row, source, startOffset);

        var styled = string.Concat(Enumerable.Range(0, row.Count)
            .Where(i => styles[i] == DetailPaneView.DetailCellStyle.WebLink)
            .Select(i => row[i].Grapheme ?? ""));
        Assert.Equal(url[15..], styled);
        Assert.All(Enumerable.Range(0, row.Count).Where(i => styles[i] == DetailPaneView.DetailCellStyle.WebLink),
            i => Assert.Equal(url, urls[i]));
        Assert.All(Enumerable.Range(0, row.Count).Where(i => styles[i] == DetailPaneView.DetailCellStyle.Normal),
            i => Assert.Null(urls[i]));
    }

    [Fact]
    public void ClassifyRowFromSource_LeavesLinkFreeAndSeparatorRowsUnstyled()
    {
        var (s1, u1) = DetailPaneView.ClassifyRowFromSource(
            DetailPaneView.BuildCells("no links here at all", Sep).Single(), "no links here at all", 0);
        Assert.All(s1, s => Assert.Equal(DetailPaneView.DetailCellStyle.Normal, s));
        Assert.All(u1, Assert.Null);

        var (s2, u2) = DetailPaneView.ClassifyRowFromSource(
            DetailPaneView.BuildCells(Sep, Sep).Single(), Sep, 0);
        Assert.All(s2, s => Assert.Equal(DetailPaneView.DetailCellStyle.Normal, s));
        Assert.All(u2, Assert.Null);
    }

    [Fact]
    public void BuildRowSourceMap_ResolvesTheCorrectOccurrence_WhenTheSourceLineRepeatsAFragment()
    {
        // The source line repeats "dup " (so a wrapped row's text also appears earlier in the same line)
        // before a unique URL. Reconciliation must map the URL's continuation row to the CORRECT (later)
        // occurrence via the running cursor; a mis-map to an earlier "dup" offset would slide the URL off
        // its source span and it would not come back styled — so `styled == url` proves the right occurrence.
        const string url = "https://ex.io/uniqpath/aaaa/bbbb/cccc/dddd";
        var body = "dup dup dup dup dup dup dup dup " + url + " end";
        var (rows, map) = WrapAndMap(body, width: 18);

        var (styled, targets, rowsWithStyle) = StyledAcrossRows(rows, map, body.Split('\n'));
        Assert.True(rowsWithStyle >= 2, $"the URL should span >= 2 rows at this width (spanned {rowsWithStyle})");
        Assert.Equal(url, styled);
        Assert.Equal(url, Assert.Single(targets));
    }

    [Fact]
    public void ClassifyRowFromSource_UnderlinesATaskUrlContiguouslyAcrossAHardWrap()
    {
        // The Task-vs-Web branch on the split path: a long ClickUp task URL hard-wraps; every row it lands
        // on must be styled TaskLink (not WebLink) with the whole URL as target.
        const string taskUrl = "https://app.clickup.com/t/86a1b2c3dabcdefghij1234567890";
        var body = "See " + taskUrl + " end";
        var (rows, map) = WrapAndMap(body, width: 20);
        var sourceLines = body.Split('\n');

        var styled = new List<string>();
        var kinds = new HashSet<DetailPaneView.DetailCellStyle>();
        var targets = new HashSet<string?>();
        var rowsWithStyle = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var src = map[r];
            Assert.True(src.SourceLineIndex >= 0, $"row {r} did not reconcile");
            var (styles, urls) = DetailPaneView.ClassifyRowFromSource(rows[r], sourceLines[src.SourceLineIndex], src.StartOffset);
            var any = false;
            for (var i = 0; i < rows[r].Count; i++)
                if (styles[i] is DetailPaneView.DetailCellStyle.TaskLink or DetailPaneView.DetailCellStyle.WebLink)
                {
                    styled.Add(rows[r][i].Grapheme ?? "");
                    kinds.Add(styles[i]);
                    targets.Add(urls[i]);
                    any = true;
                }
            if (any)
                rowsWithStyle++;
        }
        Assert.True(rowsWithStyle >= 2, $"the task URL should span >= 2 rows at this width (spanned {rowsWithStyle})");
        Assert.Equal(taskUrl, string.Concat(styled));
        Assert.Equal(taskUrl, Assert.Single(targets));
        Assert.Equal(DetailPaneView.DetailCellStyle.TaskLink, Assert.Single(kinds));
    }

    [Fact]
    public void ClassifyRowFromSource_AccountsForAWideGraphemeBeforeTheUrl()
    {
        // A multi-UTF-16 grapheme (🛠️ — a surrogate pair + VS16) before the URL: each cell's source offset
        // must advance by the grapheme's char length, not by 1 (cell index), or the URL cells would map to
        // the wrong source offsets and miss the span.
        const string url = "https://ex.io/path";
        var source = "🛠️ " + url;
        var row = DetailPaneView.BuildCells(source, Sep).Single();

        var (styles, urls) = DetailPaneView.ClassifyRowFromSource(row, source, 0);

        var styled = string.Concat(Enumerable.Range(0, row.Count)
            .Where(i => styles[i] == DetailPaneView.DetailCellStyle.WebLink)
            .Select(i => row[i].Grapheme ?? ""));
        Assert.Equal(url, styled);
        Assert.All(Enumerable.Range(0, row.Count).Where(i => styles[i] == DetailPaneView.DetailCellStyle.WebLink),
            i => Assert.Equal(url, urls[i]));
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

    // ── Mouse link activation (#318) ───────────────────────────────────────────────────────────────
    // These drive a real, laid-out DetailPaneView through the public View.NewMouseEvent entry point —
    // no Application / driver needed, since nothing is drawn. They therefore pin the actual Terminal.Gui
    // behaviour the click design rests on: that the base view maps a click to a text position and reports
    // it in unwrapped (source-line) coordinates, that it does *not* do so for a modified click, and the
    // two ways it clamps a click outside the text onto a position that would otherwise read as a hit.

    private const string TaskUrl = "https://app.clickup.com/t/abc123";
    private const string WebUrl = "https://example.com/docs";

    // A pane laid out at a fixed size (so wrapping is deterministic) with its activation requests captured.
    private static (DetailPaneView Pane, List<LinkActivationRequest> Requests) ClickablePane(
        string body, int width = 30, int height = 10, string separator = Sep)
    {
        var pane = new DetailPaneView { Frame = new Rectangle(0, 0, width, height) };
        pane.SetBody(body, separator);
        // SetBody's TextView.Load leaves the model *unwrapped* until Terminal.Gui's own draw/viewport pass
        // re-wraps it — which always happens before a user can click, but never here, because a unit test
        // has no driver to draw with. Toggling WordWrap performs that wrap, so these clicks land on the
        // wrapped layout a user actually sees.
        Rewrap(pane);
        var requests = new List<LinkActivationRequest>();
        pane.LinkActivationRequested += (_, request) => requests.Add(request);
        return (pane, requests);
    }

    private static void Click(DetailPaneView pane, Point at, bool ctrl = false)
        => ClickWith(pane, at, MouseFlags.LeftButtonClicked | (ctrl ? MouseFlags.Ctrl : MouseFlags.None));

    private static void ClickWith(DetailPaneView pane, Point at, MouseFlags flags)
        => pane.NewMouseEvent(new Mouse { Position = at, Flags = flags });

    // Re-wraps the model the way Terminal.Gui's draw/viewport pass does (see ClickablePane) — needed again
    // after any SetBody, since TextView.Load leaves the model unwrapped.
    private static void Rewrap(DetailPaneView pane)
    {
        pane.WordWrap = false;
        pane.WordWrap = true;
    }

    // The viewport (column, row) of `needle` in the pane's wrapped display lines. Positions are derived
    // from the real wrapped layout rather than hard-coded, and converted char index → cell index →
    // column so a line carrying wide runes is still addressed by the column a terminal would report.
    private static Point Locate(DetailPaneView pane, string needle)
    {
        var lines = pane.GetAllLines();
        for (var row = 0; row < lines.Count; row++)
        {
            var cells = lines[row];
            var charIndex = Cell.ToString(cells).IndexOf(needle, StringComparison.Ordinal);
            if (charIndex < 0)
                continue;

            var chars = 0;
            var cellIndex = 0;
            while (cellIndex < cells.Count && chars < charIndex)
                chars += cells[cellIndex++].Grapheme?.Length ?? 0;
            return new Point(pane.GetColumnsWidth(cells.Take(cellIndex).ToList()), row);
        }

        throw new InvalidOperationException($"'{needle}' is not in the pane's wrapped lines.");
    }

    [Fact]
    public void Click_OnATaskLink_RequestsTaskDetail()
    {
        var (pane, requests) = ClickablePane($"Related: {TaskUrl} ok", width: 60);

        Click(pane, Locate(pane, TaskUrl));

        var request = Assert.Single(requests);
        Assert.Equal(LinkAction.OpenTaskDetail, request.Action);
        Assert.Equal(TaskUrl, request.Url);
        Assert.Equal(LinkKind.Task, request.Span.Kind);
        Assert.Equal("abc123", request.Span.TaskId);
    }

    [Fact]
    public void Click_OnAWebLink_RequestsTheBrowser()
    {
        var (pane, requests) = ClickablePane($"See {WebUrl} now", width: 60);

        Click(pane, Locate(pane, WebUrl));

        var request = Assert.Single(requests);
        Assert.Equal(LinkAction.OpenInBrowser, request.Action);
        Assert.Equal(WebUrl, request.Url);
    }

    [Fact]
    public void CtrlClick_OnEitherKind_RequestsTheBrowser()
    {
        // Ctrl+click is the case a stale caret silently gets wrong: TextView only maps positions for an
        // *unmodified* click, so clicking a web link first and then Ctrl+clicking the task link would
        // report the web link's position if the pane didn't re-resolve the position itself.
        var (pane, requests) = ClickablePane($"web {WebUrl}\ntask {TaskUrl}", width: 60);

        Click(pane, Locate(pane, WebUrl));
        Click(pane, Locate(pane, TaskUrl), ctrl: true);

        Assert.Equal(2, requests.Count);
        Assert.Equal(LinkAction.OpenInBrowser, requests[0].Action);
        Assert.Equal(WebUrl, requests[0].Url);
        // The Ctrl+click resolved the *task* link (not the previously clicked web link) and, being
        // Ctrl-modified, asks for the browser rather than the in-app detail.
        Assert.Equal(LinkAction.OpenInBrowser, requests[1].Action);
        Assert.Equal(TaskUrl, requests[1].Url);
        Assert.Equal(LinkKind.Task, requests[1].Span.Kind);
    }

    [Fact]
    public void CtrlClick_OnTaskLink_WhenDestinationIsNewTerminalTab_RequestsNewTab()
    {
        // #320: the configured Ctrl+Click destination flows through the pane into Resolve.
        var (pane, requests) = ClickablePane($"Related: {TaskUrl} ok", width: 60);
        pane.TaskLinkCtrlClickDestination = TaskLinkCtrlClickDestination.NewTerminalTab;

        Click(pane, Locate(pane, TaskUrl), ctrl: true);

        var request = Assert.Single(requests);
        Assert.Equal(LinkAction.OpenTaskInNewTab, request.Action);
        Assert.Equal(LinkKind.Task, request.Span.Kind);
        Assert.Equal("abc123", request.Span.TaskId);
    }

    [Theory]
    // (configured default, expected Ctrl+Shift action) — Ctrl+Shift inverts the default (#320).
    [InlineData(TaskLinkCtrlClickDestination.Browser, LinkAction.OpenTaskInNewTab)]
    [InlineData(TaskLinkCtrlClickDestination.NewTerminalTab, LinkAction.OpenInBrowser)]
    public void CtrlShiftClick_OnTaskLink_InvertsTheConfiguredDestination(
        TaskLinkCtrlClickDestination destination, LinkAction expected)
    {
        var (pane, requests) = ClickablePane($"Related: {TaskUrl} ok", width: 60);
        pane.TaskLinkCtrlClickDestination = destination;

        ClickWith(pane, Locate(pane, TaskUrl), MouseFlags.LeftButtonClicked | MouseFlags.Ctrl | MouseFlags.Shift);

        var request = Assert.Single(requests);
        Assert.Equal(expected, request.Action);
        Assert.Equal(LinkKind.Task, request.Span.Kind);
    }

    [Theory]
    [InlineData(TaskLinkCtrlClickDestination.Browser)]
    [InlineData(TaskLinkCtrlClickDestination.NewTerminalTab)]
    public void CtrlShiftClick_OnAWebLink_StillOpensBrowser(TaskLinkCtrlClickDestination destination)
    {
        // A web link ignores the task-link destination entirely, under every modifier.
        var (pane, requests) = ClickablePane($"See {WebUrl} now", width: 60);
        pane.TaskLinkCtrlClickDestination = destination;

        ClickWith(pane, Locate(pane, WebUrl), MouseFlags.LeftButtonClicked | MouseFlags.Ctrl | MouseFlags.Shift);

        var request = Assert.Single(requests);
        Assert.Equal(LinkAction.OpenInBrowser, request.Action);
        Assert.Equal(WebUrl, request.Url);
    }

    [Fact]
    public void Click_OnOrdinaryText_ActivatesNothing()
    {
        var (pane, requests) = ClickablePane($"Related: {TaskUrl} tail", width: 60);

        Click(pane, Locate(pane, "Related"));
        Click(pane, Locate(pane, "tail"));

        Assert.Empty(requests);
    }

    [Fact]
    public void Click_JustPastALinksLastCharacter_ActivatesNothing()
    {
        // The exclusive end of a span — one column right of the URL's last character.
        var (pane, requests) = ClickablePane($"See {WebUrl} now", width: 60);
        var start = Locate(pane, WebUrl);

        Click(pane, new Point(start.X + WebUrl.Length, start.Y));

        Assert.Empty(requests);
    }

    [Fact]
    public void Click_OnASeparatorRule_ActivatesNothing()
    {
        var (pane, requests) = ClickablePane($"See {WebUrl}\n{Sep}\nmore", width: 60);

        Click(pane, new Point(2, 1));

        Assert.Empty(requests);
    }

    [Fact]
    public void Click_OnASeparatorLineIsInertEvenIfItLooksLikeALink()
    {
        // BuildCells never runs link detection on a separator line, so a click there must never activate
        // either — otherwise a click could act on something the pane never rendered as a link. The real
        // rule holds no URL, so the invariant is pinned with a separator that does.
        var separator = $"-- {WebUrl} --";
        var (pane, requests) = ClickablePane($"body\n{separator}\nmore", width: 60, separator: separator);

        Click(pane, Locate(pane, WebUrl));

        Assert.Empty(requests);
    }

    [Fact]
    public void Click_BelowTheLastLine_ActivatesNothing()
    {
        // A tall pane over a short body whose last line *ends* with a link: the base view clamps a click
        // in the empty area onto the last line at the clicked column, which would land inside that URL.
        var (pane, requests) = ClickablePane($"ends with {WebUrl}", width: 60, height: 12);
        var link = Locate(pane, WebUrl);

        Click(pane, new Point(link.X + 2, link.Y + 5));

        Assert.Empty(requests);
    }

    [Fact]
    public void Click_RightOfAWrappedRowsText_ActivatesNothing()
    {
        // 30 columns wide, so "short " occupies its own row and the URL wraps below it. A click in the
        // blank space right of "short " clamps onto the row's end — the URL's first character.
        var (pane, requests) = ClickablePane($"short {TaskUrl} tail", width: 30);
        var row = Locate(pane, "short").Y;

        Click(pane, new Point(25, row));

        Assert.Empty(requests);
    }

    [Fact]
    public void Click_OnTheContinuationOfAWrappedLink_ResolvesTheWholeLink()
    {
        // At 30 columns the URL doesn't fit on the row it starts on, so its tail spills onto the next one.
        // Clicking that continuation must still resolve the whole span (this is the case a per-display-row
        // re-scan would get wrong: the row on its own holds only a fragment of the URL).
        var (pane, requests) = ClickablePane($"short {TaskUrl} tail", width: 30);
        var urlRow = Locate(pane, "https://app.clickup.com").Y;
        var continuation = Cell.ToString(pane.GetLine(urlRow + 1));
        Assert.DoesNotContain("https://", continuation, StringComparison.Ordinal);

        Click(pane, new Point(1, urlRow + 1));

        var request = Assert.Single(requests);
        Assert.Equal(TaskUrl, request.Url);
        Assert.Equal(LinkAction.OpenTaskDetail, request.Action);
    }

    [Fact]
    public void Click_AfterScrolling_ResolvesTheLinkUnderTheClick()
    {
        var body = string.Join(
            '\n', "filler one", "filler two", "filler three", $"link {WebUrl}", "after", "and after that");
        var (pane, requests) = ClickablePane(body, width: 60, height: 3);
        var link = Locate(pane, WebUrl);

        // Scroll down, then click the link where it now sits in the viewport. (Terminal.Gui clamps the
        // scroll offset to the content, so the row it lands on is read back rather than assumed.)
        pane.ScrollTo(new Point(0, link.Y));
        Assert.True(pane.Viewport.Y > 0, "the pane should have scrolled");
        Click(pane, new Point(link.X, link.Y - pane.Viewport.Y));

        var request = Assert.Single(requests);
        Assert.Equal(WebUrl, request.Url);
    }

    [Fact]
    public void Click_OnALinkPrecededByWideRunes_ResolvesByGrapheme()
    {
        // Terminal.Gui reports a click position as a cell index, and each emoji is one cell but two
        // UTF-16 chars — so a char-offset-for-cell-index mix-up would land two chars into the URL (or
        // miss it). Locate() addresses the URL by its real column, as a terminal would.
        var (pane, requests) = ClickablePane($"ab \U0001F600\U0001F600 {WebUrl} end", width: 60);

        Click(pane, Locate(pane, WebUrl));

        var request = Assert.Single(requests);
        Assert.Equal(WebUrl, request.Url);
    }

    [Fact]
    public void DoubleClick_AndTripleClick_DoNotActivate()
    {
        // Double-click is the base view's select-word gesture and each multi-click carries its own distinct
        // flag, so neither reaches the activation path; activation is a single click only.
        var (pane, requests) = ClickablePane($"See {WebUrl} now", width: 60);
        var at = Locate(pane, WebUrl);

        ClickWith(pane, at, MouseFlags.LeftButtonDoubleClicked);
        ClickWith(pane, at, MouseFlags.LeftButtonTripleClicked);

        Assert.Empty(requests);
    }

    [Theory]
    [InlineData(MouseFlags.Shift)]
    [InlineData(MouseFlags.Alt)]
    [InlineData(MouseFlags.Shift | MouseFlags.Alt)]
    public void Click_WithAnUnsupportedModifier_ActivatesNothing(MouseFlags modifier)
    {
        // Only a plain or Ctrl-modified click activates. This is a correctness guard, not a preference:
        // TextView maps positions for a *bare* left click only, and re-reports the stale caret for anything
        // else — so admitting a Shift/Alt+click would activate whichever link the caret last sat on. Here
        // the caret is parked in the web link first, so a stale read would resolve to it.
        var (pane, requests) = ClickablePane($"web {WebUrl}\ntask {TaskUrl}", width: 60);
        Click(pane, Locate(pane, WebUrl));
        Assert.Single(requests);

        ClickWith(pane, Locate(pane, TaskUrl), MouseFlags.LeftButtonClicked | modifier);

        Assert.Single(requests);   // still just the first, plain click
    }

    [Theory]
    [InlineData(false)]   // plain click first
    [InlineData(true)]    // Ctrl+click first — its caret nudge persists into the next click
    public void Click_OnTheLastColumnOfAFullWidthWrappedLinkRow_Activates(bool ctrlFirst)
    {
        // Handling a click can scroll Viewport.X to 1 — the base view keeping the caret visible when it
        // lands on a full-width row's last column — and it stays scrolled for later clicks. Letting that
        // into the width guard made this one cell the only cell of a link that a click ignored, in whichever
        // gesture came second. Both orders are pinned because the first fix (snapshotting the viewport)
        // still failed the ctrl-then-plain order.
        // One unbroken URL long enough to fill a 30-column row and spill onto the next, so row 0 has no
        // wrap slack and its last column is the cell in question.
        var body = WebUrl + WebUrl;
        var (pane, requests) = ClickablePane(body, width: 30);
        var lastColumn = pane.GetColumnsWidth(pane.GetLine(0)) - 1;
        Assert.Equal(30, lastColumn + 1);                     // the row really is full width
        var at = new Point(lastColumn, 0);

        Click(pane, at, ctrl: ctrlFirst);
        Click(pane, at, ctrl: !ctrlFirst);

        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Span, requests[1].Span);     // both gestures found the same span
        Assert.All(requests, r => Assert.Equal(body, r.Url));
    }

    [Fact]
    public void RepeatedIdenticalClicks_ActivateEveryTime()
    {
        // The position is read from a cached caret, and Terminal.Gui re-reports it on every click even when
        // it hasn't moved. If that ever changed, clicking the same link twice would silently go inert.
        var (pane, requests) = ClickablePane($"See {WebUrl} now", width: 60);
        var at = Locate(pane, WebUrl);

        Click(pane, at);
        Click(pane, at);
        Click(pane, at, ctrl: true);

        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal(WebUrl, r.Url));
    }

    [Fact]
    public void Click_AfterAReload_UsesTheNewBody()
    {
        // SetBody is called repeatedly (a refresh / an activity-order toggle re-renders in place), so the
        // lines a click hit-tests against must be the ones currently loaded — including when the new body
        // wraps differently from the old one.
        var (pane, requests) = ClickablePane($"first body with {WebUrl} in it", width: 30);
        pane.SetBody($"second body, wrapping too, with {TaskUrl} in it", Sep);
        Rewrap(pane);

        Click(pane, Locate(pane, "clickup.com"));

        var request = Assert.Single(requests);
        Assert.Equal(TaskUrl, request.Url);
        Assert.Equal(LinkAction.OpenTaskDetail, request.Action);
    }

    // ── Keyboard link focus traversal (#319) ─────────────────────────────────────────────────────────
    // The ordered per-pane link table and the focus-highlight tagging are pure (unit-tested directly); the
    // focus movement / scroll / activation drive a real, laid-out DetailPaneView through its public methods
    // — no Application / driver needed, exactly like the mouse suite above.

    [Fact]
    public void ExtractPaneLinks_ReturnsLinksInDocumentOrderAcrossLines()
    {
        var body = string.Join('\n', $"first {WebUrl} line", "no links here", $"then {TaskUrl} and {WebUrl}");
        var links = DetailPaneView.ExtractPaneLinks(body, Sep);

        Assert.Collection(links,
            l => { Assert.Equal(0, l.LineIndex); Assert.Equal(WebUrl, l.Span.Url); },
            l => { Assert.Equal(2, l.LineIndex); Assert.Equal(TaskUrl, l.Span.Url); Assert.Equal(LinkKind.Task, l.Span.Kind); },
            l => { Assert.Equal(2, l.LineIndex); Assert.Equal(WebUrl, l.Span.Url); });
    }

    [Fact]
    public void ExtractPaneLinks_SkipsSeparatorLinesEvenWhenTheyLookLikeLinks()
    {
        var separator = $"-- {WebUrl} --";
        var body = string.Join('\n', $"body {TaskUrl}", separator, "more");
        var links = DetailPaneView.ExtractPaneLinks(body, separator);

        var link = Assert.Single(links);
        Assert.Equal(TaskUrl, link.Span.Url);
    }

    [Fact]
    public void ExtractPaneLinks_EmptyWhenThereAreNoLinks()
        => Assert.Empty(DetailPaneView.ExtractPaneLinks("just some plain text\nover two lines", Sep));

    [Fact]
    public void BuildCells_TagsOnlyTheFocusedLinkAsFocused_LeavingOthersByKind()
    {
        const string line = $"task {TaskUrl} and web {WebUrl} end";
        var focused = new PaneLink(0, TaskLinkExtractor.Extract(line).Single(s => s.Url == WebUrl));

        // The focused (web) link's cells become FocusedLink; the task link keeps its kind; nothing else moves.
        Assert.Equal(WebUrl, FocusedText(DetailPaneView.BuildCells(line, Sep, focused)));
        Assert.Equal(TaskUrl, TaggedTextIn(DetailPaneView.BuildCells(line, Sep, focused), DetailPaneView.DetailCellStyle.TaskLink));
        // Without a focused link nothing is tagged FocusedLink (guards the default overload).
        Assert.Equal("", FocusedText(DetailPaneView.BuildCells(line, Sep)));
    }

    [Fact]
    public void StepLinkFocus_NoLinks_ReturnsFalseAndFocusesNothing()
    {
        var (pane, _) = ClickablePane("no links on this pane at all", width: 60);

        Assert.False(pane.StepLinkFocus(forward: true));
        Assert.Equal(LinkFocus.None, pane.FocusedLinkIndex);
        Assert.Equal(0, pane.LinkCount);
    }

    [Fact]
    public void StepLinkFocus_ForwardCyclesThroughEveryLinkAndWraps()
    {
        var (pane, _) = ClickablePane($"a {WebUrl} b {TaskUrl} c {WebUrl} d", width: 200);
        Assert.Equal(3, pane.LinkCount);

        Assert.True(pane.StepLinkFocus(forward: true));
        Assert.Equal(0, pane.FocusedLinkIndex);
        pane.StepLinkFocus(forward: true);
        Assert.Equal(1, pane.FocusedLinkIndex);
        pane.StepLinkFocus(forward: true);
        Assert.Equal(2, pane.FocusedLinkIndex);
        pane.StepLinkFocus(forward: true);
        Assert.Equal(0, pane.FocusedLinkIndex); // wrapped
    }

    [Fact]
    public void StepLinkFocus_BackwardFromNoneWrapsToTheLastLink()
    {
        var (pane, _) = ClickablePane($"a {WebUrl} b {TaskUrl} c", width: 200);

        Assert.True(pane.StepLinkFocus(forward: false));
        Assert.Equal(1, pane.FocusedLinkIndex); // last of two
    }

    [Fact]
    public void StepLinkFocus_HighlightsTheFocusedLinkAndOnlyIt()
    {
        var (pane, _) = ClickablePane($"a {WebUrl} b {TaskUrl} c", width: 200);

        pane.StepLinkFocus(forward: true);        // focus WebUrl (index 0)
        Assert.Equal(WebUrl, FocusedTextOf(pane));

        pane.StepLinkFocus(forward: true);        // focus TaskUrl (index 1) — the web link reverts to its kind
        Assert.Equal(TaskUrl, FocusedTextOf(pane));
    }

    [Fact]
    public void SetBody_ClearsAnyPriorLinkFocus()
    {
        var (pane, _) = ClickablePane($"first {WebUrl}", width: 60);
        pane.StepLinkFocus(forward: true);
        Assert.Equal(0, pane.FocusedLinkIndex);

        pane.SetBody($"second body {TaskUrl} and {WebUrl}", Sep);

        Assert.Equal(LinkFocus.None, pane.FocusedLinkIndex);
        Assert.Equal(2, pane.LinkCount);
        Assert.Equal("", FocusedTextOf(pane)); // no highlight until Tab is pressed again
    }

    [Fact]
    public void ActivateFocusedLink_WithNoFocus_ReturnsFalseAndRaisesNothing()
    {
        var (pane, requests) = ClickablePane($"has a {WebUrl}", width: 60);

        Assert.False(pane.ActivateFocusedLink());
        Assert.Empty(requests);
    }

    [Fact]
    public void ActivateFocusedLink_RaisesTheSameRequestAPlainClickWould()
    {
        var (pane, requests) = ClickablePane($"web {WebUrl} then task {TaskUrl}", width: 200);

        pane.StepLinkFocus(forward: true);        // WebUrl
        Assert.True(pane.ActivateFocusedLink());
        pane.StepLinkFocus(forward: true);        // TaskUrl
        Assert.True(pane.ActivateFocusedLink());

        Assert.Collection(requests,
            r => { Assert.Equal(WebUrl, r.Url); Assert.Equal(LinkAction.OpenInBrowser, r.Action); },
            r => { Assert.Equal(TaskUrl, r.Url); Assert.Equal(LinkAction.OpenTaskDetail, r.Action); Assert.Equal("abc123", r.Span.TaskId); });
    }

    [Fact]
    public void StepLinkFocus_ScrollsTowardAFocusedLinkBelowTheFold()
    {
        // A link far below a 3-row viewport. Focusing it scrolls the pane down toward it. (In CI the undrawn
        // model clamps the scroll extent conservatively, so this asserts the down-scroll fired rather than
        // pixel-exact end visibility — that's what tui-validate covers; the runtime clamp reveals the row.)
        var body = string.Join('\n', "one", "two", "three", "four", "five", "six", $"deep {WebUrl} here");
        var (pane, _) = ClickablePane(body, width: 60, height: 3);
        Assert.Equal(0, pane.Viewport.Y); // starts at the top

        pane.StepLinkFocus(forward: false);   // focus the last (only) link, far below the viewport

        Assert.Equal(6, FocusedRow(pane));    // the link is highlighted on its (far-down) row…
        Assert.True(pane.Viewport.Y > 0, "the pane should have scrolled down toward the focused link");
    }

    [Fact]
    public void StepLinkFocus_ScrollsBackUpToAFocusedLinkAboveTheViewport()
    {
        var body = string.Join('\n', $"top {WebUrl} here", "two", "three", "four", "five", "six", $"deep {TaskUrl} here");
        var (pane, _) = ClickablePane(body, width: 60, height: 3);

        pane.StepLinkFocus(forward: false);   // focus the bottom link → scrolls down
        Assert.True(pane.Viewport.Y > 0);

        pane.StepLinkFocus(forward: true);    // wraps to the top link → scrolls back up to it
        Assert.Equal(0, pane.FocusedLinkIndex);
        Assert.Equal(0, pane.Viewport.Y);
    }

    // The concatenated graphemes of every FocusedLink-tagged cell across a built body's lines.
    private static string FocusedText(List<List<Cell>> cells)
        => string.Concat(cells.SelectMany(line => line)
            .Where(c => DetailPaneView.ClassifyCell(c) == DetailPaneView.DetailCellStyle.FocusedLink)
            .Select(c => c.Grapheme ?? ""));

    // As above, but read from a laid-out pane's currently loaded (wrapped) lines.
    private static string FocusedTextOf(DetailPaneView pane)
        => string.Concat(pane.GetAllLines().SelectMany(line => line)
            .Where(c => DetailPaneView.ClassifyCell(c) == DetailPaneView.DetailCellStyle.FocusedLink)
            .Select(c => c.Grapheme ?? ""));

    // The first wrapped display row carrying a FocusedLink cell.
    private static int FocusedRow(DetailPaneView pane)
    {
        var lines = pane.GetAllLines();
        for (var row = 0; row < lines.Count; row++)
            if (lines[row].Any(c => DetailPaneView.ClassifyCell(c) == DetailPaneView.DetailCellStyle.FocusedLink))
                return row;
        throw new InvalidOperationException("no focused link is highlighted");
    }

    // The concatenated graphemes tagged with `style` across a built body's lines (multi-line TaggedText).
    private static string TaggedTextIn(List<List<Cell>> cells, DetailPaneView.DetailCellStyle style)
        => string.Concat(cells.SelectMany(line => line)
            .Where(c => DetailPaneView.ClassifyCell(c) == style)
            .Select(c => c.Grapheme ?? ""));

    // ── Mouse link hover feedback (#408) ─────────────────────────────────────────────────────────────
    // The same real, laid-out pane and NewMouseEvent entry point the click tests use, driving bare motion
    // reports (MouseFlags.PositionReport) instead of clicks. They pin the pure hover hit test — a report
    // over a link yields that link's resolved target, off a link yields null — and the dedup that fires
    // HoverTargetChanged only when the hovered link changes, never on a move within one link.

    // A laid-out pane whose HoverTargetChanged values are captured in order.
    private static (DetailPaneView Pane, List<string?> Targets) HoverablePane(
        string body, int width = 60, int height = 10, string separator = Sep)
    {
        var pane = new DetailPaneView { Frame = new Rectangle(0, 0, width, height) };
        pane.SetBody(body, separator);
        Rewrap(pane);
        var targets = new List<string?>();
        pane.HoverTargetChanged += (_, target) => targets.Add(target);
        return (pane, targets);
    }

    private static void Hover(DetailPaneView pane, Point at)
        => pane.NewMouseEvent(new Mouse { Position = at, Flags = MouseFlags.PositionReport });

    [Fact]
    public void Hover_OverATaskLink_RaisesItsResolvedTarget()
    {
        var (pane, targets) = HoverablePane($"Related: {TaskUrl} ok");

        Hover(pane, Locate(pane, TaskUrl));

        Assert.Equal(TaskUrl, Assert.Single(targets));
        // The pure hit test agrees with the event it raised.
        Assert.Equal(TaskUrl, pane.HoverLinkTargetAt(Locate(pane, TaskUrl), pane.Viewport));
    }

    [Fact]
    public void Hover_OverAWebLink_RaisesItsResolvedTarget()
    {
        var (pane, targets) = HoverablePane($"See {WebUrl} now");

        Hover(pane, Locate(pane, WebUrl));

        Assert.Equal(WebUrl, Assert.Single(targets));
    }

    [Fact]
    public void Hover_OverAMarkdownLink_RaisesItsDestinationBehindTheVisibleText()
    {
        // Hovering the *visible text* of a [text](url) link names its real destination (#430), the same
        // resolved target the OSC-8/underline path carries — not the prose the user sees.
        var (pane, targets) = HoverablePane($"See [the docs]({WebUrl}) now");

        Hover(pane, Locate(pane, "the docs"));

        Assert.Equal(WebUrl, Assert.Single(targets));
    }

    [Fact]
    public void Hover_OverProse_AfterALink_RaisesNull()
    {
        var (pane, targets) = HoverablePane($"Related: {TaskUrl} ok");

        Hover(pane, Locate(pane, TaskUrl));   // on the link → its target
        Hover(pane, new Point(0, 0));          // the 'R' of "Related:" — prose

        Assert.Equal([TaskUrl, null], targets);
    }

    [Fact]
    public void Hover_MovingWithinTheSameLink_RaisesOnlyOnce()
    {
        var (pane, targets) = HoverablePane($"Related: {TaskUrl} ok");
        var at = Locate(pane, TaskUrl);

        Hover(pane, at);                                    // enter the link
        Hover(pane, at with { X = at.X + 3 });              // still inside it
        Hover(pane, at with { X = at.X + 6 });              // still inside it

        Assert.Equal(TaskUrl, Assert.Single(targets));      // one enter event, no repeats
    }

    [Fact]
    public void Hover_ThenSetBodyReRender_ClearsTheStaleHint()
    {
        // A background refresh / activity-order toggle re-renders a pane in place (SetBody) while the
        // pointer rests on a link. The hint on the status line no longer describes the re-wrapped body, so
        // SetBody must raise a clear — and must not merely reset the dedup, which would swallow the next
        // move-off and strand the stale hint.
        var (pane, targets) = HoverablePane($"Related: {TaskUrl} ok");
        Hover(pane, Locate(pane, TaskUrl));
        Assert.Equal(TaskUrl, targets[^1]);          // hint shown

        pane.SetBody($"Related: {TaskUrl} ok", Sep);  // re-render in place
        Rewrap(pane);

        Assert.Equal([TaskUrl, null], targets);       // the stale hint was cleared, not stranded
    }

    [Fact]
    public void HoverLinkTargetAt_RightOfTheText_IsNotALink()
    {
        var (pane, _) = HoverablePane($"Related: {TaskUrl}");
        var linkRow = Locate(pane, TaskUrl).Y;
        var pastEnd = pane.GetColumnsWidth(pane.GetLine(linkRow));

        Assert.Null(pane.HoverLinkTargetAt(new Point(pastEnd, linkRow), pane.Viewport));
        Assert.Null(pane.HoverLinkTargetAt(new Point(-1, linkRow), pane.Viewport));
    }

    [Fact]
    public void HoverLinkTargetAt_BelowTheBody_IsNotALink()
    {
        var (pane, _) = HoverablePane($"Related: {TaskUrl}");

        // A row past the last wrapped line (the #318 "under a short body" clamp) resolves to no link.
        Assert.Null(pane.HoverLinkTargetAt(new Point(0, pane.Lines + 2), pane.Viewport));
    }

    [Fact]
    public void HoverLinkTargetAt_WithAWideRuneBeforeTheLink_MapsTheColumnToTheRightCell()
    {
        // An emoji is two columns wide, so a naive column==cell-index mapping would read the link one cell
        // early. The emoji's own column is not a link; the URL's column resolves to the URL.
        var (pane, _) = HoverablePane($"\U0001F600 {WebUrl}");

        Assert.Null(pane.HoverLinkTargetAt(new Point(0, 0), pane.Viewport));           // the emoji
        Assert.Equal(WebUrl, pane.HoverLinkTargetAt(Locate(pane, WebUrl), pane.Viewport));
    }
}
