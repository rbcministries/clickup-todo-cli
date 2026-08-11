using System.Text;
using ClickUpTodo.Configuration;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
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

    /// <summary>
    /// Tag applied to the cells of the <b>keyboard-focused</b> link (#319) — the one <c>Tab</c>/<c>Shift+Tab</c>
    /// last stepped to. A pure sentinel like the kind markers: <see cref="OnDrawReadOnlyColor"/> re-resolves
    /// the real attribute (the theme's <see cref="VisualRole.Focus"/> + an underline) at draw time. Its
    /// colours differ from both link kind markers (so the draw path can tell "focused" from "task"/"web")
    /// and its background is opaque (so it never trips the separator branch). Because #317 underlines
    /// <em>every</em> link, the focus indicator has to be additional emphasis (a focus/reverse fill), not the
    /// underline itself.
    /// </summary>
    public static readonly Attribute FocusedLinkMarker =
        new(new Color(ColorName16.Black), new Color(ColorName16.Gray), TextStyle.Underline);

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

        /// <summary>Part of the keyboard-focused link (<see cref="FocusedLinkMarker"/>, #319).</summary>
        FocusedLink,
    }

    /// <summary>Classifies a loaded <paramref name="cell"/> by the tag <see cref="BuildCells"/> applied —
    /// the single mapping both the draw override and the unit tests read, so they can't drift.</summary>
    public static DetailCellStyle ClassifyCell(Cell cell)
    {
        if (cell.Attribute is not { } a)
            return DetailCellStyle.Normal;
        if (a.Background == Color.None)
            return DetailCellStyle.Separator;
        if (a.Equals(FocusedLinkMarker))
            return DetailCellStyle.FocusedLink;
        if (a.Equals(TaskLinkMarker))
            return DetailCellStyle.TaskLink;
        if (a.Equals(WebLinkMarker))
            return DetailCellStyle.WebLink;
        return DetailCellStyle.Normal;
    }

    /// <summary>
    /// The OSC-8 hyperlink target (#380/#430) for the cell at <paramref name="idxCol"/> of the laid-out
    /// (display) <paramref name="line"/>, or <see langword="null"/> when that cell is not part of a link.
    /// The target is the link's <b>resolved</b> <see cref="LinkSpan.Url"/> — for a <b>bare</b> link that is
    /// the URL itself; for a <b>markdown</b> <c>[text](url)</c> link it is the true destination behind the
    /// visible text (#430), recovered by re-extracting the row's own text (see <see cref="RowLinkUrls"/>),
    /// not reconstructed from the drawn cells. Pure — no Terminal.Gui draw surface — so it is unit-tested;
    /// the draw override (<see cref="OnDrawReadOnlyColor"/>) feeds the result to <see cref="IDriver.CurrentUrl"/>
    /// so the ANSI output wraps the run in an OSC-8 escape.
    /// <para>
    /// When a markdown link's <c>[text]</c> and <c>(url)</c> wrap onto <em>different</em> rendered rows, the
    /// visible-text fragment yields <see langword="null"/> (its row holds no complete markdown link) and the
    /// trailing <c>(url)</c> fragment re-extracts as an ordinary <em>bare</em> link to that URL itself — so a
    /// split markdown link never produces a <em>wrong</em> target, only a less-rich one (the visible text is
    /// left unlinked). Hyperlinking the visible text across the wrap needs the display→source mapping and is
    /// tracked with #443, together with the pre-existing edge where a bare URL sitting inside the visible
    /// <c>[text]</c> is itself extracted when the link wraps.
    /// </para>
    /// </summary>
    public static string? LinkUrlForCell(IReadOnlyList<Cell> line, int idxCol)
    {
        if (idxCol < 0 || idxCol >= line.Count)
            return null;

        return RowLinkUrls(line)[idxCol];
    }

    /// <summary>
    /// The OSC-8 hyperlink target for every cell of a single rendered (possibly word-wrapped) row: one entry
    /// per cell holding the resolved <see cref="LinkSpan.Url"/> of the link that covers it, or
    /// <see langword="null"/> for a non-link cell. Derived — like <see cref="ClassifyRowLinkCells"/>, and
    /// from the same single per-row re-extraction (<see cref="ClassifyRow"/>) — from the row's <em>own</em>
    /// graphemes rather than the per-cell tags <see cref="BuildCells"/> applied, so it is offset-correct on
    /// wrapped continuation rows (#413) and, because it consults the extracted <see cref="LinkSpan"/> rather
    /// than the drawn text, carries a markdown link's <b>resolved</b> target on its visible-text cells (#430).
    /// Pure and Terminal.Gui-draw-free, so it is unit-tested.
    /// </summary>
    public static string?[] RowLinkUrls(IReadOnlyList<Cell> row) => ClassifyRow(row).Urls;

    /// <summary>
    /// Raised when the user activates a link in this pane with the mouse (#318): a plain left click on a
    /// ClickUp task link asks for that task's Task Detail; a <c>Ctrl</c>+click (or <c>Ctrl+Shift</c>+click,
    /// #320) on a task link asks for the configured <see cref="TaskLinkCtrlClickDestination"/> (browser or
    /// a new terminal tab, with Shift inverting); any click on a web link asks for the browser — see
    /// <see cref="LinkActivator.Resolve"/>. The host owns the destinations; the pane only reports what was
    /// clicked and what it means.
    /// </summary>
    public event EventHandler<LinkActivationRequest>? LinkActivationRequested;

    /// <summary>
    /// Raised when the link the mouse is hovering over changes (#408): the argument is that link's
    /// <b>resolved</b> target URL (a bare link's own URL; a markdown link's destination behind its visible
    /// text), or <see langword="null"/> when the pointer moved off every link (onto prose, empty space, or
    /// out of the pane). Fires <b>only on a change</b> — a move within one link raises nothing — so the
    /// hint surface it drives repaints only when crossing a link boundary, never on every motion report.
    /// The screen turns this into a status-line hint; the pane never restyles a cell for hover.
    /// </summary>
    public event EventHandler<string?>? HoverTargetChanged;

    /// <summary>
    /// Where a <c>Ctrl</c>+click on a <b>task</b> link goes (#320); <c>Ctrl+Shift</c>+click does the
    /// other one. The screen sets this from the persisted <see cref="DetailViewSettings.TaskLinkCtrlClick"/>.
    /// Default <see cref="TaskLinkCtrlClickDestination.Browser"/> — the fixed behaviour #318 shipped, so a
    /// pane that is never told otherwise behaves exactly as before. Web links ignore it entirely.
    /// </summary>
    public TaskLinkCtrlClickDestination TaskLinkCtrlClickDestination { get; set; } = TaskLinkCtrlClickDestination.Browser;

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

    // The body exactly as SetBody loaded it, retained so a focus change (#319) can re-tag one link and
    // reload without the caller re-supplying it.
    private string _body = "";

    // The last hover target raised through HoverTargetChanged (#408), so a motion report that stays on the
    // same link (or off every link) raises nothing — the dedup that keeps hover off the per-keypress cost.
    // Reset by SetBody: a new body re-wraps, so the previous target no longer describes what is on screen.
    private string? _lastHoverTarget;

    // Every clickable link in the current body, in document order — the ordered set Tab/Shift+Tab step
    // through (#319). Rebuilt on each SetBody; the mouse path (#318) still re-extracts per clicked line,
    // so this table is only the keyboard traversal order, never the click hit test.
    private IReadOnlyList<PaneLink> _paneLinks = [];

    // The index into _paneLinks of the keyboard-focused link, or LinkFocus.None when nothing is focused
    // (the state on entry and after any re-render). Drives which link BuildCells tags with FocusedLinkMarker.
    private int _focusedLinkIndex = LinkFocus.None;

    public DetailPaneView()
    {
        ReadOnly = true;
        WordWrap = true;
        // Opt into bare motion reports (#408) so OnMouseEvent sees MouseFlags.PositionReport events and can
        // name the hovered link on the status line. The ansi driver already enables any-motion tracking
        // (?1003h) at boot, so this consumes events already arriving — it does not add terminal traffic.
        MousePositionTracking = true;
    }

    /// <summary>The number of clickable links in the current body (the count Tab/Shift+Tab cycle through).</summary>
    public int LinkCount => _paneLinks.Count;

    /// <summary>The index of the keyboard-focused link, or <see cref="LinkFocus.None"/> when none is focused.
    /// Surfaced for the (driver-free) focus-traversal tests.</summary>
    public int FocusedLinkIndex => _focusedLinkIndex;

    /// <summary>Loads <paramref name="body"/> into the pane, tagging every line equal to
    /// <paramref name="separator"/> so it is drawn on the terminal-default background. Safe to call
    /// repeatedly (e.g. an activity-order toggle re-renders in place).</summary>
    public void SetBody(string body, string separator)
    {
        // Remember the body in source-line form for the click hit test (#318); BuildCells splits it the
        // same way, so the two can't disagree about what line N is.
        _lines = body.Split('\n');
        _separator = separator;
        // A re-render (refresh / activity-order toggle) invalidates any keyboard link focus (#319): the
        // links may have moved, so drop the focus and rebuild the ordered link table for the new body.
        _body = body;
        _paneLinks = ExtractPaneLinks(body, separator);
        _focusedLinkIndex = LinkFocus.None;
        // Clear any hover hint (#408): the re-wrap can move or remove every link, so a hint already on the
        // status line no longer describes the screen. Route through UpdateHoverTarget so it (a) clears the
        // footer now via HoverTargetChanged and (b) keeps the dedup coherent — a bare reset to null would
        // make the *next* move-off match the remembered value and get swallowed, stranding the stale hint.
        // A no-op when nothing was hovered (the common refresh case) and before any subscriber attaches.
        UpdateHoverTarget(null);
        // A new body re-wraps into fresh row lists, so the reference-keyed source map (#443) is stale — drop
        // it so it doesn't retain the previous body's rows (the draw path rebuilds it on the next miss).
        _rowSourceMap = null;
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

    // ── Keyboard link focus traversal (#319, E) ─────────────────────────────────────────────────────

    /// <summary>
    /// Advances (<paramref name="forward"/>) or retreats the keyboard link focus to the next/previous
    /// clickable link, wrapping at the ends (<see cref="LinkFocus.Step"/>), re-drawing the focus highlight
    /// and scrolling the pane so the focused link stays visible. Returns <c>true</c> when it moved focus —
    /// i.e. the pane has at least one link, so the caller consumes the <c>Tab</c> — and <c>false</c> when
    /// the pane has no links, so <c>Tab</c> falls through to its default behaviour (#319 acceptance).
    /// </summary>
    public bool StepLinkFocus(bool forward)
    {
        if (_paneLinks.Count == 0)
            return false;
        _focusedLinkIndex = LinkFocus.Step(_focusedLinkIndex, _paneLinks.Count, forward);
        RenderFocusedLink();
        return true;
    }

    /// <summary>
    /// Activates the keyboard-focused link (<c>Enter</c>), raising <see cref="LinkActivationRequested"/>
    /// with the action <see cref="LinkActivator.Resolve"/> chooses for an unmodified activation — a task
    /// link opens in-app, any other link in the browser — identical to a plain left click (#318), so the
    /// two gestures can't drift. Returns <c>true</c> when a link was focused and the request was raised, and
    /// <c>false</c> when none is focused, so the caller lets <c>Enter</c> fall through undisturbed.
    /// </summary>
    public bool ActivateFocusedLink()
    {
        if (_focusedLinkIndex < 0 || _focusedLinkIndex >= _paneLinks.Count)
            return false;
        var span = _paneLinks[_focusedLinkIndex].Span;
        LinkActivationRequested?.Invoke(this, new LinkActivationRequest(span, LinkActivator.Resolve(span, ctrl: false)));
        return true;
    }

    /// <summary>
    /// Re-tags the cells so the currently focused link carries <see cref="FocusedLinkMarker"/> and reloads
    /// them, then scrolls the focused link into view. Reloading homes the viewport (as <see cref="SetBody"/>
    /// does), so the explicit re-wrap + <see cref="EnsureFocusedLinkVisible"/> below restore a scroll that
    /// shows the focused link — reusing the base view's own wrapped layout rather than re-implementing word
    /// wrap (the same principle the click hit test follows). A <c>Tab</c> is an infrequent, discrete keypress
    /// over a short body, so the re-wrap is cheap and keeps the scroll deterministic (no post-layout defer).
    /// </summary>
    private void RenderFocusedLink()
    {
        var focused = _focusedLinkIndex >= 0 && _focusedLinkIndex < _paneLinks.Count
            ? _paneLinks[_focusedLinkIndex]
            : (PaneLink?)null;
        MoveHome();
        Load(BuildCells(_body, _separator, focused));
        InheritsPreviousAttribute = false;
        // TextView.Load leaves the model unwrapped until a draw pass re-wraps it; force that pass now so the
        // display-row lookup in EnsureFocusedLinkVisible matches what will be drawn (mirrors the test harness).
        if (WordWrap)
        {
            WordWrap = false;
            WordWrap = true;
        }
        EnsureFocusedLinkVisible();
    }

    /// <summary>
    /// Scrolls the pane, if needed, so the focused link's wrapped display rows sit within the viewport. The
    /// focused rows are found by their <see cref="DetailCellStyle.FocusedLink"/> tag in the wrapped lines (no
    /// word-wrap maths of our own), and the viewport is nudged the minimum needed — up to the link's first
    /// row when it's above the viewport, down to its last row when it's below. A no-op when nothing is
    /// focused or the link is already visible.
    /// </summary>
    private void EnsureFocusedLinkVisible()
    {
        if (_focusedLinkIndex < 0)
            return;

        var lines = GetAllLines();
        int firstRow = -1, lastRow = -1;
        for (var row = 0; row < lines.Count; row++)
        {
            if (!lines[row].Any(c => ClassifyCell(c) == DetailCellStyle.FocusedLink))
                continue;
            if (firstRow < 0)
                firstRow = row;
            lastRow = row;
        }
        if (firstRow < 0)
            return;

        var viewport = Viewport;
        // The visible height is the viewport's once the pane is laid out; before the first layout pass
        // (only reachable from a driver-free unit test) it reads 0, so fall back to the frame height — for
        // this borderless read-only pane the two are equal, so the runtime path is unchanged.
        var height = viewport.Height > 0 ? viewport.Height : Frame.Height;
        if (firstRow < viewport.Y)
            ScrollTo(new Point(0, firstRow));
        else if (height > 0 && lastRow >= viewport.Y + height)
            ScrollTo(new Point(0, lastRow - height + 1));
    }

    /// <summary>
    /// Splits <paramref name="body"/> into one <see cref="Cell"/> list per line, tagging the cells of
    /// any line that exactly equals <paramref name="separator"/> with <see cref="SeparatorMarker"/>,
    /// tagging the cells covered by each detected link (via <see cref="TaskLinkExtractor"/>) with
    /// <see cref="TaskLinkMarker"/> / <see cref="WebLinkMarker"/> by kind (#317), and leaving every other
    /// cell's attribute null (so it draws in the pane's normal read-only colour). Pure — no Terminal.Gui
    /// draw surface — so the separator and link classification are unit-tested.
    /// </summary>
    public static List<List<Cell>> BuildCells(string body, string separator, PaneLink? focused = null)
    {
        var lines = body.Split('\n');
        var cells = new List<List<Cell>>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line == separator)
            {
                cells.Add(Cell.ToCellList(line, SeparatorMarker));
                continue;
            }

            // Links never span a newline (the URL regex stops at whitespace), so extracting per line
            // yields offsets that are char indices into this exact line — no global→(line,col) mapping.
            var links = TaskLinkExtractor.Extract(line);
            // The keyboard-focused link (#319), if it sits on this line, is tagged with FocusedLinkMarker
            // instead of its kind marker so the draw override can emphasise it.
            var focusedSpan = focused is { } f && f.LineIndex == i ? f.Span : (LinkSpan?)null;
            cells.Add(links.Count == 0 ? Cell.ToCellList(line, null) : BuildLineWithLinks(line, links, focusedSpan));
        }
        return cells;
    }

    /// <summary>
    /// Every clickable link in <paramref name="body"/>, in document order (source line ascending, then span
    /// order within a line), as <see cref="PaneLink"/>s whose <see cref="PaneLink.LineIndex"/> indexes the
    /// body split on <c>'\n'</c> — the same source-line coordinate space <see cref="BuildCells"/> and the
    /// mouse hit test use. Separator lines are skipped exactly as <see cref="BuildCells"/> skips them, so
    /// keyboard focus (#319) can never land on a link the renderer never drew. Pure — no draw surface — so
    /// the ordering is unit-tested.
    /// </summary>
    public static IReadOnlyList<PaneLink> ExtractPaneLinks(string body, string separator)
    {
        var lines = body.Split('\n');
        var result = new List<PaneLink>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] == separator)
                continue;
            foreach (var span in TaskLinkExtractor.Extract(lines[i]))
                result.Add(new PaneLink(i, span));
        }
        return result;
    }

    /// <summary>
    /// Builds one line's cells with each <paramref name="links"/> span tagged by its kind. The line is
    /// assembled from char-offset segments — untagged run, tagged link, untagged run, … — so a link's cells
    /// carry <see cref="TaskLinkMarker"/> / <see cref="WebLinkMarker"/> while everything else stays null.
    /// Segment boundaries fall on link edges (whitespace/URL characters), never inside a grapheme cluster,
    /// so slicing by UTF-16 char index is safe. <paramref name="links"/> is in document order and
    /// non-overlapping (as <see cref="TaskLinkExtractor.Extract"/> guarantees).
    /// </summary>
    private static List<Cell> BuildLineWithLinks(
        string line, IReadOnlyList<LinkSpan> links, LinkSpan? focusedSpan = null)
    {
        var result = new List<Cell>(line.Length);
        var pos = 0;
        foreach (var span in links)
        {
            if (span.Start > pos)
                result.AddRange(Cell.ToCellList(line[pos..span.Start], null));
            var marker = span.Equals(focusedSpan) ? FocusedLinkMarker
                : span.Kind == LinkKind.Task ? TaskLinkMarker : WebLinkMarker;
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
    public static DetailCellStyle[] ClassifyRowLinkCells(IReadOnlyList<Cell> row) => ClassifyRow(row).Styles;

    /// <summary>
    /// The single per-row link re-extraction that both the underline (#413, <see cref="ClassifyRowLinkCells"/>)
    /// and the OSC-8 target (#380/#430, <see cref="RowLinkUrls"/>) are projected from, so the two can never
    /// disagree on which cells are a link. For each cell it returns its link kind style
    /// (<see cref="DetailCellStyle.TaskLink"/> / <see cref="DetailCellStyle.WebLink"/> /
    /// <see cref="DetailCellStyle.Normal"/> — separators are handled from the tag, which the wrap bug leaves
    /// correct) and that link's resolved <see cref="LinkSpan.Url"/> (the true target, so a markdown link's
    /// visible-text cells carry its destination, not its prose). Both are computed from the row's own
    /// graphemes, so they are offset-correct on wrapped continuation rows.
    /// </summary>
    private static (DetailCellStyle[] Styles, string?[] Urls) ClassifyRow(IReadOnlyList<Cell> row)
    {
        var styles = new DetailCellStyle[row.Count];
        var urls = new string?[row.Count];
        if (row.Count == 0)
            return (styles, urls);

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
            return (styles, urls);

        var links = TaskLinkExtractor.Extract(rendered);
        if (links.Count == 0)
            return (styles, urls);

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
            {
                styles[i] = links[li].Kind == LinkKind.Task ? DetailCellStyle.TaskLink : DetailCellStyle.WebLink;
                urls[i] = links[li].Url;
            }
        }

        return (styles, urls);
    }

    /// <summary>
    /// The source-line origin of one rendered (word-wrapped) display row: which source line it came from
    /// (<see cref="SourceLineIndex"/>, indexing the body split on <c>'\n'</c>) and the char offset within
    /// that line where the row's first cell begins (<see cref="StartOffset"/>). <see cref="SourceLineIndex"/>
    /// is <c>-1</c> for a row that could not be reconciled (the draw path then falls back to the per-row
    /// re-extraction, so nothing regresses). Produced by <see cref="BuildRowSourceMap"/>.
    /// </summary>
    public readonly record struct RowSource(int SourceLineIndex, int StartOffset);

    /// <summary>
    /// The display→source-line mapping for #443: one <see cref="RowSource"/> per rendered
    /// <paramref name="wrappedRows"/> row, recovered by <b>reconciling Terminal.Gui's published wrap output
    /// against the source lines</b> — not by reaching into its <c>internal WordWrapManager</c> and not by
    /// reimplementing word wrap. Word wrap only ever splits a source line into consecutive display rows in
    /// order (never merging or reordering), so each wrapped row's reconstructed text is located in the
    /// current source line with <see cref="string.IndexOf(string, int, StringComparison)"/> from a running
    /// cursor; a soft-wrap dropping the break whitespace is handled for free (the next row's text is simply
    /// found <em>after</em> the gap), and a URL hard-wrapped mid-token drops no char so its fragments are
    /// found contiguously. With this mapping, link styling can be driven from the <em>source line's</em>
    /// <see cref="LinkSpan"/>s (which see the whole, unsplit link) via <see cref="ClassifyRowFromSource"/>,
    /// closing the wrap-split gap #413/#430 left. Pure — no Terminal.Gui draw surface — and, because a real
    /// <see cref="DetailPaneView"/> wraps headlessly, unit-tested against genuine <see cref="GetAllLines"/>
    /// output. Separator lines are ordinary source lines here (their short rule never wraps, so it maps
    /// one-to-one); the draw path still styles them from the tag, unchanged.
    /// </summary>
    public static IReadOnlyList<RowSource> BuildRowSourceMap(
        IReadOnlyList<string> sourceLines, IReadOnlyList<IReadOnlyList<Cell>> wrappedRows)
    {
        var result = new RowSource[wrappedRows.Count];
        var srcIdx = 0;
        var cursor = 0;
        for (var r = 0; r < wrappedRows.Count; r++)
        {
            var rowText = RowText(wrappedRows[r]);
            var matched = false;
            while (srcIdx < sourceLines.Count)
            {
                var line = sourceLines[srcIdx];
                var start = Math.Min(cursor, line.Length);
                if (rowText.Length == 0)
                {
                    // An empty display row only ever comes from an empty (or fully consumed) source line —
                    // word wrap never emits a blank row mid-content. Match it there and move to the next line.
                    if (start >= line.Length)
                    {
                        result[r] = new RowSource(srcIdx, start);
                        srcIdx++;
                        cursor = 0;
                        matched = true;
                    }
                    break;
                }

                var at = start <= line.Length ? line.IndexOf(rowText, start, StringComparison.Ordinal) : -1;
                if (at >= 0)
                {
                    result[r] = new RowSource(srcIdx, at);
                    cursor = at + rowText.Length;
                    // When the source line is fully consumed, the next display row begins a new source line.
                    if (cursor >= line.Length)
                    {
                        srcIdx++;
                        cursor = 0;
                    }
                    matched = true;
                    break;
                }

                // This source line can't hold the row's text at/after the cursor → it belongs to a later one.
                srcIdx++;
                cursor = 0;
            }

            if (!matched)
                result[r] = new RowSource(-1, 0);
        }

        return result;
    }

    /// <summary>
    /// Classifies each cell of one rendered (possibly word-wrapped) <paramref name="row"/> from the
    /// <em>source line</em> it came from (<paramref name="sourceLine"/>) and the char
    /// <paramref name="startOffset"/> at which the row begins in it (both from <see cref="BuildRowSourceMap"/>).
    /// Because the <see cref="LinkSpan"/>s come from the whole source line rather than the row's own
    /// graphemes, a link that word wrap splits across two rows is classified <b>contiguously on every row it
    /// touches</b> (#443) — the head and tail of an over-long bare URL both carry that URL, and a markdown
    /// link's visible-text cells carry its <b>resolved</b> target no matter which row they land on, while the
    /// <c>[</c>/<c>]</c>/<c>(url)</c> markup outside the span stays <see cref="DetailCellStyle.Normal"/> /
    /// <see langword="null"/>. Each cell's source offset is <paramref name="startOffset"/> plus the row's own
    /// accumulated grapheme length (a cell is one grapheme, which may be several UTF-16 chars), the same
    /// accounting <see cref="ClassifyRow"/> uses. Pure and Terminal.Gui-draw-free, so it is unit-tested.
    /// </summary>
    public static (DetailCellStyle[] Styles, string?[] Urls) ClassifyRowFromSource(
        IReadOnlyList<Cell> row, string sourceLine, int startOffset)
    {
        var styles = new DetailCellStyle[row.Count];
        var urls = new string?[row.Count];
        if (row.Count == 0)
            return (styles, urls);

        // Cheap bail-out mirroring ClassifyRow: every link (bare or markdown) carries an http(s) scheme, so
        // a source line without one has no links and skips the regex entirely — the common case for body
        // text, and a source line can wrap into several rows that each hit this path.
        if (sourceLine.IndexOf("http", StringComparison.OrdinalIgnoreCase) < 0)
            return (styles, urls);

        var links = TaskLinkExtractor.Extract(sourceLine);
        if (links.Count == 0)
            return (styles, urls);

        // Walk cells and links together in one pass, tracking each cell's char offset within the source line.
        var charPos = startOffset;
        var li = 0;
        for (var i = 0; i < row.Count; i++)
        {
            var off = charPos;
            charPos += row[i].Grapheme?.Length ?? 0;
            while (li < links.Count && off >= links[li].End)
                li++;
            if (li >= links.Count)
                break;
            if (off >= links[li].Start && off < links[li].End)
            {
                styles[i] = links[li].Kind == LinkKind.Task ? DetailCellStyle.TaskLink : DetailCellStyle.WebLink;
                urls[i] = links[li].Url;
            }
        }

        return (styles, urls);
    }

    // Reconstructs a rendered row's text from its cells' graphemes — the string BuildRowSourceMap locates in
    // the source line and ClassifyRow re-extracts from. A cell is one grapheme (possibly multi-char), so this
    // is the row's exact character content.
    private static string RowText(IReadOnlyList<Cell> row)
    {
        var text = new StringBuilder(row.Count);
        foreach (var cell in row)
            text.Append(cell.Grapheme ?? string.Empty);
        return text.ToString();
    }

    // Per-row cache for the draw path: OnDrawReadOnlyColor is invoked once per cell, but the row's link
    // classification (both the kind style #413 and the OSC-8 URL #380/#430) only needs computing once per
    // row. Preferably from the source-line mapping (#443, via _rowSourceMap) so a wrap-split link is styled
    // contiguously; falling back to a single per-row ClassifyRow re-extraction for any row that doesn't
    // reconcile. Keyed on the row list reference (Terminal.Gui's wrap model holds a distinct list per
    // rendered row), so a new row triggers a recompute.
    private List<Cell>? _linkRow;
    private DetailCellStyle[]? _linkRowStyles;
    private string?[]? _linkRowUrls;

    // The reference-keyed display→source mapping (#443), built once from GetAllLines() and rebuilt when a
    // drawn row isn't found in it (i.e. the wrap changed — a resize or a re-render). A row that reconciles
    // is styled from its source line's spans; a row that doesn't falls back to per-row re-extraction.
    private Dictionary<List<Cell>, RowSource>? _rowSourceMap;

    private void EnsureRowLinkCache(List<Cell> line)
    {
        if (ReferenceEquals(_linkRow, line) && _linkRowStyles is { } s && s.Length == line.Count && _linkRowUrls is not null)
            return;

        // Prefer the source-line mapping (#443) so a link word wrap split across rows is styled contiguously;
        // fall back to the per-row re-extraction (#413/#430) for any row that doesn't reconcile — that is
        // exactly today's behaviour, so a reconciliation miss never regresses.
        if (TryGetRowSource(line) is { SourceLineIndex: >= 0 } src && src.SourceLineIndex < _lines.Length)
            (_linkRowStyles, _linkRowUrls) = ClassifyRowFromSource(line, _lines[src.SourceLineIndex], src.StartOffset);
        else
            (_linkRowStyles, _linkRowUrls) = ClassifyRow(line);
        _linkRow = line;
    }

    // The source-line origin of a drawn row, from the reference-keyed map (built once from GetAllLines()).
    // A drawn row is one of GetLine()'s lists, and Terminal.Gui hands the draw path that same reference, so a
    // hit is the common case; a miss (first draw of a new wrap generation) rebuilds the map from the current
    // wrapped lines and retries. Returns null only if the row still isn't found (defensive) — the caller
    // then falls back to per-row re-extraction.
    private RowSource? TryGetRowSource(List<Cell> line)
    {
        if (_rowSourceMap is { } map && map.TryGetValue(line, out var found))
            return found;

        var wrapped = GetAllLines();
        var sources = BuildRowSourceMap(_lines, wrapped);
        var rebuilt = new Dictionary<List<Cell>, RowSource>(wrapped.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < wrapped.Count; i++)
            rebuilt[wrapped[i]] = sources[i];
        _rowSourceMap = rebuilt;
        return rebuilt.TryGetValue(line, out var value) ? value : null;
    }

    // The OSC-8 target for the cell about to be drawn (null outside the row / on a non-link cell), from the
    // per-row cache — parallel to LinkStyleAt.
    private string? LinkUrlAt(List<Cell> line, int idxCol)
    {
        EnsureRowLinkCache(line);
        return idxCol >= 0 && idxCol < _linkRowUrls!.Length ? _linkRowUrls[idxCol] : null;
    }

    private DetailCellStyle LinkStyleAt(List<Cell> line, int idxCol)
    {
        EnsureRowLinkCache(line);
        return idxCol >= 0 && idxCol < _linkRowStyles!.Length ? _linkRowStyles[idxCol] : DetailCellStyle.Normal;
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
    /// an unmodified click is passed to the base and its own mapping read back, and <b>a bare
    /// <c>Shift</c> or any <c>Alt</c> click is refused outright</b> — it isn't an activation gesture, and
    /// admitting one would activate whatever link the caret last sat on. A <c>Ctrl+Shift</c>+click (the
    /// #320 inversion gesture) <em>is</em> admitted: it carries <c>Ctrl</c>, so it joins the
    /// resolved-by-synthesized-click arm and its destination is chosen by
    /// <see cref="TaskLinkCtrlClickDestination"/>.
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
        // Hover feedback (#408): a bare motion report (button-less move, MouseFlags.PositionReport) names the
        // link under the pointer on the status line. The target is read from the draw path's own per-row
        // extraction (RowLinkUrls) — no caret move, no viewport nudge, no cell restyle — and deduped so only
        // a change fires HoverTargetChanged. This never handles the event: it always falls through to the
        // click/base logic below, so a click that also carries a position is unaffected.
        if (mouseEvent.Flags.HasFlag(MouseFlags.PositionReport) && mouseEvent.Position is { } hoverPosition)
            UpdateHoverTarget(HoverLinkTargetAt(hoverPosition, Viewport));

        // Only a plain, Ctrl, or Ctrl+Shift left click activates. Every other flag combination — a wheel,
        // a press/release (drag), a double- or triple-click (each its own distinct flag), an Alt-modified
        // click, and a *bare* Shift click — falls through to the base view untouched. Refusing those is
        // what keeps a gesture the base won't map from resolving to the stale caret (see above); it is not
        // merely a taste call about which gestures mean "activate". Ctrl+Shift is admitted (the #320
        // inversion gesture): it carries Ctrl, so it joins the synthesized-plain-click position arm below.
        var ctrl = mouseEvent.Flags.HasFlag(MouseFlags.Ctrl);
        var shift = mouseEvent.Flags.HasFlag(MouseFlags.Shift);
        if (!mouseEvent.Flags.HasFlag(MouseFlags.LeftButtonClicked)
            || (shift && !ctrl)
            || mouseEvent.Flags.HasFlag(MouseFlags.Alt)
            || mouseEvent.Position is not { } position)
            return base.OnMouseEvent(mouseEvent);

        // The viewport as the user saw it — the base may scroll it while handling the click (see above).
        var viewport = Viewport;

        // An unmodified click is the one the base view maps itself, so let it handle the real event and
        // read the position back; only a modified click (Ctrl or Ctrl+Shift) needs the synthesized
        // stand-in (see above), which keeps the common gesture a single pass through the base.
        var handledByBase = !ctrl && base.OnMouseEvent(mouseEvent);
        if (LinkAt(position, viewport, resolvePosition: ctrl) is not { } span)
            return handledByBase;

        LinkActivationRequested?.Invoke(
            this, new LinkActivationRequest(span, LinkActivator.Resolve(span, ctrl, shift, TaskLinkCtrlClickDestination)));
        mouseEvent.Handled = true;
        return true;
    }

    /// <summary>
    /// Raises <see cref="HoverTargetChanged"/> when <paramref name="target"/> differs from the last value
    /// raised — the dedup that keeps hover off the per-keypress redraw cost: a motion report that stays on
    /// the same link (or off every link) raises nothing, so the hint surface repaints only when the hovered
    /// link changes. <paramref name="target"/> is <c>null</c> when the pointer is not on a link.
    /// </summary>
    private void UpdateHoverTarget(string? target)
    {
        if (string.Equals(target, _lastHoverTarget, StringComparison.Ordinal))
            return;
        _lastHoverTarget = target;
        HoverTargetChanged?.Invoke(this, target);
    }

    /// <summary>
    /// The <b>resolved</b> target URL of the link under a viewport-relative point (#408), or <c>null</c> when
    /// the point is not on a link. Pure of side effects — unlike the click path it does <em>not</em> move the
    /// base view's caret or nudge the viewport, so it is safe to run on every motion report. It reuses the
    /// draw path's own per-row link extraction (<see cref="LinkUrlForCell"/> → <see cref="RowLinkUrls"/> →
    /// <see cref="ClassifyRow"/>) against the already-wrapped display row Terminal.Gui hands the draw path,
    /// so it adds no word-wrap maths of its own and can't disagree with what the pane drew.
    /// <paramref name="viewport"/> is the pane's current <see cref="Terminal.Gui.ViewBase.View.Viewport"/>.
    /// </summary>
    public string? HoverLinkTargetAt(Point position, Rectangle viewport)
    {
        // Below the last wrapped row (the #318 "under a short body" clamp): Lines is the wrapped row count.
        var displayRow = viewport.Y + position.Y;
        if (displayRow < 0 || displayRow >= Lines)
            return null;

        var row = GetLine(displayRow);
        // Right of the row's rendered text (the #318 "past the text" clamp), and the column→cell mapping in
        // one pass. Deliberately ignores viewport.X: the pane is word-wrapped, so content never scrolls
        // horizontally and a report's column is its column in the row (same rationale as LinkAt's guard 2).
        var cell = CellIndexAtColumn(row, position.X);
        if (cell < 0)
            return null;

        // Reuse the draw path's reference-keyed per-row cache (GetLine hands back the same list reference
        // the draw path caches), so a motion report over a row costs a dictionary hit, not a re-extraction.
        return LinkUrlAt(row, cell);
    }

    /// <summary>
    /// The index of the cell occupying <paramref name="column"/> in a laid-out display <paramref name="row"/>,
    /// or <c>-1</c> when <paramref name="column"/> is left of 0 or right of the row's rendered width. Walks
    /// per-grapheme column widths so a row carrying wide runes maps a reported column to the correct cell
    /// (identity on an ASCII run such as a URL). Mirrors the column measure <see cref="LinkAt"/> guards with.
    /// </summary>
    private static int CellIndexAtColumn(List<Cell> row, int column)
    {
        if (column < 0)
            return -1;
        var col = 0;
        for (var i = 0; i < row.Count; i++)
        {
            // The cell's own column width, straight off its grapheme (no per-cell List allocation) — the
            // same GetColumns() measure ContextualFooter fits the help line with.
            var width = (row[i].Grapheme ?? string.Empty).GetColumns();
            if (width <= 0)
                width = 1; // defensive: never stall on a zero-width cell
            if (column < col + width)
                return i;
            col += width;
        }
        return -1;
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave()
    {
        // The pointer left the pane entirely (onto another view): clear any hover hint it was showing.
        // A move that merely leaves a link but stays in the pane is handled by the motion arm returning
        // null; this covers the case where no further motion report reaches this pane at all (#408).
        UpdateHoverTarget(null);
        base.OnMouseLeave();
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
        // OSC-8 hyperlink (#380/#430): tag the cell about to be drawn with its link's resolved URL (or clear
        // it for a non-link cell) so the subsequent AddRune associates it and the ANSI output wraps the run in
        // an OSC-8 escape. Additive to #317's styling below; parallel to how SetAttribute drives CurrentAttribute.
        // The target comes from the same per-row re-extraction as the underline (#413), so it is offset-correct
        // on wrapped rows and carries a markdown link's true destination on its visible-text cells (#430); a
        // markdown link split across two rendered rows degrades gracefully — its (url) fragment becomes a plain
        // bare link, never a wrong target — with the visible-text hyperlinking deferred to #443.
        SetCurrentUrl(LinkUrlAt(line, idxCol));

        if (idxCol >= 0 && idxCol < line.Count)
        {
            // The separator and focused-link cues are tag-driven. A separator row is tagged uniformly, so
            // word wrap leaves its tag correct; the focused-link tag (#319) is only reliable on a source
            // (non-wrapped-continuation) row — the same wrap/attribute misalignment #413 works around for
            // link kind can move it on a continuation row, a residual limit tracked in #443.
            if (line[idxCol].Attribute is { } attr)
            {
                // A separator cell: keep the pane's read-only foreground for the rule glyph, but drop the
                // background to Color.None so the driver emits CSI 49m and the terminal's own default /
                // transparent background shows through instead of the grey read-only fill.
                if (attr.Background == Color.None)
                {
                    var readOnly = GetAttributeForRole(VisualRole.ReadOnly);
                    SetAttribute(new Attribute(readOnly.Foreground, Color.None, readOnly.Style));
                    return;
                }

                // The keyboard-focused link (#319): draw it in the theme's Focus role (a reverse-video-style
                // emphasis) plus an underline, so it stands out from the always-on link underline (#317
                // underlines every link, so the focus cue must be additional emphasis, not the underline).
                // Re-resolved from the live role each draw, so it stays theme-aware like the kind markers.
                if (attr.Equals(FocusedLinkMarker))
                {
                    var focus = GetAttributeForRole(VisualRole.Focus);
                    SetAttribute(new Attribute(focus.Foreground, focus.Background, focus.Style | TextStyle.Underline));
                    return;
                }
            }

            // A link cell (#317): keep the pane's live read-only background so the link sits in the pane
            // like surrounding text, but recolour the foreground (blue for a web link, the read-only
            // foreground for a task link) and add an underline. The kind is recomputed from the row's own
            // graphemes (#413) rather than the per-cell tag, which word wrap misaligns on continuation rows
            // — so this is deliberately NOT gated on the cell's own (possibly misaligned or null) attribute.
            // Re-resolving from the live role keeps the link theme-aware.
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
