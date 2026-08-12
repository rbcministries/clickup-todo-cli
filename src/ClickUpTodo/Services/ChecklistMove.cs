using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>The four checklist-item movement gestures (G, #569): reorder within a sibling list
/// (<see cref="Up"/>/<see cref="Down"/>) and change nesting depth (<see cref="Outdent"/>/<see cref="Indent"/>).</summary>
public enum ChecklistMoveKind
{
    Up,
    Down,
    Outdent,
    Indent,
}

/// <summary>
/// The single <c>PUT /checklist/{id}/checklist_item/{id}</c> write a legal move produces (G, #569): the new
/// <see cref="NewOrderIndex"/> slot among the destination siblings, plus how <c>parent</c> should change.
/// <list type="bullet">
/// <item><see cref="NewParentId"/> non-null — indent/outdent under that item.</item>
/// <item><see cref="ClearParent"/> true — outdent to top level (send an explicit <c>"parent": null</c>).</item>
/// <item>both absent (null / false) — a plain up/down move; <c>parent</c> is left untouched.</item>
/// </list>
/// Maps straight onto <see cref="ClickUpTodo.ClickUp.ClickUpClient.MoveChecklistItemAsync"/>'s parameters.
/// </summary>
public readonly record struct ChecklistMovePlan(
    string ChecklistId,
    string ItemId,
    string? NewParentId,
    double NewOrderIndex,
    bool ClearParent);

/// <summary>
/// The Terminal.Gui-free ordering logic behind the Checklists tab's reorder/reparent gestures (G, #569) —
/// the companion of <see cref="ChecklistArranger"/> (which renders the tree; this computes how a move
/// rearranges it). Pure, so the legality rules and the produced <c>orderindex</c>/<c>parent</c> are
/// unit-testable without a terminal.
/// <para>
/// It reconstructs the exact same sibling view the arranger displays — a child's
/// <see cref="TaskChecklistItem.ParentId"/> pointer wins, else the structural parent found via a populated
/// <see cref="TaskChecklistItem.Children"/> array; siblings ordered by <c>orderindex</c> (null last) then
/// ordinal id — then, for a gesture, returns the single write that lands the item in the target slot, or
/// <c>null</c> when the move is illegal (a boundary no-op, indenting the first item in a group, outdenting a
/// top-level item, or reparenting under the item's own descendant). An illegal move is rejected here with no
/// request; a legal one's <see cref="ChecklistMovePlan.NewOrderIndex"/> is a fractional index between the
/// destination neighbours' current ones, so writing only the moved item's <c>orderindex</c> reorders it
/// under the arranger's <c>(orderindex, id)</c> sort.
/// </para>
/// </summary>
public static class ChecklistMove
{
    /// <summary>Computes the write for moving item <paramref name="itemId"/> in checklist
    /// <paramref name="checklistId"/> per <paramref name="kind"/>, or <c>null</c> when the move is illegal /
    /// a boundary no-op / the item or checklist is missing.</summary>
    public static ChecklistMovePlan? Plan(
        IReadOnlyList<TaskChecklist>? checklists, string checklistId, string itemId, ChecklistMoveKind kind)
    {
        if (Layout.Build(checklists, checklistId) is not { } layout || !layout.Contains(itemId))
            return null;

        var parent = layout.ParentOf(itemId);
        var siblings = layout.SiblingsUnder(parent);
        var k = siblings.IndexOf(itemId);
        if (k < 0)
            return null; // defensive: the item wasn't in its own sibling list.

        return kind switch
        {
            ChecklistMoveKind.Up => PlanReorder(layout, checklistId, itemId, siblings, k, delta: -1),
            ChecklistMoveKind.Down => PlanReorder(layout, checklistId, itemId, siblings, k, delta: +1),
            ChecklistMoveKind.Indent => PlanIndent(layout, checklistId, itemId, siblings, k),
            ChecklistMoveKind.Outdent => PlanOutdent(layout, checklistId, itemId, parent),
            _ => null,
        };
    }

    /// <summary>Whether reparenting <paramref name="itemId"/> under <paramref name="newParentId"/> is legal:
    /// the target must exist in the same checklist and be neither the item itself nor one of its descendants
    /// (a cycle). A null <paramref name="newParentId"/> (to top level) is always a legal target.
    /// <para><b>Not called by <see cref="Plan"/> by design</b> — the four gestures only ever reparent under a
    /// preceding sibling (indent) or a grandparent (outdent), neither of which can be a descendant, so the
    /// cycle is structurally impossible. This predicate exists to pin the issue's explicit "no reparent under
    /// a descendant" rule under a direct unit test; a future gesture that reparents under an arbitrary target
    /// should route through it.</para></summary>
    public static bool IsLegalReparentTarget(
        IReadOnlyList<TaskChecklist>? checklists, string checklistId, string itemId, string? newParentId)
    {
        if (Layout.Build(checklists, checklistId) is not { } layout || !layout.Contains(itemId))
            return false;
        if (newParentId is null)
            return true; // to top level — never a cycle.
        if (!layout.Contains(newParentId) || string.Equals(newParentId, itemId, StringComparison.Ordinal))
            return false;
        return !layout.DescendantsAndSelf(itemId).Contains(newParentId);
    }

    // ── gesture computations ─────────────────────────────────────────────────────────────────────────

    /// <summary>Up/Down within the same sibling list: swap the item past the adjacent sibling (delta ∓1).
    /// A move off either end is illegal (null) — a boundary no-op that must not switch tabs.</summary>
    private static ChecklistMovePlan? PlanReorder(
        Layout layout, string checklistId, string itemId, IReadOnlyList<string> siblings, int k, int delta)
    {
        // Up (delta -1): land above sibling k-1, between k-2 and k-1. Down (+1): land below k+1, between k+1
        // and k+2. A move past the first/last sibling has no room and is rejected.
        var target = k + delta;
        if (target < 0 || target >= siblings.Count)
            return null;

        double? before, after;
        if (delta < 0)
        {
            before = k - 2 >= 0 ? layout.OrderIndexOf(siblings[k - 2]) : null;
            after = layout.OrderIndexOf(siblings[k - 1]);
        }
        else
        {
            before = layout.OrderIndexOf(siblings[k + 1]);
            after = k + 2 < siblings.Count ? layout.OrderIndexOf(siblings[k + 2]) : null;
        }

        // Parent unchanged (null NewParentId + ClearParent false ⇒ the facade sends no `parent`).
        return new ChecklistMovePlan(checklistId, itemId, NewParentId: null, Between(before, after), ClearParent: false);
    }

    /// <summary>Indent: reparent under the preceding sibling (k-1), appended as its last child. Indenting the
    /// first item in a group (k == 0) is illegal — there is no preceding sibling to nest under.</summary>
    private static ChecklistMovePlan? PlanIndent(
        Layout layout, string checklistId, string itemId, IReadOnlyList<string> siblings, int k)
    {
        if (k == 0)
            return null;

        var newParent = siblings[k - 1];
        var destChildren = layout.SiblingsUnder(newParent);
        // Append after the new parent's current last child (before = its orderindex, after = none).
        var before = destChildren.Count > 0 ? layout.OrderIndexOf(destChildren[^1]) : (double?)null;
        return new ChecklistMovePlan(checklistId, itemId, NewParentId: newParent, Between(before, after: null), ClearParent: false);
    }

    /// <summary>Outdent: reparent under the grandparent, placed just after the former parent among the
    /// grandparent's children. Outdenting a top-level item (no parent) is illegal. A null grandparent means
    /// the destination is the root list, so the write clears <c>parent</c> to null.</summary>
    private static ChecklistMovePlan? PlanOutdent(Layout layout, string checklistId, string itemId, string? parent)
    {
        if (parent is null)
            return null; // already at top level — nothing to outdent to.

        var grandparent = layout.ParentOf(parent);
        var destSiblings = layout.SiblingsUnder(grandparent);
        var j = destSiblings.IndexOf(parent);
        // Land immediately after the former parent: before = parent's orderindex, after = the next sibling's.
        var before = layout.OrderIndexOf(parent);
        double? after = j >= 0 && j + 1 < destSiblings.Count ? layout.OrderIndexOf(destSiblings[j + 1]) : null;

        var orderIndex = Between(before, after);
        return grandparent is null
            ? new ChecklistMovePlan(checklistId, itemId, NewParentId: null, orderIndex, ClearParent: true)
            : new ChecklistMovePlan(checklistId, itemId, NewParentId: grandparent, orderIndex, ClearParent: false);
    }

    /// <summary>A fractional index landing an item between two neighbours' current <c>orderindex</c> values:
    /// their midpoint when both are known, one step beyond the single known end, or 0 into an empty list.
    /// (Neighbours without a numeric orderindex are treated as "unknown"; ClickUp assigns numeric indices,
    /// and the server-confirmed checklist reconciles the exact order after the write either way.)</summary>
    private static double Between(double? before, double? after)
    {
        if (before is double b && after is double a)
            return (b + a) / 2.0;
        if (before is double b2)
            return b2 + 1.0;
        if (after is double a2)
            return a2 - 1.0;
        return 0.0;
    }

    /// <summary>
    /// The reconstructed sibling/parent view of one checklist, mirroring <see cref="ChecklistArranger"/>'s
    /// nesting rules (ParentId pointer wins, else structural <c>children</c>; siblings ordered by orderindex
    /// null-last then ordinal id). Built once per <see cref="Plan"/> call.
    /// </summary>
    private sealed class Layout
    {
        // A null parent (the root sibling list) can't key a Dictionary (notnull TKey), so root children live
        // under this sentinel — a control char that can never be a real ClickUp id.
        private const string RootKey = "\u0000";
        private static readonly List<string> EmptySiblings = [];

        private readonly Dictionary<string, TaskChecklistItem> _byId;
        private readonly Dictionary<string, string?> _effectiveParent;
        private readonly Dictionary<string, List<string>> _childrenByParent;

        private Layout(
            Dictionary<string, TaskChecklistItem> byId,
            Dictionary<string, string?> effectiveParent,
            Dictionary<string, List<string>> childrenByParent)
        {
            _byId = byId;
            _effectiveParent = effectiveParent;
            _childrenByParent = childrenByParent;
        }

        /// <summary>Builds the layout for checklist <paramref name="checklistId"/>, or null when the list
        /// (or the checklists collection) is absent.</summary>
        public static Layout? Build(IReadOnlyList<TaskChecklist>? checklists, string checklistId)
        {
            var checklist = checklists?.FirstOrDefault(c => string.Equals(c.Id, checklistId, StringComparison.Ordinal));
            if (checklist is null)
                return null;

            var byId = new Dictionary<string, TaskChecklistItem>(StringComparer.Ordinal);
            var order = new List<string>();
            var structuralParent = new Dictionary<string, string>(StringComparer.Ordinal);
            Collect(checklist.Items, parentId: null, byId, order, structuralParent);

            var effectiveParent = new Dictionary<string, string?>(StringComparer.Ordinal);
            var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var id in order)
            {
                var parent = EffectiveParent(id, byId, structuralParent);
                effectiveParent[id] = parent;
                var key = parent ?? RootKey;
                if (!childrenByParent.TryGetValue(key, out var siblings))
                    childrenByParent[key] = siblings = [];
                siblings.Add(id);
            }

            foreach (var siblings in childrenByParent.Values)
                siblings.Sort((a, b) => CompareOrder(byId[a].OrderIndex, byId[b].OrderIndex, a, b));

            return new Layout(byId, effectiveParent, childrenByParent);
        }

        public bool Contains(string id) => _byId.ContainsKey(id);

        public double? OrderIndexOf(string id) => _byId.TryGetValue(id, out var item) ? item.OrderIndex : null;

        /// <summary>The item's effective parent id, or null when it is a top-level (root) item.</summary>
        public string? ParentOf(string id) => _effectiveParent.GetValueOrDefault(id);

        /// <summary>The ordered child ids under <paramref name="parent"/> (null ⇒ the root list); empty when
        /// none.</summary>
        public List<string> SiblingsUnder(string? parent)
            => _childrenByParent.TryGetValue(parent ?? RootKey, out var siblings) ? siblings : EmptySiblings;

        /// <summary>The id set of <paramref name="id"/> plus every descendant, for the reparent cycle guard.</summary>
        public ISet<string> DescendantsAndSelf(string id)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(id);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!result.Add(current))
                    continue;
                foreach (var child in SiblingsUnder(current))
                    stack.Push(child);
            }
            return result;
        }

        // Mirrors ChecklistArranger.Collect: records every distinct item once (first occurrence wins), a
        // stable discovery order, and the structural parent found via a Children array.
        private static void Collect(
            IReadOnlyList<TaskChecklistItem> items,
            string? parentId,
            Dictionary<string, TaskChecklistItem> byId,
            List<string> order,
            Dictionary<string, string> structuralParent)
        {
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Id))
                    continue;
                var isNew = byId.TryAdd(item.Id, item);
                if (isNew)
                    order.Add(item.Id);
                if (parentId is not null)
                    structuralParent.TryAdd(item.Id, parentId);
                if (isNew && item.Children.Count > 0)
                    Collect(item.Children, item.Id, byId, order, structuralParent);
            }
        }

        // Mirrors ChecklistArranger.EffectiveParent: a ParentId pointing at an in-set item wins; else the
        // structural parent found via a Children array; else null (a root, incl. an out-of-set orphan pointer).
        private static string? EffectiveParent(
            string id,
            Dictionary<string, TaskChecklistItem> byId,
            Dictionary<string, string> structuralParent)
        {
            var parentId = byId[id].ParentId;
            if (!string.IsNullOrEmpty(parentId))
                return byId.ContainsKey(parentId) ? parentId : null;
            return structuralParent.TryGetValue(id, out var sp) ? sp : null;
        }

        // Mirrors ChecklistArranger.CompareOrder: present orderindex ascending, null last, ties on ordinal id.
        private static int CompareOrder(double? left, double? right, string leftId, string rightId)
        {
            if (left is not null && right is not null)
            {
                var byIndex = left.Value.CompareTo(right.Value);
                if (byIndex != 0)
                    return byIndex;
            }
            else if (left is null != (right is null))
            {
                return left is null ? 1 : -1;
            }

            return string.CompareOrdinal(leftId, rightId);
        }
    }
}
