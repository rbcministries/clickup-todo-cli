using System.Globalization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>The two kinds of row the Checklists tab (C, #456) renders: a <see cref="Header"/> for a
/// checklist group (its name + progress) and an <see cref="Item"/> for one checklist item. The glue
/// styles headers distinctly — the way the main list's group headers are (<c>GroupHeaderPalette</c>) —
/// and may make them non-selectable.</summary>
public enum ChecklistRowKind
{
    Header,
    Item,
}

/// <summary>
/// One projected row of the Checklists tab (B, #455). Pure data: the arranger decides the ordering,
/// nesting <see cref="Depth"/>, progress and assignee text; the Terminal.Gui glue in <b>C</b> picks the
/// glyphs. <see cref="Depth"/> is an indent level (a value, not baked-in spaces) so the view controls the
/// indentation. <see cref="ChecklistId"/> and <see cref="ItemId"/> are carried on every row so the write
/// slices (<b>D</b>–<b>G</b>) never have to re-walk the tree from a selected index.
/// </summary>
public readonly record struct ChecklistRow(
    ChecklistRowKind Kind,
    int Depth,
    string Text,
    string ChecklistId,
    string? ItemId,
    bool Resolved,
    int ResolvedCount,
    int TotalCount,
    string? Assignee)
{
    /// <summary><c>true</c> for a checklist-group header row.</summary>
    public bool IsHeader => Kind == ChecklistRowKind.Header;
}

/// <summary>
/// The result of <see cref="ChecklistArranger.Project"/>: the flat, display-ordered <see cref="Rows"/>
/// plus the aggregate progress the tab title shows (e.g. <c>Checklists (5/12)</c>). <see cref="IsEmpty"/>
/// is the glue's cue to render an empty-state line instead of the list.
/// </summary>
public readonly record struct ChecklistProjection(
    IReadOnlyList<ChecklistRow> Rows,
    int ChecklistCount,
    int ResolvedCount,
    int TotalCount)
{
    /// <summary>The task has no checklists at all — the glue renders an empty-state line, not rows.</summary>
    public bool IsEmpty => ChecklistCount == 0;

    /// <summary>The shared "no checklists" result.</summary>
    public static readonly ChecklistProjection Empty = new([], 0, 0, 0);
}

/// <summary>
/// Flattens a task's native ClickUp checklists (groups + nested items, from
/// <see cref="TaskDetail.Checklists"/>) into one ordered, indented row list for the Checklists tab (C,
/// #456). Pure (no Terminal.Gui, no I/O) so the ordering/nesting/progress rules are unit-testable — the
/// same arranger shape as <see cref="TaskTreeArranger"/> / <see cref="SubtaskArranger"/>; <b>C</b> is thin
/// glue over it.
/// <para>
/// Checklists sort by <c>OrderIndex</c>, items by <c>OrderIndex</c> within their parent; a null/absent
/// order index sorts last and every tie breaks on ordinal <c>Id</c>, so a refresh never reorders. Nesting
/// is reconstructed from whichever representation the API used — a child's <see cref="TaskChecklistItem.ParentId"/>
/// pointer and/or a parent's populated <see cref="TaskChecklistItem.Children"/> array (#454) — collecting
/// each distinct item once so an item present in both is not doubled. An item whose parent id is missing
/// from the set surfaces at top level rather than vanishing, and a parent/child cycle terminates (a
/// visited guard plus a straggler pass) with every item still appearing exactly once.
/// </para>
/// </summary>
public static class ChecklistArranger
{
    /// <summary>Projects <paramref name="checklists"/> into display rows + aggregate progress. A null or
    /// empty input yields <see cref="ChecklistProjection.Empty"/>.</summary>
    public static ChecklistProjection Project(IReadOnlyList<TaskChecklist>? checklists)
    {
        if (checklists is null || checklists.Count == 0)
            return ChecklistProjection.Empty;

        var rows = new List<ChecklistRow>();
        var checklistCount = 0;
        var aggregateResolved = 0;
        var aggregateTotal = 0;

        var orderedChecklists = checklists
            .OrderBy(c => c, ChecklistOrder.Instance)
            .ToList();

        foreach (var checklist in orderedChecklists)
        {
            checklistCount++;

            // Collect every distinct item in the checklist once — walking the top level and, first, each
            // item's Children — so an item ClickUp expressed in both a Children array and a flat ParentId
            // pointer is counted a single time. byId is keyed by id (first occurrence wins); order keeps a
            // stable discovery order; structuralParent records the parent found via a Children array.
            var byId = new Dictionary<string, TaskChecklistItem>(StringComparer.Ordinal);
            var order = new List<string>();
            var structuralParent = new Dictionary<string, string>(StringComparer.Ordinal);
            Collect(checklist.Items, parentId: null, byId, order, structuralParent);

            // Effective parent per item → a children map + the roots. A ParentId that points at an in-set
            // item wins (reconstructs the pointer representation); otherwise the structural parent found via
            // Children; otherwise it is a root — including an orphan whose ParentId points outside the set.
            var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var roots = new List<string>();
            foreach (var id in order)
            {
                var parent = EffectiveParent(id, byId, structuralParent);
                if (parent is null)
                {
                    roots.Add(id);
                }
                else
                {
                    if (!childrenByParent.TryGetValue(parent, out var siblings))
                        childrenByParent[parent] = siblings = [];
                    siblings.Add(id);
                }
            }

            SortIds(roots, byId);
            foreach (var siblings in childrenByParent.Values)
                SortIds(siblings, byId);

            // Progress is computed from the items actually projected, so the header always agrees with what
            // is shown (rather than trusting the API's possibly-divergent resolved/unresolved counts).
            var resolved = 0;
            foreach (var id in order)
                if (byId[id].Resolved)
                    resolved++;
            var total = order.Count;
            aggregateResolved += resolved;
            aggregateTotal += total;

            rows.Add(new ChecklistRow(
                Kind: ChecklistRowKind.Header,
                Depth: 0,
                Text: checklist.Name,
                ChecklistId: checklist.Id,
                ItemId: null,
                Resolved: false,
                ResolvedCount: resolved,
                TotalCount: total,
                Assignee: null));

            var visited = new HashSet<string>(StringComparer.Ordinal);

            void Emit(string id, int depth)
            {
                if (!visited.Add(id)) // cycle guard: a parent cycle can't recurse forever.
                    return;
                var item = byId[id];
                rows.Add(new ChecklistRow(
                    Kind: ChecklistRowKind.Item,
                    Depth: depth,
                    Text: item.Name,
                    ChecklistId: checklist.Id,
                    ItemId: item.Id,
                    Resolved: item.Resolved,
                    ResolvedCount: 0,
                    TotalCount: 0,
                    Assignee: AssigneeText(item.Assignee)));
                if (childrenByParent.TryGetValue(id, out var kids))
                    foreach (var kid in kids)
                        Emit(kid, depth + 1);
            }

            foreach (var id in roots)
                Emit(id, 0);

            // Straggler safety net: an item a cycle left with no reachable root (only possible with a
            // parent cycle, which ClickUp doesn't produce) is surfaced flat at top level so every item
            // appears exactly once. Walk in sorted order for a deterministic result.
            var sortedAll = new List<string>(order);
            SortIds(sortedAll, byId);
            foreach (var id in sortedAll)
                if (!visited.Contains(id))
                    Emit(id, 0);
        }

        return new ChecklistProjection(rows, checklistCount, aggregateResolved, aggregateTotal);
    }

    /// <summary>Recursively records every distinct item, keyed by id (first occurrence wins), keeping a
    /// stable discovery <paramref name="order"/> and the parent found structurally via a
    /// <see cref="TaskChecklistItem.Children"/> array.</summary>
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
                continue; // the reader already drops id-less items; defensive against a hand-built list.
            if (!byId.TryAdd(item.Id, item))
                continue; // already seen (present in both a Children array and flat): first occurrence wins.
            order.Add(item.Id);
            if (parentId is not null)
                structuralParent[item.Id] = parentId;
            if (item.Children.Count > 0)
                Collect(item.Children, item.Id, byId, order, structuralParent);
        }
    }

    /// <summary>An item's effective parent: its <see cref="TaskChecklistItem.ParentId"/> when that points
    /// at an in-set item; else the structural parent found via a <see cref="TaskChecklistItem.Children"/>
    /// array; else null (a root — a genuine top-level item, or an orphan whose parent id is absent).</summary>
    private static string? EffectiveParent(
        string id,
        Dictionary<string, TaskChecklistItem> byId,
        Dictionary<string, string> structuralParent)
    {
        var parentId = byId[id].ParentId;
        if (!string.IsNullOrEmpty(parentId))
            return byId.ContainsKey(parentId) ? parentId : null; // orphan pointer → surface at top level.
        return structuralParent.TryGetValue(id, out var sp) ? sp : null;
    }

    /// <summary>The projection-decided assignee suffix: the assignee's display name, or null when the item
    /// is unassigned or only a bare (unresolved) id is known — resolving a bare id to a name is <b>G</b>'s
    /// job.</summary>
    private static string? AssigneeText(TaskAssignee? assignee)
        => assignee is not null && !string.IsNullOrWhiteSpace(assignee.Name) ? assignee.Name : null;

    /// <summary>Sorts ids in place by their item's <c>OrderIndex</c> (null last) then ordinal id, so a tie
    /// never reorders between refreshes.</summary>
    private static void SortIds(List<string> ids, Dictionary<string, TaskChecklistItem> byId)
        => ids.Sort((a, b) => CompareOrder(byId[a].OrderIndex, byId[b].OrderIndex, a, b));

    /// <summary>Order comparison shared by checklists and items: present <c>OrderIndex</c> ascending, a
    /// null index last, ties broken on ordinal id.</summary>
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
            return left is null ? 1 : -1; // a null order index sorts after any present one.
        }

        return string.CompareOrdinal(leftId, rightId);
    }

    /// <summary>Orders checklist groups by the same rule as items (order index, null last, id tie-break).</summary>
    private sealed class ChecklistOrder : IComparer<TaskChecklist>
    {
        public static readonly ChecklistOrder Instance = new();

        public int Compare(TaskChecklist? x, TaskChecklist? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            return CompareOrder(x.OrderIndex, y.OrderIndex, x.Id, y.Id);
        }
    }
}
