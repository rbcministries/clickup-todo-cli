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
/// <see cref="ListWrapper{T}"/> (which it composes), then overlays the ClickUp status color on just
/// the <c>[status]</c> span of the row.
/// <para>
/// Composing the stock wrapper — rather than re-implementing rendering — means text layout,
/// horizontal scroll, wide-rune handling, selection highlight, marking, <see cref="ToList"/>
/// (which backs the type-ahead navigator, see #12), and change notifications are all inherited
/// unchanged. The only added behavior is recoloring a known character span, whose worst-case
/// failure is a mis-placed color cell, never garbled or missing text.
/// </para>
/// </summary>
public sealed class StatusBadgeListSource : IListDataSource
{
    /// <summary>A colored span on a row: the <c>[status]</c> badge's char offset, length, and attribute.</summary>
    public readonly record struct Badge(int Start, int Length, Attribute Attr);

    private readonly ObservableCollection<string> _text;
    private readonly IReadOnlyList<Badge?> _badges; // parallel to _text; null = no badge (e.g. header rows)
    private readonly ListWrapper<string> _inner;

    public StatusBadgeListSource(ObservableCollection<string> text, IReadOnlyList<Badge?> badges)
    {
        _text = text;
        _badges = badges;
        _inner = new ListWrapper<string>(text);
    }

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
    public IList ToList() => _inner.ToList();

    // ── Render = stock text + color overlay ──────────────────────────────────
    public void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX = 0)
    {
        _inner.Render(listView, selected, item, col, row, width, viewportX);

        var badge = item >= 0 && item < _badges.Count ? _badges[item] : null;
        if (badge is { } b && b.Length > 0 && item >= 0 && item < _text.Count)
            OverlayBadge(listView, b, col, row, width, viewportX, _text[item]);
    }

    /// <summary>
    /// Re-draws just the badge's runes with its attribute. Positions are computed in display-column
    /// space (honoring wide runes via <see cref="RuneExtensions.GetColumns"/>) and offset by the
    /// horizontal scroll (<paramref name="viewportX"/>); cells outside the viewport are skipped.
    /// </summary>
    private static void OverlayBadge(ListView listView, Badge badge, int col, int row, int width, int viewportX, string text)
    {
        var end = Math.Min(badge.Start + badge.Length, text.Length);
        var displayCol = 0; // display column within the full, unscrolled line
        for (var i = 0; i < end;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed);
            var runeWidth = Math.Max(1, rune.GetColumns());
            if (i >= badge.Start)
            {
                var x = displayCol - viewportX;
                if (x >= 0 && x + runeWidth <= width)
                {
                    listView.Move(col + x, row);
                    listView.SetAttribute(badge.Attr);
                    listView.AddRune(rune);
                }
            }
            displayCol += runeWidth;
            i += consumed;
        }
    }

    public void Dispose() => _inner.Dispose();
}
