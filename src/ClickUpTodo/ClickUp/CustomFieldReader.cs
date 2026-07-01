using System.Text.Json;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Reads a task custom field's loosely-typed <c>value</c> and <c>type_config.options</c> out of the
/// raw JSON of a ClickUp <c>CustomField</c>. Kept pure (a <see cref="JsonElement"/> in, plain records
/// out) so it is unit-testable with hand-built JSON and never touches a Kiota-generated type — the
/// one line that turns a generated <c>CustomField</c> into that <see cref="JsonElement"/> lives in
/// <see cref="ClickUpClient"/>. See issue #35.
/// </summary>
public static class CustomFieldReader
{
    /// <summary>
    /// Extracts the field's <c>value</c> (cloned so it outlives the source document; null when the
    /// property is absent or JSON-null) and its <c>type_config.options</c> (empty when absent).
    /// </summary>
    public static (JsonElement? Value, IReadOnlyList<CustomFieldOption> Options) Read(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object)
            return (null, []);

        JsonElement? value = null;
        if (field.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null)
            value = v.Clone();

        var options = ReadOptions(field);
        return (value, options);
    }

    private static IReadOnlyList<CustomFieldOption> ReadOptions(JsonElement field)
    {
        if (!field.TryGetProperty("type_config", out var config) || config.ValueKind != JsonValueKind.Object)
            return [];
        if (!config.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<CustomFieldOption>();
        foreach (var o in opts.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object)
                continue;
            // Drop-down options name the choice via "name"; labels-type options use "label".
            var name = GetString(o, "name") ?? GetString(o, "label");
            double? orderIndex =
                o.TryGetProperty("orderindex", out var oi) && oi.ValueKind == JsonValueKind.Number && oi.TryGetDouble(out var d)
                    ? d
                    : null;
            result.Add(new CustomFieldOption(GetString(o, "id"), name, orderIndex));
        }
        return result;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
