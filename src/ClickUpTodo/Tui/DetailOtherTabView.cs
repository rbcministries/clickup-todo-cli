using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace ClickUpTodo.Tui;

/// <summary>
/// The task detail <b>Other</b> tab's content: a fixed, coloured header (<see cref="DetailAttributesView"/>)
/// above a focusable, navigable custom-fields <see cref="ListView"/> (#587 §2). This is the (CI-untestable)
/// Terminal.Gui glue; the split arithmetic lives in the unit-tested <see cref="DetailOtherTabLayout"/> and
/// the row model in the unit-tested <see cref="CustomFieldOtherTabArranger"/>.
/// <para>
/// The body was a word-wrapped read-only <c>TextView</c> blob; #587 §2 turns it into a per-field row
/// <see cref="ListView"/> — ↑/↓ move a selection over the rows (routed through the detail screen's
/// <c>MoveActiveTab</c>, exactly like the Task&#160;Tree / Checklists tabs), PgUp/PgDn page, and the
/// coloured header attributes stay non-selectable scenery. It is still a <b>single</b> focus target (the
/// body <see cref="ListView"/> replaces the body <c>TextView</c> one-for-one), so the #3 invariant (no
/// second focusable pane) holds. §3 (per-type activation) builds on this and is a separate slice.
/// </para>
/// <para>
/// On a very short window (issue #81) the layout is re-applied from the container's own height in
/// <see cref="OnSubViewLayout"/>: the header is capped so the body keeps a minimum region, and any clipped
/// trailing header lines become non-selectable <see cref="CustomFieldOtherRowKind.Spill"/> rows at the top
/// of the body so they stay reachable. The header stays a fixed, non-scrollable view — no hand-rolled
/// vertical scroll (the #63 risk PR #80 avoided).
/// </para>
/// </summary>
public sealed class DetailOtherTabView : View
{
    private IReadOnlyList<TaskDetailFormatter.DetailLine> _lines;
    private IReadOnlyList<CustomFieldItem> _fields;
    private readonly DetailAttributesView _header;
    private readonly ListView _body;
    // The projected rows currently backing the body ListView, parallel to its Source — used to re-anchor
    // the selection (by field id) across a re-render (a #81 resize or a #114 refresh), mirroring the
    // Checklists tab's AnchorSelection.
    private IReadOnlyList<CustomFieldOtherRow> _rows = [];
    // The split currently applied to the subviews (null until the first layout). Guards against
    // re-projecting the body — which resets its selection — on resizes that don't change the split.
    private DetailOtherTabLayout.Layout? _applied;
    // The container height the current split was computed for; drives the draw-time self-correction below.
    // int.MinValue forces the first draw to reconcile.
    private int _laidOutForHeight = int.MinValue;

    public DetailOtherTabView(IReadOnlyList<TaskDetailFormatter.DetailLine> lines,
        IReadOnlyList<CustomFieldItem>? fields)
    {
        _lines = lines;
        _fields = fields ?? [];

        Title = "Other";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        // CanFocus so the container is in the focus chain — its body ListView (below) takes focus via
        // SetFocus; the coloured header stays non-focusable.
        CanFocus = true;

        _header = new DetailAttributesView(lines)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = lines.Count,
        };
        _body = new ListView
        {
            X = 0,
            Y = lines.Count + DetailOtherTabLayout.GapRows,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        Add(_header, _body);
        // Seed the rows with no spill; Apply() re-projects with the real #81 spill on the first layout.
        RenderRows(spilledHeaderLines: 0);
    }

    /// <summary>The focusable body the detail screen focuses and routes ↑/↓/PgUp/PgDn to — now a
    /// <see cref="ListView"/> (#587 §2). <c>MoveActiveTab</c>'s <c>ListView</c> branch moves its selection,
    /// exactly as it does for the Task&#160;Tree / Checklists tabs, so no per-key wiring changes.</summary>
    public ListView ScrollTarget => _body;

    /// <summary>Swaps in fresh header lines and custom-field values (a detail-view refresh, #114 follow-up)
    /// and forces the adaptive split to recompute so the body rows and header height are re-applied. Callers
    /// only invoke this when the content actually changed, so re-projecting (which the selection re-anchor
    /// keeps steady where the field survives) is bounded to real updates.</summary>
    public void Update(IReadOnlyList<TaskDetailFormatter.DetailLine> lines,
        IReadOnlyList<CustomFieldItem>? fields)
    {
        _lines = lines;
        _fields = fields ?? [];
        _header.Update(lines);
        // Re-render now (selection re-anchored by field id) so a refresh reflects immediately; force
        // Apply() to recompute the split so the header height / #81 spill re-apply on the next layout.
        RenderRows(_applied?.SpilledHeaderLines ?? 0);
        _applied = null;
        _laidOutForHeight = int.MinValue;
        SetNeedsLayout();
    }

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
        RenderRows(layout.SpilledHeaderLines);
    }

    // Projects the current header spill + field values into the body's row list and re-anchors the
    // selection. The #81 clipped trailing header lines become leading non-selectable Spill rows (the
    // row-model analogue of the old BuildSpilledBody plain-text prefix); everything else is a field /
    // heading / empty-state row from the pure arranger.
    private void RenderRows(int spilledHeaderLines)
    {
        var spill = spilledHeaderLines > 0
            ? (IReadOnlyList<string>)_lines.Skip(_lines.Count - spilledHeaderLines).Select(l => l.Text).ToList()
            : [];
        var projection = CustomFieldOtherTabArranger.Project(spill, _fields);

        // Re-anchor the selection across the rebuild: keep the same field where it survives, else the first
        // selectable row (mirrors the Checklists tab). SetSource resets SelectedItem, so capture first.
        var previousFieldId = SelectedFieldId();
        _rows = projection.Rows;
        _body.SetSource(new ObservableCollection<string>(projection.Rows.Select(r => r.Text).ToList()));

        var target = ReanchorIndex(previousFieldId, projection);
        var count = _body.Source?.Count ?? 0;
        if (count > 0 && target >= 0 && target < count)
            _body.SelectedItem = target;
    }

    /// <summary>The field id under the current body selection, or null when the selection is on a
    /// non-field row (spill / heading / empty state) — the anchor a re-render preserves.</summary>
    private string? SelectedFieldId()
        => _body.SelectedItem is int i && i >= 0 && i < _rows.Count ? _rows[i].FieldId : null;

    private static int ReanchorIndex(string? fieldId, CustomFieldOtherProjection projection)
    {
        if (fieldId is not null)
            for (var i = 0; i < projection.Rows.Count; i++)
                if (string.Equals(projection.Rows[i].FieldId, fieldId, StringComparison.Ordinal))
                    return i;
        var first = projection.FirstSelectableIndex();
        return first >= 0 ? first : 0;
    }
}
