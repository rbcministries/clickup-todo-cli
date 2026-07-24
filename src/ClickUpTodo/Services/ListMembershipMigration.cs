using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Pure, terminal-free logic for the field/status hazard when a task's list membership changes via the
/// <b>add/remove of additional list locations</b> ("Tasks in Multiple Lists", #237) — the writes the
/// Quick Updates List pane (#242) and the New Task multi-list create (#241) perform. See
/// <c>docs/plans/list-change-field-status-migration.md</c> (#365) for the API grounding.
/// <para><b>Why this is the whole hazard.</b> A task in multiple lists always uses its <i>home</i>
/// List's status, so add/remove of an <i>additional</i> location never remaps or drops the status
/// (that is a <i>move</i>-operation concern, out of scope). Adding to a list keeps every value and
/// merely exposes that list's fields, so an add can't lose data. The one real hazard is <b>removing</b>
/// a list that <i>solely</i> defined a Custom Field the task has a value for — that value is no longer
/// surfaced. This class detects exactly that case so the caller can confirm before writing (and skip
/// the confirmation when nothing would be stranded).</para>
/// </summary>
public static class ListMembershipMigration
{
    /// <summary>
    /// The names of Custom Fields whose <b>set</b> values would be stranded by removing
    /// <paramref name="listToRemove"/> from a task — i.e. fields the task has a value for that are
    /// defined by the removed list but by <b>none</b> of <paramref name="remainingListIds"/> (the
    /// task's other lists). Space/Folder-level fields are returned by every list's
    /// <c>GET /list/{id}/field</c> (#249), so they appear under a remaining list and are inherently
    /// never flagged — only truly list-local values strand.
    /// <para>Errs to the <b>safe</b> side (over-report, never under-report): if the removed list's
    /// definitions are absent from <paramref name="perListDefinitions"/> (a failed preflight fetch),
    /// every set value is treated as potentially list-local and flagged unless a <i>known</i> remaining
    /// list clearly still defines it. A remaining list whose definitions are absent rescues nothing.</para>
    /// <para>Returns distinct field names in the task's field order; fields with a blank id (unmatchable)
    /// or no value are skipped.</para>
    /// </summary>
    /// <param name="taskFields">The task's custom fields with their values (<see cref="TaskDetail.CustomFields"/>).</param>
    /// <param name="listToRemove">The id of the additional list being removed.</param>
    /// <param name="perListDefinitions">Field definitions per list id, keyed by list id
    /// (<see cref="ClickUpClient.GetListCustomFieldsAsync"/>). A missing key means that list's
    /// definitions could not be fetched.</param>
    /// <param name="remainingListIds">The ids of the lists the task will still belong to after the
    /// remove (its full membership minus <paramref name="listToRemove"/>).</param>
    public static IReadOnlyList<string> StrandedFieldsOnRemove(
        IReadOnlyList<CustomFieldItem> taskFields,
        string listToRemove,
        IReadOnlyDictionary<string, IReadOnlyList<CustomFieldDefinition>> perListDefinitions,
        IReadOnlyCollection<string> remainingListIds)
    {
        if (taskFields is null || taskFields.Count == 0)
            return [];

        var removedKnown = perListDefinitions.TryGetValue(listToRemove, out var removedDefs);
        var removedIds = removedKnown ? DefinitionIds(removedDefs!) : null;

        // Union of field ids still reachable through a KNOWN remaining list (an unfetched remaining
        // list contributes nothing — it can't be relied on to rescue a value).
        var coveredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listId in remainingListIds)
            if (perListDefinitions.TryGetValue(listId, out var defs))
                coveredIds.UnionWith(DefinitionIds(defs));

        var stranded = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in taskFields)
        {
            if (string.IsNullOrWhiteSpace(field.Id) || !HasValue(field))
                continue;

            // Conservative when the removed list's definitions are unknown: assume it defined the field.
            var definedByRemoved = removedIds is null || removedIds.Contains(field.Id!);
            if (definedByRemoved && !coveredIds.Contains(field.Id!) && seen.Add(field.Id!))
                stranded.Add(field.Name);
        }
        return stranded;
    }

    /// <summary>
    /// Whether a custom field actually carries a set value (so an <b>unset</b> field can never strand).
    /// A JSON string counts only when non-whitespace; an array/object only when non-empty; numbers and
    /// booleans always count. A null/absent value never counts.
    /// </summary>
    public static bool HasValue(CustomFieldItem field)
    {
        if (field.Value is not { } value)
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true, // Number / True / False — a concrete value.
        };
    }

    private static HashSet<string> DefinitionIds(IReadOnlyList<CustomFieldDefinition> defs)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in defs)
            if (!string.IsNullOrWhiteSpace(d.Id))
                ids.Add(d.Id);
        return ids;
    }
}
