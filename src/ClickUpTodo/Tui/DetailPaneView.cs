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

    public DetailPaneView()
    {
        ReadOnly = true;
        WordWrap = true;
    }

    /// <summary>Loads <paramref name="body"/> into the pane, tagging every line equal to
    /// <paramref name="separator"/> so it is drawn on the terminal-default background. Safe to call
    /// repeatedly (e.g. a Stream sort toggle re-renders in place).</summary>
    public void SetBody(string body, string separator)
    {
        Load(BuildCells(body, separator));
        // Load() enables attribute inheritance (a null-attribute cell would copy the previous cell's
        // colour). We tag whole separator lines explicitly and want every other cell to fall back to
        // the read-only role, so keep inheritance off. (Moot while ReadOnly, but explicit.)
        InheritsPreviousAttribute = false;
    }

    /// <summary>
    /// Splits <paramref name="body"/> into one <see cref="Cell"/> list per line, tagging the cells of
    /// any line that exactly equals <paramref name="separator"/> with <see cref="SeparatorMarker"/> and
    /// leaving every other cell's attribute null (so it draws in the pane's normal read-only colour).
    /// Pure — no Terminal.Gui draw surface — so the separator classification is unit-tested.
    /// </summary>
    public static List<List<Cell>> BuildCells(string body, string separator)
    {
        var lines = body.Split('\n');
        var cells = new List<List<Cell>>(lines.Length);
        foreach (var line in lines)
            cells.Add(Cell.ToCellList(line, line == separator ? SeparatorMarker : (Attribute?)null));
        return cells;
    }

    /// <inheritdoc/>
    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count
            && line[idxCol].Attribute is { } attr && attr.Background == Color.None)
        {
            // A separator cell: keep the pane's read-only foreground for the rule glyph, but drop the
            // background to Color.None so the driver emits CSI 49m and the terminal's own default /
            // transparent background shows through instead of the grey read-only fill.
            var readOnly = GetAttributeForRole(VisualRole.ReadOnly);
            SetAttribute(new Attribute(readOnly.Foreground, Color.None, readOnly.Style));
            return;
        }

        base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }
}
