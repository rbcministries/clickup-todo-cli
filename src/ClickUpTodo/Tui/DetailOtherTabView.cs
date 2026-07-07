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
    // The header height last applied to the subviews; -1 forces the first layout to apply. Guards
    // against re-setting the body Text (which resets scroll) on resizes that don't change the split.
    private int _appliedHeaderHeight = -1;

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
        var layout = DetailOtherTabLayout.Compute(_lines.Count, Viewport.Height);
        if (layout.HeaderHeight != _appliedHeaderHeight)
        {
            _header.Height = layout.HeaderHeight;
            _body.Y = layout.BodyY;
            _body.Text = layout.SpilledHeaderLines > 0 ? BuildSpilledBody(layout.SpilledHeaderLines) : _customFieldsBody;
            _appliedHeaderHeight = layout.HeaderHeight;
        }

        base.OnSubViewLayout(args);
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
