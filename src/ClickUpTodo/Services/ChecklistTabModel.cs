using System.Text;

namespace ClickUpTodo.Services;

/// <summary>
/// The Terminal.Gui-free half of the Checklists tab (C, #456): the display text for each projected
/// <see cref="ChecklistRow"/>, the tab title, the empty-state line, plus the refresh-safe
/// <see cref="Signature"/> / <see cref="AnchorSelection"/> the glue uses to rebuild the tab only when its
/// content moved and to re-anchor the selection by item identity across that rebuild. Pure (no
/// Terminal.Gui, no I/O) so all of it is unit-testable — the same pure-glue split
/// <see cref="ChecklistArranger"/>, <see cref="DetailScrollModel"/> and <c>DetailTabNav</c> use; the
/// glyphs, indentation width and colouring are the model's, and <c>TaskDetailScreen</c> is thin glue.
/// </summary>
public static class ChecklistTabModel
{
    /// <summary>Spaces per nesting level for an item row's indentation.</summary>
    public const int IndentPerLevel = 2;

    private const string ResolvedGlyph = "[x] ";
    private const string UnresolvedGlyph = "[ ] ";

    // A unit-separator (U+001F) delimits Signature fields so no id/text/assignee value can forge a
    // field boundary and collide two structurally-different projections.
    private const char FieldSeparator = '';

    /// <summary>The single explanatory row a task with no checklists shows, so the tab is never blank.</summary>
    public const string EmptyStateText = "No checklists on this task.";

    /// <summary>The tab header text carrying aggregate progress, e.g. <c>Checklists (5/12)</c>; a task
    /// with no items (or no checklists) shows a bare <c>Checklists</c>.</summary>
    public static string TabTitle(ChecklistProjection projection)
        => projection.TotalCount > 0
            ? $"Checklists ({projection.ResolvedCount}/{projection.TotalCount})"
            : "Checklists";

    /// <summary>The display string for one projected row: a checklist-group header renders as
    /// <c>{name}  (resolved/total)</c>; an item renders as <c>{indent}{glyph}{name}{ — assignee}</c>,
    /// where the indent is <see cref="IndentPerLevel"/> spaces per <see cref="ChecklistRow.Depth"/> and
    /// the glyph is <c>[x] </c>/<c>[ ] </c>. The glue draws headers with a distinct attribute
    /// (<c>StatusBadgeListSource</c> header rows) rather than baking styling into the text.</summary>
    public static string RenderRow(ChecklistRow row)
    {
        if (row.IsHeader)
            return $"{row.Text}  ({row.ResolvedCount}/{row.TotalCount})";

        var sb = new StringBuilder();
        sb.Append(' ', Math.Max(0, row.Depth) * IndentPerLevel);
        sb.Append(row.Resolved ? ResolvedGlyph : UnresolvedGlyph);
        sb.Append(row.Text);
        if (!string.IsNullOrWhiteSpace(row.Assignee))
            sb.Append(" — ").Append(row.Assignee);
        return sb.ToString();
    }

    /// <summary>A cheap content fingerprint of the whole projection (every row's kind, depth, ids,
    /// resolved state, counts, text and assignee, plus the aggregate progress) so a refresh only rebuilds
    /// the tab when its rendered content actually moved — the <c>OtherTabSignature</c> discipline applied
    /// to checklist rows. A collision would only skip a cosmetic rebuild.</summary>
    public static string Signature(ChecklistProjection projection)
    {
        if (projection.IsEmpty)
            return "empty";

        var sb = new StringBuilder();
        sb.Append(projection.ResolvedCount).Append('/').Append(projection.TotalCount).Append('\n');
        foreach (var row in projection.Rows)
        {
            sb.Append(row.IsHeader ? 'H' : 'I').Append(FieldSeparator)
              .Append(row.Depth).Append(FieldSeparator)
              .Append(row.ChecklistId).Append(FieldSeparator)
              .Append(row.ItemId).Append(FieldSeparator)
              .Append(row.Resolved ? '1' : '0').Append(FieldSeparator)
              .Append(row.ResolvedCount).Append('/').Append(row.TotalCount).Append(FieldSeparator)
              .Append(row.Assignee).Append(FieldSeparator)
              .Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The selected index to restore after a content-changing rebuild: keeps the cursor on the same row
    /// <em>identity</em> (its kind + checklist id + item id) when that row still exists in
    /// <paramref name="newRows"/>, otherwise clamps the old index into the new range — so a refresh that
    /// added/removed/reordered rows never drops the selection onto an unrelated row, and never off the
    /// end. Returns 0 when there are no new rows to select.
    /// </summary>
    public static int AnchorSelection(
        IReadOnlyList<ChecklistRow> oldRows, int oldIndex, IReadOnlyList<ChecklistRow> newRows)
    {
        if (newRows.Count == 0)
            return 0;

        if (oldIndex >= 0 && oldIndex < oldRows.Count)
        {
            var anchor = oldRows[oldIndex];
            for (var i = 0; i < newRows.Count; i++)
                if (SameRow(newRows[i], anchor))
                    return i;
        }

        return Math.Clamp(oldIndex, 0, newRows.Count - 1);
    }

    /// <summary>
    /// The index to select after deleting the item at <paramref name="deletedIndex"/> (E, #458): prefer
    /// the next item row in the same checklist (the sibling/next row immediately after the deleted item's
    /// subtree), else the previous item row in the same checklist, else that checklist's header row — so a
    /// delete lands the cursor deterministically on a neighbour rather than jumping checklists or off the
    /// end. Falls back to <see cref="AnchorSelection"/>'s clamp when none of those survive in
    /// <paramref name="newRows"/> (or the index is out of range). Pure and unit-tested.
    /// </summary>
    public static int SelectAfterDelete(
        IReadOnlyList<ChecklistRow> oldRows, int deletedIndex, IReadOnlyList<ChecklistRow> newRows)
    {
        if (newRows.Count == 0)
            return 0;
        if (deletedIndex < 0 || deletedIndex >= oldRows.Count)
            return AnchorSelection(oldRows, deletedIndex, newRows);

        var deleted = oldRows[deletedIndex];
        var checklistId = deleted.ChecklistId;

        // The deleted subtree is contiguous: the deleted row plus the following rows deeper than it.
        var subtreeEnd = deletedIndex + 1;
        while (subtreeEnd < oldRows.Count && oldRows[subtreeEnd].Depth > deleted.Depth)
            subtreeEnd++;

        // 1) Next item row in the same checklist, immediately after the subtree.
        if (subtreeEnd < oldRows.Count
            && !oldRows[subtreeEnd].IsHeader
            && string.Equals(oldRows[subtreeEnd].ChecklistId, checklistId, StringComparison.Ordinal))
        {
            var idx = IndexOfRow(newRows, oldRows[subtreeEnd]);
            if (idx >= 0)
                return idx;
        }

        // 2) Previous item row in the same checklist (stop at this checklist's header).
        for (var i = deletedIndex - 1; i >= 0; i--)
        {
            var row = oldRows[i];
            if (!string.Equals(row.ChecklistId, checklistId, StringComparison.Ordinal))
                continue;
            if (row.IsHeader)
                break; // reached our header without a prior item — fall through to it.
            var idx = IndexOfRow(newRows, row);
            if (idx >= 0)
                return idx;
        }

        // 3) The checklist's header row.
        for (var i = 0; i < oldRows.Count; i++)
        {
            if (oldRows[i].IsHeader
                && string.Equals(oldRows[i].ChecklistId, checklistId, StringComparison.Ordinal))
            {
                var idx = IndexOfRow(newRows, oldRows[i]);
                if (idx >= 0)
                    return idx;
            }
        }

        // 4) Nothing matched — clamp the old index into the new range.
        return AnchorSelection(oldRows, deletedIndex, newRows);
    }

    /// <summary>The index of the first row in <paramref name="rows"/> with the same identity as
    /// <paramref name="anchor"/> (<see cref="SameRow"/>), or -1.</summary>
    private static int IndexOfRow(IReadOnlyList<ChecklistRow> rows, ChecklistRow anchor)
    {
        for (var i = 0; i < rows.Count; i++)
            if (SameRow(rows[i], anchor))
                return i;
        return -1;
    }

    /// <summary>Row identity for selection anchoring: same kind, same checklist, same item (an ordinal
    /// id compare; a header carries a null item id, so two headers of the same checklist match).</summary>
    private static bool SameRow(ChecklistRow a, ChecklistRow b)
        => a.Kind == b.Kind
           && string.Equals(a.ChecklistId, b.ChecklistId, StringComparison.Ordinal)
           && string.Equals(a.ItemId, b.ItemId, StringComparison.Ordinal);
}
