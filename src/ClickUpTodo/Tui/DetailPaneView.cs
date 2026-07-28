using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

// TextView is marked obsolete in Terminal.Gui 2.4 in favour of a not-yet-shipped EditorView; it
// remains the supported v2 read-only text pane the detail view uses (see TaskDetailScreen).
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// The read-only, word-wrapped text pane used by the task detail Stream / Comments / Description tabs.
/// Identical to a stock <see cref="TextView"/> except that the horizontal-rule lines that separate
/// adjacent blocks (<see cref="TaskDetailFormatter.CommentSeparator"/>) are drawn on the terminal's
/// <em>default</em> background instead of the pane's normal (grey) read-only fill, so a break between
/// comments reads as a clear gap rather than blending into the surrounding text.
/// <para>
/// The mechanism: each line of the body is loaded as its own list of <see cref="Cell"/>s, and every
/// cell of a separator line is tagged with <see cref="SeparatorMarker"/> — an attribute whose
/// background is <see cref="Color.None"/> (alpha 0). At draw time <see cref="OnDrawReadOnlyColor"/>
/// repaints those cells keeping the pane's read-only foreground (so the rule glyphs stay legible) but
/// with a <see cref="Color.None"/> background. The driver turns a <see cref="Color.None"/> background
/// into the ANSI "reset background" sequence (CSI 49m), so the terminal's own default background shows
/// through — on Windows Terminal that includes any acrylic/background-image the profile sets.
/// </para>
/// <para>
/// Only the separator glyph cells are recoloured; the rest of a separator row (the padding out to the
/// pane's right edge) keeps the normal read-only fill, exactly as the fixed-width rule already did.
/// The <see cref="BuildCells"/> line classification is pure, so it is unit-tested; the draw override is
/// the (CI-untestable) Terminal.Gui glue.
/// </para>
/// <para>
/// The same draw override also emits **OSC-8 terminal hyperlinks** (#380): for each bare link cell it sets
/// <see cref="IDriver.CurrentUrl"/> to the link's URL (via the pure <see cref="LinkUrlForCell"/>) so the ANSI
/// output wraps the run in an <c>ESC ] 8 ; ; URL ST … ESC ] 8 ; ; ST</c> escape, letting a supporting terminal
/// open the link natively. This is purely additive to the #317 styling — the visible colours/underline are
/// unchanged — and degrades to nothing where the driver/terminal doesn't support it.
/// </para>
/// </summary>
public sealed class DetailPaneView : TextView
{
    /// <summary>
    /// The per-cell tag applied to every cell of a separator line. Its <see cref="Attribute.Background"/>
    /// is <see cref="Color.None"/> — both a rendering intent (terminal-default background) and the marker
    /// <see cref="OnDrawReadOnlyColor"/> keys off. The foreground is irrelevant (the draw override
    /// substitutes the live read-only foreground), so it too is left as the default sentinel.
    /// </summary>
    public static readonly Attribute SeparatorMarker = new(Color.None, Color.None);

    /// <summary>
    /// Tag applied to the cells of a detected ClickUp <b>task</b> link (#317). A pure sentinel: its
    /// concrete colours are placeholders — <see cref="OnDrawReadOnlyColor"/> re-resolves the real,
    /// theme-aware attribute (the live read-only foreground + an underline) at draw time. Its background
    /// is deliberately <em>opaque</em> (not <see cref="Color.None"/>) so it never trips the separator
    /// branch, and it differs from <see cref="WebLinkMarker"/> so the draw path can tell the two apart.
    /// </summary>
    public static readonly Attribute TaskLinkMarker =
        new(new Color(ColorName16.Gray), new Color(ColorName16.Black), TextStyle.Underline);

    /// <summary>
    /// Tag applied to the cells of a detected <b>other web</b> link (#317). Like <see cref="TaskLinkMarker"/>
    /// a pure sentinel re-resolved at draw time — to a blue foreground + underline (how a terminal renders a
    /// bare URL). Opaque background, distinct from the task tag and the separator tag.
    /// </summary>
    public static readonly Attribute WebLinkMarker =
        new(WebLinkForeground, new Color(ColorName16.Black), TextStyle.Underline);

    /// <summary>The foreground a web link is drawn in (blue), applied by <see cref="OnDrawReadOnlyColor"/>
    /// over the pane's live read-only background so the link sits in the pane like surrounding text.</summary>
    private static readonly Color WebLinkForeground = new(ColorName16.BrightBlue);

    /// <summary>How <see cref="BuildCells"/> classified a cell — the pure tagging the draw override acts on,
    /// surfaced so the (Terminal.Gui-free) link/separator tagging is unit-tested end to end.</summary>
    public enum DetailCellStyle
    {
        /// <summary>Ordinary body text — no tag; drawn in the pane's read-only colour.</summary>
        Normal,

        /// <summary>A comment/block separator rule (<see cref="SeparatorMarker"/>).</summary>
        Separator,

        /// <summary>Part of a ClickUp task link (<see cref="TaskLinkMarker"/>).</summary>
        TaskLink,

        /// <summary>Part of an other-web link (<see cref="WebLinkMarker"/>).</summary>
        WebLink,
    }

    /// <summary>Classifies a loaded <paramref name="cell"/> by the tag <see cref="BuildCells"/> applied —
    /// the single mapping both the draw override and the unit tests read, so they can't drift.</summary>
    public static DetailCellStyle ClassifyCell(Cell cell)
    {
        if (cell.Attribute is not { } a)
            return DetailCellStyle.Normal;
        if (a.Background == Color.None)
            return DetailCellStyle.Separator;
        if (a.Equals(TaskLinkMarker))
            return DetailCellStyle.TaskLink;
        if (a.Equals(WebLinkMarker))
            return DetailCellStyle.WebLink;
        return DetailCellStyle.Normal;
    }

    /// <summary>
    /// The OSC-8 hyperlink target (#380) for the link run covering cell <paramref name="idxCol"/> of the
    /// laid-out (display) <paramref name="line"/>, or <see langword="null"/> when that cell is not part of a
    /// link whose on-screen text is itself a navigable <c>http(s)</c> URL. Pure — no Terminal.Gui draw surface —
    /// so it is unit-tested; the draw override (<see cref="OnDrawReadOnlyColor"/>) feeds the result to
    /// <see cref="IDriver.CurrentUrl"/> so the ANSI output wraps the run in an OSC-8 escape.
    /// <para>
    /// The emitted target is <b>always exactly the run's on-screen text</b>, returned only when that text
    /// re-parses as an absolute <c>http(s)</c> URL with a real host. For a <b>bare</b> link that text <em>is</em>
    /// the URL (the case #317 renders and #380 validates), so the target is exact. For a <b>markdown</b>
    /// <c>[text](url)</c> link — whose true target (<see cref="LinkSpan.Url"/>) can differ from the visible
    /// text — this returns <see langword="null"/> when the visible text is prose, and returns the <em>visible</em>
    /// URL (not the markdown target) in the rare case the visible text is itself an <c>http(s)</c> URL. That
    /// deviation is deliberate and bounded: the target then equals what the reader sees on screen (never a
    /// hidden destination), and correct markdown-target OSC-8 — which needs the resolved span threaded into the
    /// draw path — is deferred (#430; see the plan doc). A word-wrapped link's non-URL tail fragment likewise
    /// fails the URL check; wrapped-link rendering is tracked separately (#413).
    /// </para>
    /// </summary>
    public static string? LinkUrlForCell(IReadOnlyList<Cell> line, int idxCol)
    {
        if (idxCol < 0 || idxCol >= line.Count)
            return null;

        var kind = ClassifyCell(line[idxCol]);
        if (kind is not (DetailCellStyle.TaskLink or DetailCellStyle.WebLink))
            return null;

        // Expand over the maximal contiguous run of same-kind link cells around idxCol. Runs are naturally
        // separated by the untagged cells BuildCells leaves between links (a link is bounded by whitespace),
        // so a run is exactly one link.
        var start = idxCol;
        while (start > 0 && ClassifyCell(line[start - 1]) == kind)
            start--;
        var end = idxCol;
        while (end + 1 < line.Count && ClassifyCell(line[end + 1]) == kind)
            end++;

        var text = string.Concat(Enumerable.Range(start, end - start + 1).Select(i => line[i].Grapheme ?? ""));

        // Emit only when the run's on-screen text is itself a navigable absolute http(s) URL, and use that
        // text as the target. A bare link passes (its text is the URL). A markdown link's visible prose, or a
        // wrapped link's non-URL tail fragment, does not — so no wrong target is invented. (A markdown link
        // whose *visible text* is itself a URL yields that displayed URL, not its true target — the bounded,
        // never-hidden-destination deviation documented above; correct markdown targets are deferred to #430.)
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
                ? text
                : null;
    }

    public DetailPaneView()
    {
        ReadOnly = true;
        WordWrap = true;
    }

    /// <summary>Loads <paramref name="body"/> into the pane, tagging every line equal to
    /// <paramref name="separator"/> so it is drawn on the terminal-default background. Safe to call
    /// repeatedly (e.g. an activity-order toggle re-renders in place).</summary>
    public void SetBody(string body, string separator)
    {
        // Home the caret before re-loading. Terminal.Gui 2.4.10's TextView.Load raises OnContentsChanged
        // (via its history-clear) with InheritsPreviousAttribute already turned on but *before* it resets
        // the caret, so it runs ProcessInheritsPreviousScheme against the stale CurrentRow/CurrentColumn
        // from the previous body. When the previous body was longer (e.g. the pane had been MoveEnd()-ed
        // to its bottom for auto-scroll, #107) that caret indexes past the shorter new content and throws
        // ArgumentOutOfRangeException — the crash a Ctrl+PgUp/PgDn re-render hit on a pane that isn't the
        // front-most tab. Homing the caret first keeps the (row,col) it reads at (0,0), always in range.
        MoveHome();
        Load(BuildCells(body, separator));
        // Load() enables attribute inheritance (a null-attribute cell would copy the previous cell's
        // colour). We tag whole separator lines explicitly and want every other cell to fall back to
        // the read-only role, so keep inheritance off. (Moot while ReadOnly, but explicit.)
        InheritsPreviousAttribute = false;
    }

    /// <summary>
    /// Splits <paramref name="body"/> into one <see cref="Cell"/> list per line, tagging the cells of
    /// any line that exactly equals <paramref name="separator"/> with <see cref="SeparatorMarker"/>,
    /// tagging the cells covered by each detected link (via <see cref="TaskLinkExtractor"/>) with
    /// <see cref="TaskLinkMarker"/> / <see cref="WebLinkMarker"/> by kind (#317), and leaving every other
    /// cell's attribute null (so it draws in the pane's normal read-only colour). Pure — no Terminal.Gui
    /// draw surface — so the separator and link classification are unit-tested.
    /// </summary>
    public static List<List<Cell>> BuildCells(string body, string separator)
    {
        var lines = body.Split('\n');
        var cells = new List<List<Cell>>(lines.Length);
        foreach (var line in lines)
        {
            if (line == separator)
            {
                cells.Add(Cell.ToCellList(line, SeparatorMarker));
                continue;
            }

            // Links never span a newline (the URL regex stops at whitespace), so extracting per line
            // yields offsets that are char indices into this exact line — no global→(line,col) mapping.
            var links = TaskLinkExtractor.Extract(line);
            cells.Add(links.Count == 0 ? Cell.ToCellList(line, null) : BuildLineWithLinks(line, links));
        }
        return cells;
    }

    /// <summary>
    /// Builds one line's cells with each <paramref name="links"/> span tagged by its kind. The line is
    /// assembled from char-offset segments — untagged run, tagged link, untagged run, … — so a link's cells
    /// carry <see cref="TaskLinkMarker"/> / <see cref="WebLinkMarker"/> while everything else stays null.
    /// Segment boundaries fall on link edges (whitespace/URL characters), never inside a grapheme cluster,
    /// so slicing by UTF-16 char index is safe. <paramref name="links"/> is in document order and
    /// non-overlapping (as <see cref="TaskLinkExtractor.Extract"/> guarantees).
    /// </summary>
    private static List<Cell> BuildLineWithLinks(string line, IReadOnlyList<LinkSpan> links)
    {
        var result = new List<Cell>(line.Length);
        var pos = 0;
        foreach (var span in links)
        {
            if (span.Start > pos)
                result.AddRange(Cell.ToCellList(line[pos..span.Start], null));
            var marker = span.Kind == LinkKind.Task ? TaskLinkMarker : WebLinkMarker;
            result.AddRange(Cell.ToCellList(line[span.Start..span.End], marker));
            pos = span.End;
        }
        if (pos < line.Length)
            result.AddRange(Cell.ToCellList(line[pos..], null));
        return result;
    }

    /// <inheritdoc/>
    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        // OSC-8 hyperlink (#380): tag the cell about to be drawn with its link's URL (or clear it for a
        // non-link cell) so the subsequent AddRune associates it and the ANSI output wraps the run in an
        // OSC-8 escape. Additive to #317's styling below; parallel to how SetAttribute drives CurrentAttribute.
        SetCurrentUrl(LinkUrlForCell(line, idxCol));

        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attr)
        {
            if (attr.Background == Color.None)
            {
                // A separator cell: keep the pane's read-only foreground for the rule glyph, but drop the
                // background to Color.None so the driver emits CSI 49m and the terminal's own default /
                // transparent background shows through instead of the grey read-only fill.
                var readOnly = GetAttributeForRole(VisualRole.ReadOnly);
                SetAttribute(new Attribute(readOnly.Foreground, Color.None, readOnly.Style));
                return;
            }

            // A link cell (#317): keep the pane's live read-only background so the link sits in the pane
            // like surrounding text, but recolour the foreground (blue for a web link, the read-only
            // foreground for a task link) and add an underline. Re-resolving from the live role keeps the
            // link theme-aware; the tag itself only carries which kind it is.
            if (attr.Equals(TaskLinkMarker) || attr.Equals(WebLinkMarker))
            {
                var readOnly = GetAttributeForRole(VisualRole.ReadOnly);
                var foreground = attr.Equals(WebLinkMarker) ? WebLinkForeground : readOnly.Foreground;
                SetAttribute(new Attribute(foreground, readOnly.Background, readOnly.Style | TextStyle.Underline));
                return;
            }
        }

        base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }

    /// <inheritdoc/>
    protected override void OnDrawComplete(DrawContext? context)
    {
        // Clear any URL left active by the pane's last drawn cell (#380) so a link that ends the pane can't
        // leak its OSC-8 target onto a sibling view drawn later in the same frame.
        SetCurrentUrl(null);
        base.OnDrawComplete(context);
    }

    /// <summary>Sets the driver's active OSC-8 URL (or clears it with <see langword="null"/>). No-op when
    /// there is no driver (e.g. a headless unit test loading the model without a running application).</summary>
    private static void SetCurrentUrl(string? url)
    {
        if (Application.Driver is { } driver)
            driver.CurrentUrl = url;
    }
}
