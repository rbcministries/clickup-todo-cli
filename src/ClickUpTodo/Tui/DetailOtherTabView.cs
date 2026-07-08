using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// TextView is marked obsolete in Terminal.Gui 2.4 in favour of a not-yet-shipped EditorView; it
// remains the supported v2 read-only text pane the rest of the detail view uses (see TaskDetailScreen).
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// The task detail <b>Other</b> tab's content: a fixed, coloured header (<see cref="DetailAttributesView"/>)
/// above a scrollable, word-wrapped custom-fields <see cref="TextView"/>. This is the (CI-untestable)
/// Terminal.Gui glue; the split arithmetic lives in the unit-tested <see cref="DetailOtherTabLayout"/>.
/// <para>
/// On a very short window (issue #81) the layout is re-applied from the container's own height in
/// <see cref="OnSubViewLayout"/>: the header is capped so the body keeps a minimum scrollable region,
/// and any clipped trailing header lines are rendered as plain text at the top of the scrollable body
/// so they stay reachable. The header stays a fixed, non-scrollable view — no hand-rolled vertical
/// scroll (the #63 risk PR #80 avoided).
/// </para>
/// </summary>
public sealed class DetailOtherTabView : View
{
    private readonly IReadOnlyList<TaskDetailFormatter.DetailLine> _lines;
    private readonly string _customFieldsBody;
    private readonly DetailAttributesView _header;
    private readonly TextView _body;
    // The split currently applied to the subviews (null until the first layout). Guards against
    // re-setting the body Text — which resets its scroll — on resizes that don't change the split.
    private DetailOtherTabLayout.Layout? _applied;
    // The container height the current split was computed for; drives the draw-time self-correction
    // below. int.MinValue forces the first draw to reconcile.
    private int _laidOutForHeight = int.MinValue;

    public DetailOtherTabView(IReadOnlyList<TaskDetailFormatter.DetailLine> lines, string customFieldsBody)
    {
        _lines = lines;
        _customFieldsBody = customFieldsBody;

        Title = "Other";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        // CanFocus so the container is in the focus chain — its scrollable body (below) takes focus via
        // SetFocus; the coloured header stays non-focusable.
        CanFocus = true;

        _header = new DetailAttributesView(lines)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = lines.Count,
        };
        _body = new TextView
        {
            X = 0,
            Y = lines.Count + DetailOtherTabLayout.GapRows,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = customFieldsBody,
            ReadOnly = true,
            WordWrap = true,
        };
        Add(_header, _body);
    }

    /// <summary>The focusable, scrollable view the detail screen focuses and routes ↑/↓/PgUp/PgDn to.</summary>
    public TextView ScrollTarget => _body;

    /// <inheritdoc/>
    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        Apply(Viewport.Height);
        base.OnSubViewLayout(args);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        // A newly-shown tab's content can be laid out before this container has its final height (e.g.
        // while a sibling tab is the visible one), so the split can be computed against a stale size and
        // never re-run on the tab switch alone — leaving the header uncapped and the spillover missing
        // until the next keypress. By the time we draw, Viewport holds the real height; if it differs
        // from what we last laid out for, schedule a relayout so the adaptive split applies on its own.
        if (Viewport.Height != _laidOutForHeight)
            SetNeedsLayout();
        return base.OnDrawingContent(context);
    }

    private void Apply(int availableHeight)
    {
        _laidOutForHeight = availableHeight;
        var layout = DetailOtherTabLayout.Compute(_lines.Count, availableHeight);
        if (layout == _applied)
            return;

        _applied = layout;
        _header.Height = layout.HeaderHeight;
        _body.Y = layout.BodyY;
        _body.Text = layout.SpilledHeaderLines > 0 ? BuildSpilledBody(layout.SpilledHeaderLines) : _customFieldsBody;
    }

    // The clipped trailing header lines (rendered uncoloured — they are the date lines except on a
    // pathologically tiny window), a blank separator, then the custom-fields body.
    private string BuildSpilledBody(int spilledLines)
    {
        var start = _lines.Count - spilledLines;
        var spilled = string.Join('\n', _lines.Skip(start).Select(l => l.Text));
        return spilled + "\n\n" + _customFieldsBody;
    }
}
