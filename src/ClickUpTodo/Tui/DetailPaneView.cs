using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

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

    /// <summary>
    /// Raised when the user activates a link in this pane with the mouse (#318): a plain left click on a
    /// ClickUp task link asks for that task's Task Detail, any other click on a link (or any
    /// <c>Ctrl</c>+click) asks for the browser — see <see cref="LinkActivator.Resolve"/>. The host owns
    /// the destinations; the pane only reports what was clicked and what it means.
    /// </summary>
    public event EventHandler<LinkActivationRequest>? LinkActivationRequested;

    // The body exactly as SetBody loaded it, split on '\n' — one entry per *source* line, which is the
    // coordinate space Terminal.Gui reports a click in (see OnMouseEvent) and the one TaskLinkExtractor
    // offsets index into. Kept instead of a pre-extracted span table: a click re-extracts one short line,
    // so there is no per-render cache that could go stale against the loaded cells.
    private string[] _lines = [];

    // The separator passed to the last SetBody, so a click skips a rule line exactly as BuildCells does.
    private string _separator = "";

    // The caret in unwrapped model coordinates (X = cell index within the source line, Y = source line
    // index), as reported by Terminal.Gui while it handles a click. This is the wrapped→source mapping —
    // WordWrapManager, which owns it, is internal, and reproducing its wrap here would be drift.
    private Point? _unwrappedCaret;

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
        // Remember the body in source-line form for the click hit test (#318); BuildCells splits it the
        // same way, so the two can't disagree about what line N is.
        _lines = body.Split('\n');
        _separator = separator;
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
    /// Left-click activation of an in-pane link (#318). A click resolves to a <see cref="LinkSpan"/> and,
    /// when it lands on one, raises <see cref="LinkActivationRequested"/> with the action
    /// <see cref="LinkActivator.Resolve"/> chose for the gesture's modifiers; anything else — a click on
    /// ordinary text, a wheel, a drag, a double-click — falls through to the base
    /// <see cref="TextView"/> so its native caret / selection / scroll behaviour is untouched.
    /// <para>
    /// The position comes from Terminal.Gui itself rather than from a re-implementation of its word wrap:
    /// the base view maps a click to a text position, and <see cref="OnUnwrappedCursorPositionChanged"/>
    /// reports that position in <em>unwrapped</em> (source-line) coordinates, which already accounts for
    /// wrapping and for the pane's scroll offset. Two details of that base behaviour shape the code:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>It only maps unmodified clicks</b>, and reports the <em>stale</em> caret for the rest.
    /// <see cref="TextView"/>'s handler tests the flags for a bare
    /// <see cref="MouseFlags.LeftButtonClicked"/>, so a modified click leaves the caret where it was — and
    /// still re-raises <see cref="OnUnwrappedCursorPositionChanged"/> with that old position, so there is
    /// no "it declined to map this" signal to detect. A <c>Ctrl</c>+click therefore resolves its position
    /// by handing the base a synthesized plain click at the same point (the panes are read-only, so the
    /// caret move that entails is invisible, and it is what an unmodified click would have done anyway),
    /// an unmodified click is passed to the base and its own mapping read back, and <b>every other
    /// modifier is refused outright</b> — a <c>Shift</c>/<c>Alt</c>+click isn't an activation gesture, and
    /// admitting one would activate whatever link the caret last sat on. When #320 adds
    /// <c>Ctrl+Shift</c>+click it joins the resolved-by-synthesized-click arm, since it carries
    /// <c>Ctrl</c>.
    /// </description></item>
    /// <item><description>
    /// <b>It clamps a click outside the text onto the nearest position.</b> That turns two ordinary
    /// clicks on empty space into false hits — below a short body it clamps onto the last line at the
    /// clicked column, and right of a wrapped row's text it clamps onto the row's end, which for a line
    /// that continues past the wrap is the *next* character (probed: clicking right of a row showing
    /// <c>"short "</c> resolved onto the URL that follows it). Hence the two guards below; a click on the
    /// exclusive end of a span is separately not a hit (<see cref="LinkActivator.SpanAt"/>).
    /// </description></item>
    /// <item><description>
    /// <b>Handling a click can shift the viewport.</b> Keeping the caret visible sets <c>Viewport.X</c> to 1
    /// when the caret lands on the last column of a full-width wrapped row, and it stays there for
    /// subsequent clicks. The width guard therefore ignores <c>Viewport.X</c> altogether (the pane is
    /// word-wrapped, so content never scrolls horizontally), and the vertical guard reads the viewport
    /// <em>as it was when the user clicked</em>, captured before the base sees the event. Letting either
    /// leak in made the last cell of a full-width row the one cell of a link that a click ignored.
    /// </description></item>
    /// </list>
    /// </summary>
    protected override bool OnMouseEvent(Mouse mouseEvent)
    {
        // Only a plain or Ctrl-modified left click activates. Every other flag combination — a wheel, a
        // press/release (drag), a double- or triple-click (each its own distinct flag), and any
        // Shift/Alt-modified click — falls through to the base view untouched. Refusing the other
        // modifiers is what keeps a gesture the base won't map from resolving to the stale caret (see
        // above); it is not merely a taste call about which gestures mean "activate".
        if (!mouseEvent.Flags.HasFlag(MouseFlags.LeftButtonClicked)
            || mouseEvent.Flags.HasFlag(MouseFlags.Shift)
            || mouseEvent.Flags.HasFlag(MouseFlags.Alt)
            || mouseEvent.Position is not { } position)
            return base.OnMouseEvent(mouseEvent);

        // The viewport as the user saw it — the base may scroll it while handling the click (see above).
        var viewport = Viewport;

        // An unmodified click is the one the base view maps itself, so let it handle the real event and
        // read the position back; only a modified click needs the synthesized stand-in (see above), which
        // keeps the common gesture a single pass through the base.
        var ctrl = mouseEvent.Flags.HasFlag(MouseFlags.Ctrl);
        var handledByBase = !ctrl && base.OnMouseEvent(mouseEvent);
        if (LinkAt(position, viewport, resolvePosition: ctrl) is not { } span)
            return handledByBase;

        LinkActivationRequested?.Invoke(this, new LinkActivationRequest(span, LinkActivator.Resolve(span, ctrl)));
        mouseEvent.Handled = true;
        return true;
    }

    /// <summary>
    /// The link under a viewport-relative click <paramref name="position"/>, or <c>null</c> when the click
    /// isn't on one. Guards first against the two ways the base view clamps a click outside the text onto
    /// a position that would read as a hit (see <see cref="OnMouseEvent"/>), then resolves the source
    /// (line, cell) the click landed on and hit-tests that line's links.
    /// <paramref name="viewport"/> is the pane's viewport as it was when the user clicked (the base view
    /// may scroll it while handling the click). <paramref name="resolvePosition"/> asks the base view to
    /// map the position first — needed for a modified click, which it would otherwise not map at all.
    /// </summary>
    private LinkSpan? LinkAt(Point position, Rectangle viewport, bool resolvePosition)
    {
        // Guard 1 — a click below the last wrapped row (the empty area under a short body). Lines is the
        // wrapped line count while WordWrap is on, and viewport.Y is the topmost displayed wrapped row.
        // (GetLine clamps an out-of-range row to the last line, so without this the next guard would pass
        // and the click would resolve into whatever the body's last line ends with.)
        var displayRow = viewport.Y + position.Y;
        if (displayRow < 0 || displayRow >= Lines)
            return null;

        // Guard 2 — a click right of that row's rendered text. Measured in columns (GetColumnsWidth), so a
        // row carrying wide runes isn't cut short of its real width. Deliberately ignores viewport.X: the
        // pane is word-wrapped, so its content never scrolls horizontally and a click's column *is* its
        // column in the row. The only thing that moves viewport.X is the base view nudging it to keep the
        // caret visible when the caret lands on a full-width row's last column — and letting that leak in
        // here made the last cell of such a row the one cell of a link that a click ignored.
        if (position.X < 0 || position.X >= GetColumnsWidth(GetLine(displayRow)))
            return null;

        // The click's source (line, cell), from the base view's own mapping.
        if (resolvePosition)
            base.OnMouseEvent(new Mouse { Position = position, Flags = MouseFlags.LeftButtonClicked });
        if (_unwrappedCaret is not { } caret || caret.X < 0 || caret.Y < 0 || caret.Y >= _lines.Length)
            return null;

        // A separator rule is skipped exactly as BuildCells skips it, so a click can never activate a link
        // on a line the renderer never tagged as one. (For the actual rule — a run of '─' — this is a
        // no-op, since it holds no URL either way; it earns its keep only if a caller ever passes a
        // separator with text in it, which the test pins.)
        var line = _lines[caret.Y];
        if (line == _separator)
            return null;

        // Terminal.Gui reports the position as a *cell* index and a cell holds a whole grapheme cluster,
        // so convert to the UTF-16 char offset LinkSpan uses — via the same Cell.ToCellList segmentation
        // BuildCells tags with, which is byte-for-byte the segmentation TextView's own model uses.
        var graphemes = Cell.ToCellList(line, null).Select(c => c.Grapheme).ToArray();
        var offset = LinkActivator.CharOffsetAtCell(graphemes, caret.X);
        return LinkActivator.SpanAt(TaskLinkExtractor.Extract(line), offset);
    }

    /// <inheritdoc/>
    protected override void OnUnwrappedCursorPositionChanged(Point newUnwrappedCursorPosition)
    {
        // (column, row) in the unwrapped model = (cell index within the source line, source line index).
        _unwrappedCaret = newUnwrappedCursorPosition;
        base.OnUnwrappedCursorPositionChanged(newUnwrappedCursorPosition);
    }

    /// <inheritdoc/>
    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
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
}
