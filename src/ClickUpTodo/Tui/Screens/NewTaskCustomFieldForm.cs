using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The result of collecting the New Task screen's custom-field widgets (#395): the values to attach to
/// the create request, the names of required fields still unfilled, and any per-field validation errors.
/// <see cref="IsValid"/> gates Save.
/// </summary>
public sealed record NewTaskCustomFieldResult(
    IReadOnlyList<CustomFieldValue> Values,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> Errors)
{
    /// <summary>Save may proceed only when every entered value parsed (<see cref="Errors"/> empty) and
    /// every required fillable field has a value (<see cref="MissingRequired"/> empty).</summary>
    public bool IsValid => Errors.Count == 0 && MissingRequired.Count == 0;
}

/// <summary>
/// Pure aggregation for the New Task screen's custom-field block (#395 §2), factored out of the
/// Terminal.Gui glue so the collect/validate decision is unit-tested without a terminal (mirrors
/// <see cref="NewTaskForm"/>). It composes the two #368 foundation helpers — the per-type
/// <see cref="CustomFieldValueSerializer"/> and the <see cref="CustomFieldRequiredValidator"/> — into one
/// screen-ready call: given a list's field definitions and the widget inputs keyed by field id, it
/// produces the create-payload values, the still-missing required field names, and any parse errors.
/// </summary>
public static class NewTaskCustomFieldForm
{
    private static readonly CustomFieldEntry Empty = new();

    /// <summary>
    /// Builds each field's value from its collected <see cref="CustomFieldEntry"/> (an absent entry is
    /// treated as empty), in <paramref name="fields"/> order: a produced value is added to
    /// <see cref="NewTaskCustomFieldResult.Values"/> and its id counts as <em>filled</em>; a parse error
    /// is added to <see cref="NewTaskCustomFieldResult.Errors"/>; a skip (blank / not fillable) is
    /// ignored. <see cref="NewTaskCustomFieldResult.MissingRequired"/> then reports every required,
    /// fillable field whose id wasn't filled — so a required field left blank (or with an invalid value,
    /// which also doesn't fill it) blocks Save. The value serializer and required validator already skip
    /// non-fillable / computed / relationship types, so those never contribute values, errors, or blocks.
    /// </summary>
    public static NewTaskCustomFieldResult Collect(
        IReadOnlyList<CustomFieldDefinition> fields,
        IReadOnlyDictionary<string, CustomFieldEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(entries);

        var values = new List<CustomFieldValue>();
        var errors = new List<string>();
        var filled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var entry = field.Id is not null && entries.TryGetValue(field.Id, out var e) ? e : Empty;
            var result = CustomFieldValueSerializer.Build(field, entry);
            switch (result.Outcome)
            {
                case CustomFieldWriteOutcome.Value when result.Value is { } value:
                    values.Add(value);
                    filled.Add(field.Id!);
                    break;
                case CustomFieldWriteOutcome.Error when result.Error is { } message:
                    errors.Add(message);
                    break;
            }
        }

        var missing = CustomFieldRequiredValidator.MissingRequired(fields, filled);
        return new NewTaskCustomFieldResult(values, missing, errors);
    }
}
