namespace ClickUpTodo.ClickUp;

/// <summary>
/// Client-side required-custom-field enforcement for the New Task screen (#368 §3). The #249 spike
/// confirmed ClickUp's public v2 API surfaces a per-field <c>required</c> boolean
/// (<see cref="CustomFieldDefinition.Required"/>), so a screen can block Save until required fields have
/// values. Pure (definitions + filled ids in, missing names out) so it is unit-tested without a terminal;
/// the (future, #368 §2) screen calls it at Save and flashes the returned names.
/// </summary>
public static class CustomFieldRequiredValidator
{
    /// <summary>
    /// The <b>names</b> of the fields that must be filled before Save but aren't — i.e. every definition
    /// that is <see cref="CustomFieldDefinition.Required"/>, <b>fillable</b>
    /// (<see cref="CustomFieldTypes.IsFillable"/>), and whose id is not in <paramref name="filledFieldIds"/>.
    /// Read-only/computed required fields are excluded on purpose: the New Task screen can't fill them, so
    /// blocking on one would be an unsatisfiable Save. Order follows <paramref name="fields"/>; each field is
    /// reported at most once. A definition with a blank id can never be marked filled, so it would always
    /// report — such a field is skipped (it isn't writable anyway, mirroring the serializer).
    /// </summary>
    public static IReadOnlyList<string> MissingRequired(
        IEnumerable<CustomFieldDefinition> fields,
        ISet<string> filledFieldIds)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(filledFieldIds);

        var missing = new List<string>();
        foreach (var field in fields)
        {
            if (!field.Required || !CustomFieldTypes.IsFillable(field.Type))
                continue;
            if (string.IsNullOrWhiteSpace(field.Id))
                continue;
            if (filledFieldIds.Contains(field.Id))
                continue;

            missing.Add(string.IsNullOrWhiteSpace(field.Name) ? field.Id : field.Name);
        }

        return missing;
    }
}
