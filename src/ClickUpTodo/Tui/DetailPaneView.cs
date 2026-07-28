using System.Text;
using Terminal.Gui.Drawing;
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

    /// <summary>
    /// Recomputes, for a single rendered (possibly word-wrapped) row, which cells belong to a task/web
    /// link — from the row's <em>own</em> graphemes rather than the per-cell tags <see cref="BuildCells"/>
    /// applied to the source line. Terminal.Gui 2.4.10's word wrap rebuilds a wrapped row's graphemes from
    /// the wrap offset but its per-cell attributes from index 0 of the source line, so the link tags land
    /// on the wrong cells of every row after the first and the underline is drawn in the wrong columns
    /// (#413). Re-extracting from the row text is offset-free: a URL contains no whitespace and word wrap
    /// breaks on whitespace, so a URL that fits the pane width sits wholly on one wrapped row and yields
    /// row-local offsets with no wrap-offset arithmetic. Returns one <see cref="DetailCellStyle"/> per cell
    /// (only <see cref="DetailCellStyle.TaskLink"/> / <see cref="DetailCellStyle.WebLink"/> /
    /// <see cref="DetailCellStyle.Normal"/> — separators are handled from the tag, which the wrap bug
    /// leaves correct because a separator row is tagged uniformly). Pure and Terminal.Gui-draw-free so it
    /// is unit-tested.
    /// </summary>
    public static DetailCellStyle[] ClassifyRowLinkCells(IReadOnlyList<Cell> row)
    {
        var styles = new DetailCellStyle[row.Count];
        if (row.Count == 0)
            return styles;

        // Reconstruct the row's text and each cell's start char offset. A cell is one grapheme, which can
        // be more than one UTF-16 char (e.g. an emoji), so map by accumulated grapheme length — not cell
        // index — to stay aligned with the char offsets TaskLinkExtractor returns.
        var text = new StringBuilder(row.Count);
        var offsets = new int[row.Count];
        for (var i = 0; i < row.Count; i++)
        {
            offsets[i] = text.Length;
            text.Append(row[i].Grapheme ?? string.Empty);
        }

        var rendered = text.ToString();
        // Cheap bail-out: every link (bare or markdown) carries an http(s) scheme, so a row without one
        // has no links and skips the regex entirely — the common case for body text.
        if (rendered.IndexOf("http", StringComparison.OrdinalIgnoreCase) < 0)
            return styles;

        var links = TaskLinkExtractor.Extract(rendered);
        if (links.Count == 0)
            return styles;

        // Links are in document order and non-overlapping; walk cells and links together in one pass.
        var li = 0;
        for (var i = 0; i < row.Count; i++)
        {
            var off = offsets[i];
            while (li < links.Count && off >= links[li].End)
                li++;
            if (li >= links.Count)
                break;
            if (off >= links[li].Start && off < links[li].End)
                styles[i] = links[li].Kind == LinkKind.Task ? DetailCellStyle.TaskLink : DetailCellStyle.WebLink;
        }

        return styles;
    }

    // Per-row cache for the draw path: OnDrawReadOnlyColor is invoked once per cell, but the row's link
    // classification only needs computing once per row. Keyed on the row list reference (Terminal.Gui's
    // wrap model holds a distinct list per rendered row), so a new row triggers a recompute.
    private List<Cell>? _linkRow;
    private DetailCellStyle[]? _linkRowStyles;

    private DetailCellStyle LinkStyleAt(List<Cell> line, int idxCol)
    {
        if (!ReferenceEquals(_linkRow, line) || _linkRowStyles is null || _linkRowStyles.Length != line.Count)
        {
            _linkRowStyles = ClassifyRowLinkCells(line);
            _linkRow = line;
        }

        return idxCol < _linkRowStyles.Length ? _linkRowStyles[idxCol] : DetailCellStyle.Normal;
    }

    /// <inheritdoc/>
    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count)
        {
            // A separator cell: keep the pane's read-only foreground for the rule glyph, but drop the
            // background to Color.None so the driver emits CSI 49m and the terminal's own default /
            // transparent background shows through instead of the grey read-only fill. The separator tag
            // survives word wrap (a separator row is tagged uniformly), so it is read straight off the cell.
            if (line[idxCol].Attribute is { } attr && attr.Background == Color.None)
            {
                var readOnly = GetAttributeForRole(VisualRole.ReadOnly);
                SetAttribute(new Attribute(readOnly.Foreground, Color.None, readOnly.Style));
                return;
            }

            // A link cell (#317): keep the pane's live read-only background so the link sits in the pane
            // like surrounding text, but recolour the foreground (blue for a web link, the read-only
            // foreground for a task link) and add an underline. The kind is recomputed from the row's own
            // graphemes (#413) rather than the per-cell tag, which word wrap misaligns on continuation
            // rows; re-resolving from the live role keeps the link theme-aware.
            var style = LinkStyleAt(line, idxCol);
            if (style is DetailCellStyle.TaskLink or DetailCellStyle.WebLink)
            {
                var readOnly = GetAttributeForRole(VisualRole.ReadOnly);
                var foreground = style == DetailCellStyle.WebLink ? WebLinkForeground : readOnly.Foreground;
                SetAttribute(new Attribute(foreground, readOnly.Background, readOnly.Style | TextStyle.Underline));
                return;
            }
        }

        base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }
}
