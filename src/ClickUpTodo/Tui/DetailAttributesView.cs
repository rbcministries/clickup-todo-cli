using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace ClickUpTodo.Tui;

/// <summary>
/// A small, read-only, non-focusable view that draws the task detail Other tab's header attributes
/// (List / Lists / Priority / Status / dates) with per-run colour — the machinery the plain
/// <see cref="Terminal.Gui.Views.TextView"/> lacks (issue #66). Each line's runs come from the pure,
/// unit-tested <see cref="TaskDetailFormatter.HeaderAttributeLines"/>; this view is only the
/// (CI-untestable) Terminal.Gui glue that turns a run's hex colour into a badge attribute and paints it.
/// <para>
/// It is sized to exactly the number of lines and never scrolls (the header is small and always fits);
/// the scrollable, word-wrapped "Custom fields:" body lives in a separate <c>TextView</c> beneath it.
/// A coloured run reuses the same badge attribute as the list row
/// (<see cref="StatusBadgeListSource.HeaderAttr"/> → foreground/background via
/// <see cref="StatusBadgeColor.PreferDarkText"/>), so status/priority read identically in both places.
/// </para>
/// </summary>
public sealed class DetailAttributesView : View
{
    private IReadOnlyList<TaskDetailFormatter.DetailLine> _lines;

    public DetailAttributesView(IReadOnlyList<TaskDetailFormatter.DetailLine> lines)
    {
        _lines = lines;
        CanFocus = false;
    }

    /// <summary>Swaps in a fresh set of attribute lines (a detail-view refresh, #114 follow-up) and
    /// repaints. The height is re-sized to the new line count so the container's split arithmetic
    /// (<see cref="DetailOtherTabView"/>) stays correct.</summary>
    public void Update(IReadOnlyList<TaskDetailFormatter.DetailLine> lines)
    {
        _lines = lines;
        Height = lines.Count;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        // The framework sets the view's normal attribute current before drawing content; capture it so
        // uncoloured runs (labels, dates, list names) render in it and so we can restore it afterwards
        // (SetAttribute mutates the driver's shared current attribute — see StatusBadgeListSource #34).
        var normal = GetCurrentAttribute();

        for (var row = 0; row < _lines.Count && row < Viewport.Height; row++)
        {
            Move(0, row);
            foreach (var run in _lines[row].Runs)
            {
                var attr = normal;
                if (run.Color is { } hex && StatusBadgeListSource.HeaderAttr(hex) is { } badge)
                    attr = badge;
                SetAttribute(attr);
                // Consecutive AddStr calls advance the cursor at the driver's own width and clip to the
                // view's viewport, so no hand-rolled column math (the #63 failure mode) is needed here.
                AddStr(run.Text);
            }
        }

        SetAttribute(normal);
        return true;
    }
}
