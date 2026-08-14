using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// The Terminal.Gui-free transform behind the Other tab's optimistic custom-field write (#587 §3): produce a
/// new immutable custom-field list with exactly one field's <see cref="CustomFieldItem.Value"/> replaced (or
/// cleared with <c>null</c>), leaving every other field untouched. Pure and order-preserving so the
/// optimistic-update and revert logic is unit-testable without a terminal, the analogue of
/// <see cref="ChecklistToggle.SetResolved"/> for the Checklists tab. The screen re-renders the Other tab from
/// the result and reverts by setting the field back to its prior value on a failed write.
/// </summary>
public static class CustomFieldValueEdit
{
    /// <summary>
    /// Returns a copy of <paramref name="fields"/> with field <paramref name="fieldId"/>'s
    /// <see cref="CustomFieldItem.Value"/> set to <paramref name="value"/> (<c>null</c> clears it). Every
    /// other field is carried through with the same data. A missing id is a value-identical no-op (so a stray
    /// call against a non-field row can't corrupt the list), and an empty/null list returns unchanged.
    /// </summary>
    public static IReadOnlyList<CustomFieldItem> SetValue(
        IReadOnlyList<CustomFieldItem> fields, string fieldId, JsonElement? value)
    {
        if (fields is null || fields.Count == 0)
            return fields ?? [];

        var result = new List<CustomFieldItem>(fields.Count);
        foreach (var field in fields)
            result.Add(string.Equals(field.Id, fieldId, StringComparison.Ordinal)
                ? field with { Value = value }
                : field);
        return result;
    }
}
