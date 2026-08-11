using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// The Terminal.Gui-free transforms behind the Checklists tab's <b>group</b> CRUD (F, #459): rename, remove
/// and optimistic-insert a whole checklist group in a task's checklist list, plus the create-response diff.
/// Immutable and order-preserving, exactly like <see cref="ChecklistItemEdits"/> (which handles the
/// <em>item</em> level), so the optimistic-update and revert-on-failure logic is unit-testable without a
/// terminal — the screen re-projects the result through <see cref="ChecklistArranger"/> to get the new rows
/// and progress counts. Name normalization is shared with the item level via
/// <see cref="ChecklistItemEdits.NormalizeName"/>.
/// <para>Every transform is a value-identical no-op when its target checklist is missing, so a stray call
/// (e.g. against an item row) can never corrupt the list.</para>
/// </summary>
public static class ChecklistGroupEdits
{
    /// <summary>The sentinel id an optimistically-inserted (not-yet-created) group carries until the create
    /// round-trips and the server-confirmed checklist (with the real id) replaces it. Shared so the screen
    /// and its tests agree on the placeholder identity.</summary>
    public const string ProvisionalChecklistId = "__pending_new_checklist__";

    /// <summary>Returns a copy of <paramref name="checklists"/> with the group <paramref name="checklistId"/>
    /// renamed to <paramref name="name"/>; every other group is carried through unchanged. A no-op when no
    /// group matches.</summary>
    public static IReadOnlyList<TaskChecklist> Rename(
        IReadOnlyList<TaskChecklist> checklists, string checklistId, string name)
    {
        if (checklists is null || checklists.Count == 0)
            return checklists ?? [];
        var result = new List<TaskChecklist>(checklists.Count);
        foreach (var checklist in checklists)
        {
            result.Add(string.Equals(checklist.Id, checklistId, StringComparison.Ordinal)
                ? checklist with { Name = name }
                : checklist);
        }
        return result;
    }

    /// <summary>Returns a copy of <paramref name="checklists"/> with the group
    /// <paramref name="checklistId"/> — and, by construction, all of its items — removed. A no-op when no
    /// group matches.</summary>
    public static IReadOnlyList<TaskChecklist> Remove(
        IReadOnlyList<TaskChecklist> checklists, string checklistId)
    {
        if (checklists is null || checklists.Count == 0)
            return checklists ?? [];
        return [.. checklists.Where(c => !string.Equals(c.Id, checklistId, StringComparison.Ordinal))];
    }

    /// <summary>Returns a copy of <paramref name="checklists"/> with <paramref name="provisional"/> appended
    /// — the optimistic provisional group for a create (a newly created group joins at the end; group
    /// reorder is G, #460). <paramref name="checklists"/> may be empty (the first-checklist path).</summary>
    public static IReadOnlyList<TaskChecklist> InsertProvisional(
        IReadOnlyList<TaskChecklist> checklists, TaskChecklist provisional)
        => [.. checklists ?? [], provisional];

    /// <summary>The single checklist-group id present in <paramref name="after"/> but not
    /// <paramref name="before"/> — the group a create added — so the screen can land the selection on the
    /// freshly-created group's header once the server list replaces the provisional one. Returns
    /// <c>null</c> when there is no such id, or more than one (an ambiguous/concurrent addition), so the
    /// caller falls back to identity anchoring rather than guessing. Mirrors
    /// <see cref="ChecklistItemEdits.NewItemId"/> at the group level.</summary>
    public static string? NewChecklistId(
        IReadOnlyList<TaskChecklist>? before, IReadOnlyList<TaskChecklist>? after)
    {
        if (after is null)
            return null;
        var beforeIds = new HashSet<string>(
            (before ?? []).Select(c => c.Id).Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
        string? found = null;
        foreach (var checklist in after)
        {
            var id = checklist.Id;
            if (string.IsNullOrEmpty(id) || beforeIds.Contains(id))
                continue;
            if (found is not null)
                return null; // more than one new id — ambiguous, don't guess.
            found = id;
        }
        return found;
    }
}
