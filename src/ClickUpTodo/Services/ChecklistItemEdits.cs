using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// The Terminal.Gui-free transforms behind the Checklists tab's item CRUD (E, #458): rename, remove and
/// optimistic-insert an item in a task's checklist tree, plus the small pure helpers the screen needs
/// (name normalization, and diffing a create response to find the new item's id). Immutable and
/// order-preserving, exactly like <see cref="ChecklistToggle"/>, so the optimistic-update and
/// revert-on-failure logic is unit-testable without a terminal — the screen re-projects the result
/// through <see cref="ChecklistArranger"/> to get the new rows and progress counts.
/// <para>Every transform is a value-identical no-op when its target checklist/item is missing, so a stray
/// call (e.g. against a header row) can never corrupt the tree.</para>
/// </summary>
public static class ChecklistItemEdits
{
    /// <summary>The sentinel id an optimistically-inserted (not-yet-created) item carries until the create
    /// round-trips and the server-confirmed checklist (with the real id) replaces it. Shared so the screen
    /// and its tests agree on the placeholder identity.</summary>
    public const string ProvisionalItemId = "__pending_new_item__";

    /// <summary>Returns a copy of <paramref name="checklists"/> with item <paramref name="itemId"/> in
    /// checklist <paramref name="checklistId"/> renamed to <paramref name="name"/>, updating the item
    /// wherever it appears (the flat <see cref="TaskChecklist.Items"/> list and any nested
    /// <see cref="TaskChecklistItem.Children"/>). Every other checklist/item is carried through unchanged.</summary>
    public static IReadOnlyList<TaskChecklist> SetName(
        IReadOnlyList<TaskChecklist> checklists, string checklistId, string itemId, string name)
        => MapChecklist(checklists, checklistId, items => SetNameInItems(items, itemId, name));

    /// <summary>Returns a copy of <paramref name="checklists"/> with item <paramref name="itemId"/> in
    /// checklist <paramref name="checklistId"/> — and its whole subtree — removed. Descendants expressed
    /// either as nested <see cref="TaskChecklistItem.Children"/> or as flat items pointing back via
    /// <see cref="TaskChecklistItem.ParentId"/> are dropped too, so a deleted parent never leaves an
    /// orphan the arranger would resurface at top level (matching ClickUp's server-side cascade).</summary>
    public static IReadOnlyList<TaskChecklist> Remove(
        IReadOnlyList<TaskChecklist> checklists, string checklistId, string itemId)
        => MapChecklist(checklists, checklistId, items =>
        {
            var toRemove = DescendantsAndSelf(items, itemId);
            return FilterItems(items, toRemove);
        });

    /// <summary>Returns a copy of <paramref name="checklists"/> with <paramref name="item"/> appended to
    /// the top level of checklist <paramref name="checklistId"/>'s items — the optimistic provisional row
    /// for a create (a newly created item is top-level; reparenting is G, #460).</summary>
    public static IReadOnlyList<TaskChecklist> InsertProvisional(
        IReadOnlyList<TaskChecklist> checklists, string checklistId, TaskChecklistItem item)
        => MapChecklist(checklists, checklistId, items => [.. items, item]);

    /// <summary>Trims <paramref name="raw"/> and returns it, or <c>null</c> when it is null/empty/whitespace
    /// — the client-side reject that stops an empty create/rename before a request (mirroring the
    /// task-name rule in <c>NewTaskForm</c>).</summary>
    public static string? NormalizeName(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>The single id present in <paramref name="after"/> but not <paramref name="before"/> — the
    /// item a create added — so the screen can land the selection on the freshly-created row once the server
    /// checklist replaces the provisional one. Returns <c>null</c> when there is no such id, or more than
    /// one (an ambiguous/concurrent addition), so the caller falls back to identity anchoring rather than
    /// guessing. A concurrent removal alongside the single addition does not affect the result.</summary>
    public static string? NewItemId(TaskChecklist? before, TaskChecklist? after)
    {
        if (after is null)
            return null;
        ISet<string> beforeIds = before is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : AllItemIds(before.Items);
        string? found = null;
        foreach (var id in AllItemIds(after.Items))
        {
            if (beforeIds.Contains(id))
                continue;
            if (found is not null)
                return null; // more than one new id — ambiguous, don't guess.
            found = id;
        }
        return found;
    }

    // ── internals ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the checklist list, applying <paramref name="transform"/> to the matching
    /// checklist's items and carrying every other checklist through by reference.</summary>
    private static IReadOnlyList<TaskChecklist> MapChecklist(
        IReadOnlyList<TaskChecklist> checklists,
        string checklistId,
        Func<IReadOnlyList<TaskChecklistItem>, IReadOnlyList<TaskChecklistItem>> transform)
    {
        if (checklists is null || checklists.Count == 0)
            return checklists ?? [];

        var result = new List<TaskChecklist>(checklists.Count);
        foreach (var checklist in checklists)
        {
            result.Add(string.Equals(checklist.Id, checklistId, StringComparison.Ordinal)
                ? checklist with { Items = transform(checklist.Items) }
                : checklist);
        }
        return result;
    }

    /// <summary>Rebuilds an item list, renaming the matching item and recursing into every item's children
    /// (so a nested match — or the same item present flat and as a child — is updated consistently).</summary>
    private static IReadOnlyList<TaskChecklistItem> SetNameInItems(
        IReadOnlyList<TaskChecklistItem> items, string itemId, string name)
    {
        var result = new List<TaskChecklistItem>(items.Count);
        foreach (var item in items)
        {
            var newName = string.Equals(item.Id, itemId, StringComparison.Ordinal) ? name : item.Name;
            var newChildren = item.Children.Count > 0 ? SetNameInItems(item.Children, itemId, name) : item.Children;
            result.Add(item with { Name = newName, Children = newChildren });
        }
        return result;
    }

    /// <summary>Rebuilds an item list dropping any item whose id is in <paramref name="toRemove"/>, at
    /// every nesting level.</summary>
    private static IReadOnlyList<TaskChecklistItem> FilterItems(
        IReadOnlyList<TaskChecklistItem> items, ISet<string> toRemove)
    {
        var result = new List<TaskChecklistItem>(items.Count);
        foreach (var item in items)
        {
            if (toRemove.Contains(item.Id))
                continue;
            var newChildren = item.Children.Count > 0 ? FilterItems(item.Children, toRemove) : item.Children;
            result.Add(item with { Children = newChildren });
        }
        return result;
    }

    /// <summary>The id of <paramref name="itemId"/> plus every descendant reachable through nested
    /// <see cref="TaskChecklistItem.Children"/> or a <see cref="TaskChecklistItem.ParentId"/> pointer,
    /// within one checklist's item set.</summary>
    private static ISet<string> DescendantsAndSelf(IReadOnlyList<TaskChecklistItem> items, string itemId)
    {
        var byId = new Dictionary<string, TaskChecklistItem>(StringComparer.Ordinal);
        var structuralParent = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(items, null, byId, structuralParent);

        // Effective children per parent: a ParentId pointing at an in-set item wins; else the structural
        // parent found via a Children array. Mirrors ChecklistArranger's nesting reconstruction.
        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, item) in byId)
        {
            string? parent = !string.IsNullOrEmpty(item.ParentId) && byId.ContainsKey(item.ParentId)
                ? item.ParentId
                : structuralParent.GetValueOrDefault(id);
            if (parent is null)
                continue;
            if (!childrenByParent.TryGetValue(parent, out var kids))
                childrenByParent[parent] = kids = [];
            kids.Add(id);
        }

        var remove = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(itemId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!remove.Add(id)) // cycle/duplicate guard.
                continue;
            if (childrenByParent.TryGetValue(id, out var kids))
                foreach (var kid in kids)
                    stack.Push(kid);
        }
        return remove;
    }

    /// <summary>Records every distinct item (first occurrence wins), keyed by id, plus the structural
    /// parent found via a <see cref="TaskChecklistItem.Children"/> array — a trimmed-down mirror of
    /// <c>ChecklistArranger.Collect</c> sufficient for descendant reachability.</summary>
    private static void Collect(
        IReadOnlyList<TaskChecklistItem> items,
        string? parentId,
        Dictionary<string, TaskChecklistItem> byId,
        Dictionary<string, string> structuralParent)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Id))
                continue;
            var isNew = byId.TryAdd(item.Id, item);
            if (parentId is not null)
                structuralParent.TryAdd(item.Id, parentId);
            if (isNew && item.Children.Count > 0)
                Collect(item.Children, item.Id, byId, structuralParent);
        }
    }

    /// <summary>Every distinct item id in the tree (flat + nested), for the create-response diff.</summary>
    private static ISet<string> AllItemIds(IReadOnlyList<TaskChecklistItem> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IReadOnlyList<TaskChecklistItem> xs)
        {
            foreach (var x in xs)
            {
                if (!string.IsNullOrEmpty(x.Id))
                    ids.Add(x.Id);
                if (x.Children.Count > 0)
                    Walk(x.Children);
            }
        }
        Walk(items);
        return ids;
    }
}
