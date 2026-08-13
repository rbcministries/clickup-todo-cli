using System.Globalization;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>The gesture a highlighted Other-tab custom-field row supports (#587 §3), derived purely from the
/// field's type. The screen glue routes <c>Space</c>/<c>Enter</c> on this.</summary>
public enum CustomFieldActivationKind
{
    /// <summary>A <c>checkbox</c> field: <c>Space</c> (and <c>Enter</c>) toggle it in place.</summary>
    Checkbox,

    /// <summary>A text-like fillable field (<c>text</c> / <c>short_text</c> / <c>url</c> / <c>email</c> /
    /// <c>phone</c> / <c>number</c> / <c>currency</c> / <c>date</c>): <c>Enter</c> opens the value editor.</summary>
    TextEdit,

    /// <summary>An option field (<c>drop_down</c> / <c>labels</c>): fillable, but its option picker is a
    /// deferred follow-up (#587), so activation flashes a "edit in ClickUp" notice rather than opening a
    /// text editor (which would mis-serialise an option name into a field clear).</summary>
    OptionsDeferred,

    /// <summary>A computed / relationship / unknown type that cannot be edited here — inert.</summary>
    NotEditable,
}

/// <summary>
/// The Terminal.Gui-free policy behind the Other tab's per-type custom-field activation (#587 §3): given a
/// field's type it decides which gesture the row supports (<see cref="Classify"/>), and it derives the two
/// pure inputs the write path needs — the next state of a checkbox (<see cref="NextCheckboxState"/>) and the
/// round-trippable seed text for the value editor (<see cref="SeedText"/>). Pure (no Terminal.Gui, no I/O)
/// so the per-type routing and seeding rules are unit-testable, exactly as <see cref="ChecklistToggle"/> /
/// <see cref="ChecklistItemEdits"/> are for the Checklists tab.
/// <para>
/// The seed text is sourced from the field's <em>raw</em> <see cref="CustomFieldItem.Value"/> JSON, never
/// the display string (which <c>TaskDetailFormatter.CustomFieldValue</c> truncates and, for options, resolves
/// to labels) — prefilling from the display would silently drop the tail on save. The write itself reuses the
/// tested <see cref="CustomFieldValueSerializer"/>, so this class owns only the read/route half.
/// </para>
/// </summary>
public static class CustomFieldActivation
{
    /// <summary>Which gesture the row supports, from its field type. Non-fillable / computed / unknown
    /// types are <see cref="CustomFieldActivationKind.NotEditable"/>; option fields are
    /// <see cref="CustomFieldActivationKind.OptionsDeferred"/>; a checkbox toggles; everything else in the
    /// fillable set opens the text editor. Mirrors <see cref="CustomFieldTypes.IsFillable"/>'s taxonomy.</summary>
    public static CustomFieldActivationKind Classify(string? fieldType)
    {
        if (!CustomFieldTypes.IsFillable(fieldType))
            return CustomFieldActivationKind.NotEditable;
        return fieldType!.Trim().ToLowerInvariant() switch
        {
            "checkbox" => CustomFieldActivationKind.Checkbox,
            "drop_down" or "labels" => CustomFieldActivationKind.OptionsDeferred,
            _ => CustomFieldActivationKind.TextEdit,
        };
    }

    /// <summary>The bool a checkbox toggle should write: the negation of the field's current state. A
    /// null / unset / unrecognised value reads as unchecked, so the first toggle checks it.</summary>
    public static bool NextCheckboxState(JsonElement? current) => !IsChecked(current);

    /// <summary>Whether a checkbox custom field's raw value reads as checked. ClickUp has expressed the
    /// stored value as a JSON bool, a 0/1 number, or a "true"/"false"/"1"/"0" string across payloads, so all
    /// are accepted; null / empty / anything unrecognised is unchecked.</summary>
    public static bool IsChecked(JsonElement? current)
    {
        if (current is not { } v)
            return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => ParseBoolString(v.GetString()),
            _ => false,
        };
    }

    private static bool ParseBoolString(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s))
            return false;
        if (bool.TryParse(s, out var b))
            return b;
        return string.Equals(s, "1", StringComparison.Ordinal)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || s.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The round-trippable text to pre-fill the value editor with for a text-like field, from the
    /// raw <see cref="CustomFieldItem.Value"/> JSON. Empty when the field has no value. Dates are rendered
    /// <c>yyyy-MM-dd</c> in UTC (the form <see cref="TaskFieldInfo.TryParseNumeric"/> re-parses to the same
    /// epoch), numbers/currency as their invariant string, and text-like scalars verbatim. Only meaningful
    /// for <see cref="CustomFieldActivationKind.TextEdit"/> fields — the caller gates on
    /// <see cref="Classify"/>.</summary>
    public static string SeedText(CustomFieldItem field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Value is not { } v || v.ValueKind == JsonValueKind.Null)
            return "";
        return (field.Type?.Trim().ToLowerInvariant()) switch
        {
            "date" => SeedDate(v),
            "number" or "currency" => SeedNumber(v),
            _ => SeedScalar(v),
        };
    }

    // Renders a date field at day granularity (yyyy-MM-dd, UTC). An unedited save is protected by the
    // caller's ordinal dirty-check against this seed, so no silent corruption; but a user who edits a
    // time-bearing ClickUp date field saves it back at midnight (the time-of-day is dropped). Acceptable
    // within the §3 text-editor scope — a time-of-day picker would be its own follow-up.
    private static string SeedDate(JsonElement v)
    {
        long? ms = v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(
                v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
        return ms is { } m
            ? DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : SeedScalar(v);
    }

    private static string SeedNumber(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : v.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.String => v.GetString() ?? "",
        _ => SeedScalar(v),
    };

    private static string SeedScalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.TryGetInt64(out var l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : v.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };
}
