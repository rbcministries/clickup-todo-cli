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
        string body, int width = 30, int height = 10)
    {
        var pane = new DetailPaneView { Frame = new Rectangle(0, 0, width, height) };
        pane.SetBody(body, Sep);
        // SetBody's TextView.Load leaves the model *unwrapped* until Terminal.Gui's own draw/viewport pass
        // re-wraps it — which always happens before a user can click, but never here, because a unit test
        // has no driver to draw with. Toggling WordWrap performs that wrap, so these clicks land on the
        // wrapped layout a user actually sees.
        pane.WordWrap = false;
        pane.WordWrap = true;
        var requests = new List<LinkActivationRequest>();
        pane.LinkActivationRequested += (_, request) => requests.Add(request);
        return (pane, requests);
    }

    private static void Click(DetailPaneView pane, Point at, bool ctrl = false)
        => pane.NewMouseEvent(new Mouse
        {
            Position = at,
            Flags = MouseFlags.LeftButtonClicked | (ctrl ? MouseFlags.Ctrl : MouseFlags.None),
        });

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
    public void DoubleClick_DoesNotActivate()
    {
        // A double-click is the base view's select-word gesture; activation is a single click only.
        var (pane, requests) = ClickablePane($"See {WebUrl} now", width: 60);
        var at = Locate(pane, WebUrl);

        pane.NewMouseEvent(new Mouse { Position = at, Flags = MouseFlags.LeftButtonDoubleClicked });

        Assert.Empty(requests);
    }

    [Fact]
    public void Click_AfterAReload_UsesTheNewBody()
    {
        // SetBody is called repeatedly (a refresh / an activity-order toggle re-renders in place), so the
        // lines a click hit-tests against must be the ones currently loaded.
        var (pane, requests) = ClickablePane($"first {WebUrl}", width: 60);
        pane.SetBody($"second {TaskUrl}", Sep);

        Click(pane, Locate(pane, TaskUrl));

        var request = Assert.Single(requests);
        Assert.Equal(TaskUrl, request.Url);
        Assert.Equal(LinkAction.OpenTaskDetail, request.Action);
    }
}
