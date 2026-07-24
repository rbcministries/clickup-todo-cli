using System.Globalization;
using System.Text.Json;
using ClickUpTodo.Services;

namespace ClickUpTodo.ClickUp;

/// <summary>A widget-agnostic entry for one custom field on the New Task screen (#368): the raw input the
/// (future, #368 §2) per-type widget collected, normalised enough that the pure
/// <see cref="CustomFieldValueSerializer"/> can shape it into a create payload without knowing about
/// Terminal.Gui. Text fields (text/number/date/checkbox-typed) fill <see cref="Text"/>; a checkbox fills
/// <see cref="Checked"/>; a drop-down fills the first of <see cref="SelectedOptionIds"/> and a labels
/// multi-select fills all of them.</summary>
public sealed record CustomFieldEntry
{
    /// <summary>Raw text for text-ish / number / date / (fallback) checkbox inputs. Null/blank ⇒ nothing entered.</summary>
    public string? Text { get; init; }

    /// <summary>Explicit checkbox state (preferred over <see cref="Text"/> for a <c>checkbox</c> field).</summary>
    public bool? Checked { get; init; }

    /// <summary>Selected option ids for <c>drop_down</c> (first is used) and <c>labels</c> (all are used).</summary>
    public IReadOnlyList<string> SelectedOptionIds { get; init; } = [];
}

/// <summary>What building one field's value produced: nothing to send, a value, or a validation error.</summary>
public enum CustomFieldWriteOutcome
{
    /// <summary>Nothing entered, or the field type isn't fillable here — send no value for it.</summary>
    Skip,

    /// <summary>A value to include in the create's <c>custom_fields</c> array.</summary>
    Value,

    /// <summary>The entered value is invalid for the field type (e.g. a non-numeric number) — block Save.</summary>
    Error,
}

/// <summary>The result of <see cref="CustomFieldValueSerializer.Build"/>: an <see cref="Outcome"/> with a
/// <see cref="Value"/> when it's <see cref="CustomFieldWriteOutcome.Value"/>, or an <see cref="Error"/>
/// message when it's <see cref="CustomFieldWriteOutcome.Error"/>.</summary>
public sealed record CustomFieldWriteResult(
    CustomFieldWriteOutcome Outcome,
    CustomFieldValue? Value,
    string? Error)
{
    /// <summary>Nothing to send for this field.</summary>
    public static readonly CustomFieldWriteResult Skipped = new(CustomFieldWriteOutcome.Skip, null, null);

    /// <summary>A value to send.</summary>
    public static CustomFieldWriteResult Of(CustomFieldValue value)
        => new(CustomFieldWriteOutcome.Value, value, null);

    /// <summary>A validation error that should block Save.</summary>
    public static CustomFieldWriteResult Invalid(string error)
        => new(CustomFieldWriteOutcome.Error, null, error);
}

/// <summary>
/// Maps a user's entered custom-field value into the loosely-typed value shape ClickUp's create-task
/// <c>custom_fields: [{ id, value }]</c> array expects, per field type (#368 §1). Pure (definition + entry
/// in, a <see cref="CustomFieldWriteResult"/> out) so it is unit-tested with hand-built inputs and never
/// touches Terminal.Gui or a Kiota type — the facade turns the produced <see cref="JsonElement"/> into the
/// wire node. The write-side counterpart to <see cref="CustomFieldReader"/> / the read-side
/// <c>TaskDetailFormatter.CustomFieldValue</c>, whose type taxonomy it mirrors via
/// <see cref="CustomFieldTypes"/>.
/// </summary>
public static class CustomFieldValueSerializer
{
    /// <summary>
    /// Builds the create-payload value for <paramref name="field"/> from <paramref name="entry"/>. Returns
    /// <see cref="CustomFieldWriteResult.Skipped"/> when nothing was entered or the type isn't fillable
    /// (<see cref="CustomFieldTypes.IsFillable"/>); a value shaped for the type; or an error for
    /// unparseable numeric/date input. A blank field id also skips (there's nothing to write back to).
    /// </summary>
    public static CustomFieldWriteResult Build(CustomFieldDefinition field, CustomFieldEntry entry)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(field.Id) || !CustomFieldTypes.IsFillable(field.Type))
            return CustomFieldWriteResult.Skipped;

        var text = entry.Text?.Trim();
        var label = string.IsNullOrWhiteSpace(field.Name) ? field.Type! : field.Name;

        switch (field.Type!.Trim().ToLowerInvariant())
        {
            case "text" or "short_text" or "url" or "email" or "phone":
                return string.IsNullOrEmpty(text)
                    ? CustomFieldWriteResult.Skipped
                    : Value(field.Id, JsonSerializer.SerializeToElement(text));

            case "number" or "currency":
                if (string.IsNullOrEmpty(text))
                    return CustomFieldWriteResult.Skipped;
                // Preserve integers as integers (avoid "5" → 5.0); fall back to a double otherwise.
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return Value(field.Id, JsonSerializer.SerializeToElement(l));
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return Value(field.Id, JsonSerializer.SerializeToElement(d));
                return CustomFieldWriteResult.Invalid($"{label} must be a number.");

            case "checkbox":
                if (entry.Checked is { } b)
                    return Value(field.Id, JsonSerializer.SerializeToElement(b));
                if (string.IsNullOrEmpty(text))
                    return CustomFieldWriteResult.Skipped;
                return TryParseBool(text, out var parsed)
                    ? Value(field.Id, JsonSerializer.SerializeToElement(parsed))
                    : CustomFieldWriteResult.Invalid($"{label} must be true or false.");

            case "date":
                if (string.IsNullOrEmpty(text))
                    return CustomFieldWriteResult.Skipped;
                return TaskFieldInfo.TryParseNumeric(text, out var ms)
                    ? Value(field.Id, JsonSerializer.SerializeToElement(ms))
                    : CustomFieldWriteResult.Invalid($"{label} must be a date like 2026-07-15 (yyyy-MM-dd).");

            case "drop_down":
                var selected = entry.SelectedOptionIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                return string.IsNullOrWhiteSpace(selected)
                    ? CustomFieldWriteResult.Skipped
                    : Value(field.Id, JsonSerializer.SerializeToElement(selected));

            case "labels":
                var ids = entry.SelectedOptionIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
                return ids.Length == 0
                    ? CustomFieldWriteResult.Skipped
                    : Value(field.Id, JsonSerializer.SerializeToElement(ids));

            default:
                // Unreachable while Fillable and this switch agree; guards against them drifting apart.
                return CustomFieldWriteResult.Skipped;
        }
    }

    private static CustomFieldWriteResult Value(string id, JsonElement value)
        => CustomFieldWriteResult.Of(new CustomFieldValue(id, value));

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1" or "on":
                value = true;
                return true;
            case "false" or "no" or "0" or "off":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}
