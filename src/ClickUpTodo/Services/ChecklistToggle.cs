using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// The Terminal.Gui-free transform behind the Checklists tab's <c>Space</c>-toggle (D, #457): produce a new
/// immutable checklist tree with exactly one item's <see cref="TaskChecklistItem.Resolved"/> flag set to a
/// target value, updating that item <em>wherever</em> it appears — the flat <see cref="TaskChecklist.Items"/>
/// list and any nested <see cref="TaskChecklistItem.Children"/> (ClickUp may express one item in both, and
/// <see cref="ChecklistArranger"/> collects it once). Pure and order-preserving so the optimistic-update and
/// revert logic is unit-testable without a terminal, exactly as <see cref="ChecklistArranger"/> is: the
/// screen re-projects the result through the arranger to get the new progress counts, and reverts by
/// toggling back to the prior value.
/// <para>The parent checklist's API <see cref="TaskChecklist.Resolved"/>/<see cref="TaskChecklist.Unresolved"/>
/// counts are intentionally left untouched — the arranger derives progress from the <em>projected items</em>,
/// not from those counts, so they never drive the display and would only drift if half-maintained here.</para>
/// </summary>
public static class ChecklistToggle
{
    /// <summary>
    /// Returns a copy of <paramref name="checklists"/> with the item <paramref name="itemId"/> in checklist
    /// <paramref name="checklistId"/> set to <paramref name="resolved"/>. Every other checklist and item is
    /// carried through with the same data. A missing checklist or item is a value-identical no-op (the
    /// projected rows are unchanged), so a stray call (e.g. against a header row) can't corrupt the tree.
    /// </summary>
    public static IReadOnlyList<TaskChecklist> SetResolved(
        IReadOnlyList<TaskChecklist> checklists, string checklistId, string itemId, bool resolved)
    {
        if (checklists is null || checklists.Count == 0)
            return checklists ?? [];

        var result = new List<TaskChecklist>(checklists.Count);
        foreach (var checklist in checklists)
        {
            result.Add(string.Equals(checklist.Id, checklistId, StringComparison.Ordinal)
                ? checklist with { Items = SetInItems(checklist.Items, itemId, resolved) }
                : checklist);
        }
        return result;
    }

    /// <summary>Rebuilds an item list, flipping the matching item's <see cref="TaskChecklistItem.Resolved"/>
    /// and recursing into every item's children (so a nested match, or the same item present flat and as a
    /// child, is updated consistently). Order is preserved.</summary>
    private static IReadOnlyList<TaskChecklistItem> SetInItems(
        IReadOnlyList<TaskChecklistItem> items, string itemId, bool resolved)
    {
        var result = new List<TaskChecklistItem>(items.Count);
        foreach (var item in items)
        {
            var newResolved = string.Equals(item.Id, itemId, StringComparison.Ordinal) ? resolved : item.Resolved;
            var newChildren = item.Children.Count > 0 ? SetInItems(item.Children, itemId, resolved) : item.Children;
            result.Add(item with { Resolved = newResolved, Children = newChildren });
        }
        return result;
    }
}
