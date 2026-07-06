using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace ClickUpTodo.Tui;

/// <summary>
/// A <see cref="ListView"/> data source that draws each row's text exactly like the stock
/// <see cref="ListWrapper{T}"/> (which it composes), then overlays ClickUp colors on the row's badge
/// spans — the <c>[status]</c> and (when set) <c>[priority]</c> brackets.
/// <para>
/// Composing the stock wrapper — rather than re-implementing rendering — means text layout,
/// horizontal scroll, wide-rune handling, selection highlight, marking, <see cref="ToList"/>
/// (which backs the type-ahead navigator, see #12), and change notifications are all inherited
/// unchanged. The only added behavior is recoloring known character spans, whose worst-case
/// failure is a mis-placed color cell, never garbled or missing text.
/// </para>
/// </summary>
public sealed class StatusBadgeListSource : IListDataSource
{
    /// <summary>A colored span on a row: a badge's char offset, length, and attribute.</summary>
    public readonly record struct Badge(int Start, int Length, Attribute Attr);

    private readonly ObservableCollection<string> _text;
    // Parallel to _text; each row's zero or more colored badge spans (empty = header row / no badges).
    private readonly IReadOnlyList<IReadOnlyList<Badge>> _badges;
    private readonly IReadOnlyList<Attribute?> _headerAttrs; // parallel to _text; non-null only on header rows
    // Parallel to _text; the per-row type-ahead search key (a task row's title only), decoupled from the
    // decorated display text so the #12 navigator matches titles even with a ▶/▼ marker + badges (#76).
    private readonly IReadOnlyList<string>? _searchKeys;
    private readonly ListWrapper<string> _inner;
    // Per-row grapheme layout, computed lazily and reused across frames. Render runs for every
    // visible row on every redraw (each keypress), and each badge/header overlay used to re-segment
    // the row's graphemes and re-measure their widths from scratch — O(row length) per badge per
    // frame, growing with emoji/wide-rune-heavy titles. A row's layout only changes when its
    // display string is replaced (an in-place status update), so cache by row and revalidate by
    // string identity.
    private readonly (string? Text, LaidOutGrapheme[] Layout)[] _layoutCache;

    public StatusBadgeListSource(
        ObservableCollection<string> text,
        IReadOnlyList<IReadOnlyList<Badge>> badges,
        IReadOnlyList<Attribute?>? headerAttrs = null,
        IReadOnlyList<string>? searchKeys = null)
    {
        _text = text;
        _badges = badges;
        _headerAttrs = headerAttrs ?? new Attribute?[text.Count];
        _searchKeys = searchKeys;
        _inner = new ListWrapper<string>(text);
        // Structural changes rebuild the whole source (TodoApp.Render assigns a new one), so the
        // row count is fixed for this instance's lifetime; only row strings are replaced in place.
        _layoutCache = new (string?, LaidOutGrapheme[])[text.Count];
    }

    /// <summary>
    /// Builds the full-row background attribute for a group header from its hex color (background from
    /// the color, foreground the higher-contrast of black/white), or null when the color is
    /// missing/malformed — the caller then uses <see cref="NeutralHeaderAttr"/> instead.
    /// </summary>
    public static Attribute? HeaderAttr(string? hexColor)
    {
        if (!StatusBadgeColor.TryParseHex(hexColor, out var r, out var g, out var b))
            return null;
        var background = new Color(r, g, b, 255);
        var foreground = StatusBadgeColor.PreferDarkText(r, g, b)
            ? new Color(0, 0, 0, 255)
            : new Color(255, 255, 255, 255);
        return new Attribute(foreground, background);
    }

    /// <summary>A muted gray bar for header rows with no field color (the pinned/tasks sections, the
    /// "no date" bucket, or a list whose color isn't resolved).</summary>
    public static Attribute NeutralHeaderAttr { get; } =
        new(new Color(210, 210, 210, 255), new Color(58, 58, 58, 255));

    /// <summary>
    /// Builds a badge from a status hex color, or null when there's no badge (no status) or the
    /// color is missing/malformed (the row then renders with the default attributes).
    /// </summary>
    public static Badge? TryCreate(int start, int length, string? hexColor)
    {
        if (length <= 0 || start < 0)
            return null;
        if (!StatusBadgeColor.TryParseHex(hexColor, out var r, out var g, out var b))
            return null;

        var background = new Color(r, g, b, 255);
        var foreground = StatusBadgeColor.PreferDarkText(r, g, b)
            ? new Color(0, 0, 0, 255)
            : new Color(255, 255, 255, 255);
        return new Badge(start, length, new Attribute(foreground, background));
    }

    // ── Delegated to the stock wrapper ───────────────────────────────────────
    public int Count => _inner.Count;
    public int MaxItemLength => _inner.MaxItemLength;

    public bool SuspendCollectionChangedEvent
    {
        get => _inner.SuspendCollectionChangedEvent;
        set => _inner.SuspendCollectionChangedEvent = value;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => _inner.CollectionChanged += value;
        remove => _inner.CollectionChanged -= value;
    }

    public bool IsMarked(int item) => _inner.IsMarked(item);
    public void SetMark(int item, bool value) => _inner.SetMark(item, value);
    public bool RenderMark(ListView listView, int item, int row, bool isMarked, bool markMultiple)
        => _inner.RenderMark(listView, item, row, isMarked, markMultiple);

    /// <summary>
    /// The list the type-ahead navigator (#12) searches. When per-row search keys were supplied (#76)
    /// this returns them — a task row's title only — so typing a title's first letters still jumps to it
    /// even though the rendered line now leads with a ▶/▼ fold marker (and carries badges/metadata). With
    /// no keys it delegates to the stock wrapper (the decorated display strings), preserving old behaviour.
    /// </summary>
    public IList ToList() => _searchKeys is null ? _inner.ToList() : new List<string>(_searchKeys);

    // ── Render = stock text + color overlay ──────────────────────────────────
    public void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX = 0)
    {
        _inner.Render(listView, selected, item, col, row, width, viewportX);

        if (item < 0 || item >= _text.Count)
            return;

        // A header row paints its whole width with the bar attribute — but only when unselected, so the
        // list's selection highlight still shows the cursor when it lands on a header. Headers never
        // carry a badge, so the two overlays are mutually exclusive.
        var headerAttr = item < _headerAttrs.Count ? _headerAttrs[item] : null;
        if (headerAttr is { } ha && !selected)
        {
            PaintHeaderBar(listView, ha, col, row, width, viewportX, LayoutFor(item));
            return;
        }

        if (item >= _badges.Count)
            return;
        var layout = LayoutFor(item);
        foreach (var badge in _badges[item])
            if (badge.Length > 0)
                OverlayBadge(listView, badge, col, row, width, viewportX, layout);
    }

    /// <summary>The cached grapheme layout for a row, recomputed only when the row's display
    /// string was replaced (reference comparison — rows are only ever swapped wholesale).</summary>
    private LaidOutGrapheme[] LayoutFor(int item)
    {
        var text = _text[item];
        if ((uint)item >= (uint)_layoutCache.Length)
            return LayOutGraphemes(text).ToArray(); // defensive; row count is fixed in practice

        ref var entry = ref _layoutCache[item];
        if (!ReferenceEquals(entry.Text, text))
        {
            entry.Text = text;
            entry.Layout = LayOutGraphemes(text).ToArray();
        }
        return entry.Layout;
    }

    /// <summary>
    /// Re-draws an entire header row — text then space-padding out to <paramref name="width"/> — in the
    /// bar attribute, so the color spans the full line (the stock wrapper already padded with spaces in
    /// the base attribute; this recolors those cells). Wide runes and horizontal scroll are honored the
    /// same way as <see cref="OverlayBadge"/>.
    /// </summary>
    private static void PaintHeaderBar(ListView listView, Attribute attr, int col, int row, int width, int viewportX, IReadOnlyList<LaidOutGrapheme> layout)
    {
        var baseAttr = listView.SetAttribute(attr);

        var displayCol = 0; // total columns consumed by the text, for where padding starts
        foreach (var g in layout)
        {
            var x = g.Column - viewportX;
            if (x >= 0 && x + g.Width <= width)
            {
                listView.Move(col + x, row);
                listView.AddStr(g.Text);
            }
            displayCol = g.Column + g.Width;
        }

        // Pad the rest of the visible line with spaces so the bar fills the frame width.
        for (var x = Math.Max(0, displayCol - viewportX); x < width; x++)
        {
            listView.Move(col + x, row);
            listView.AddRune(new Rune(' '));
        }

        listView.SetAttribute(baseAttr);
    }

    /// <summary>
    /// Re-draws just the badge's characters with its attribute. Positions are computed in
    /// display-column space using <see cref="LayOutGraphemes"/> — the same grapheme-aware width the
    /// stock renderer uses (<see cref="StringExtensions.GetColumns(string, bool)"/>) — and offset by
    /// the horizontal scroll (<paramref name="viewportX"/>); cells outside the viewport are skipped.
    /// Computing widths any other way (e.g. per-rune) drifts from the base renderer for names with
    /// wide/combining/emoji runes and mis-places the color (see #63).
    /// </summary>
    private static void OverlayBadge(ListView listView, Badge badge, int col, int row, int width, int viewportX, IReadOnlyList<LaidOutGrapheme> layout)
    {
        var end = badge.Start + badge.Length;

        // The driver's current attribute is global, shared state. The stock wrapper just rendered
        // this row's text and left that base attribute current; switching to the badge attribute and
        // leaving it set would taint the next row's space-padding (the stock wrapper pads to width
        // with whatever attribute is current), bleeding this badge's background onto rows below it
        // (see #34). Capture the base attribute and restore it once we're done.
        var baseAttr = listView.SetAttribute(badge.Attr);

        foreach (var g in layout)
        {
            if (g.CharIndex >= end)
                break;
            if (g.CharIndex < badge.Start)
                continue;
            var x = g.Column - viewportX;
            if (x >= 0 && x + g.Width <= width)
            {
                listView.Move(col + x, row);
                listView.AddStr(g.Text);
            }
        }

        listView.SetAttribute(baseAttr);
    }

    /// <summary>A grapheme cluster of a row's text with the display <paramref name="Column"/> it starts
    /// at (in the full, unscrolled line), its display <paramref name="Width"/> in columns, and the
    /// UTF-16 <paramref name="CharIndex"/> of its first char.</summary>
    public readonly record struct LaidOutGrapheme(int Column, int Width, int CharIndex, string Text);

    /// <summary>
    /// Walks a row's text as grapheme clusters, reporting each cluster's start display column, width,
    /// and char offset. Widths use Terminal.Gui's grapheme-aware <see cref="StringExtensions.GetColumns(string, bool)"/>
    /// (per cluster), so a cluster's <c>Column</c> equals <c>text[..CharIndex].GetColumns()</c> — i.e.
    /// exactly where the stock <see cref="ListWrapper{T}"/> draws it. Pure (no Terminal.Gui draw
    /// surface), so the column math the overlays depend on is unit-testable (see #63).
    /// </summary>
    public static IEnumerable<LaidOutGrapheme> LayOutGraphemes(string text)
    {
        var charIndex = 0;
        var displayCol = 0;
        foreach (var grapheme in GraphemeHelper.GetGraphemes(text))
        {
            var w = grapheme.GetColumns();
            yield return new LaidOutGrapheme(displayCol, w, charIndex, grapheme);
            displayCol += w;
            charIndex += grapheme.Length;
        }
    }

    public void Dispose() => _inner.Dispose();
}
