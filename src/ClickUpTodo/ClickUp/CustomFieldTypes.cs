namespace ClickUpTodo.ClickUp;

/// <summary>
/// The ClickUp custom-field type taxonomy for the <b>write</b> path (#368): which field types the New Task
/// screen can fill and how each maps to a create-payload value. Mirrors the read-side dispatch in
/// <c>TaskDetailFormatter.CustomFieldValue</c> (#35) so the two stay in lock-step. Shared by
/// <see cref="CustomFieldValueSerializer"/> (skip logic) and <see cref="CustomFieldRequiredValidator"/>
/// (a required field the UI can't fill must never create an unsatisfiable Save block).
/// </summary>
public static class CustomFieldTypes
{
    /// <summary>The field types this app can fill from the New Task screen — the common scalar/choice
    /// types. Compared case-insensitively via <see cref="IsFillable"/>. Everything else (computed:
    /// <c>formula</c>/<c>rollup</c>/progress; baselines <c>multi_key</c>/<c>signature</c>; and the
    /// relationship/rich pickers <c>users</c>/<c>tasks</c>/<c>location</c>/<c>emoji</c>) is deferred — a
    /// value-filler skips it, and required-enforcement never blocks on it.</summary>
    public static readonly IReadOnlyList<string> Fillable =
    [
        "text", "short_text", "url", "email", "phone",
        "number", "currency", "checkbox", "date", "drop_down", "labels",
    ];

    private static readonly HashSet<string> FillableSet =
        new(Fillable, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="type"/> is a field type the New Task screen can fill (a member of
    /// <see cref="Fillable"/>). Null/blank/unknown types are not fillable.</summary>
    public static bool IsFillable(string? type)
        => !string.IsNullOrWhiteSpace(type) && FillableSet.Contains(type.Trim());
}
