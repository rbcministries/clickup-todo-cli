using System.Globalization;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// Pure text formatting for the task detail view (issue #17). Builds the header line and the body of
/// each tab from the domain DTOs, with no Terminal.Gui dependency, so the layout logic is unit-tested
/// while the (untestable) Terminal.Gui glue in <see cref="TaskDetailView"/> stays thin.
/// </summary>
public static class TaskDetailFormatter
{
    /// <summary>Header shown above the tabs: title, then tags and assignees when present.</summary>
    public static string Header(TaskDetail task)
    {
        var sb = new StringBuilder();
        sb.Append(task.Name);
        if (!string.IsNullOrWhiteSpace(task.CustomId))
            sb.Append("  (").Append(task.CustomId).Append(')');
        sb.Append('\n');

        if (task.Tags.Count > 0)
            sb.Append("Tags: ").Append(string.Join(", ", task.Tags)).Append('\n');
        sb.Append("Assignees: ")
          .Append(task.Assignees.Count > 0 ? string.Join(", ", task.Assignees) : "(none)");
        return sb.ToString();
    }

    /// <summary>The Description tab body.</summary>
    public static string Description(TaskDetail task)
        => string.IsNullOrWhiteSpace(task.Description) ? "(no description)" : task.Description!.Trim();

    /// <summary>The Comments tab body: one block per comment, in the order ClickUp returns them.</summary>
    public static string Comments(IReadOnlyList<CommentItem> comments)
    {
        if (comments.Count == 0)
            return "(no comments)";

        var sb = new StringBuilder();
        for (var i = 0; i < comments.Count; i++)
        {
            var c = comments[i];
            if (i > 0)
                sb.Append('\n');
            sb.Append(string.IsNullOrWhiteSpace(c.Author) ? "(unknown)" : c.Author);
            if (c.DateMs is { } ms)
                sb.Append("  ·  ").Append(FormatDate(ms));
            if (c.Resolved)
                sb.Append("  ·  [resolved]");
            sb.Append('\n');
            sb.Append(string.IsNullOrWhiteSpace(c.Text) ? "(empty comment)" : c.Text.Trim());
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The Other-attributes tab body: dates, list, priority, custom fields.</summary>
    public static string OtherAttributes(TaskDetail task)
    {
        var sb = new StringBuilder();
        sb.Append("List:          ").Append(Coalesce(task.ListName)).Append('\n');
        sb.Append("Priority:      ").Append(Coalesce(task.Priority)).Append('\n');
        sb.Append("Status:        ").Append(Coalesce(task.StatusName)).Append('\n');
        sb.Append("Created:       ").Append(FormatDateOrDash(task.CreatedMs)).Append('\n');
        sb.Append("Last activity: ").Append(FormatDateOrDash(task.UpdatedMs)).Append('\n');
        sb.Append("Due:           ").Append(FormatDateOrDash(task.DueDateMs)).Append('\n');

        sb.Append('\n').Append("Custom fields:").Append('\n');
        if (task.CustomFields.Count == 0)
            sb.Append("  (none)");
        else
            foreach (var f in task.CustomFields)
            {
                sb.Append("  • ").Append(f.Name);
                if (!string.IsNullOrWhiteSpace(f.Type))
                    sb.Append("  (").Append(f.Type).Append(')');
                var value = CustomFieldValue(f);
                if (!string.IsNullOrWhiteSpace(value))
                    sb.Append(": ").Append(value);
                sb.Append('\n');
            }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Longest custom-field value rendered on one line before it's truncated with an ellipsis.</summary>
    private const int MaxValueLength = 200;

    /// <summary>
    /// Renders a custom field's loosely-typed value for the terminal, dispatched by the field
    /// <see cref="CustomFieldItem.Type"/> then the JSON kind. Returns <c>null</c> when the field has
    /// no value (so the caller shows just its name/type). Never throws — any unexpected shape falls
    /// back to a compact stringified value. Pure (operates on the DTO only), so it is unit-tested.
    /// </summary>
    public static string? CustomFieldValue(CustomFieldItem field)
    {
        if (field.Value is not { } value || value.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            return (field.Type?.ToLowerInvariant()) switch
            {
                "drop_down" => DropDownValue(value, field.Options),
                "labels" => LabelsValue(value, field.Options),
                "users" => UsersValue(value),
                "date" => DateValue(value),
                "checkbox" => CheckboxValue(value),
                "manual_progress" or "automatic_progress" => ProgressValue(value),
                // Note: "emoji" (rating) is intentionally not here — its value shape isn't a bare
                // number, so it falls through to the compact fallback rather than mis-render.
                "number" or "currency" => NumberValue(value),
                // Note: "location" is an object ({formatted_address,…}), so it isn't here — it falls
                // through to the compact fallback rather than dumping raw object JSON as "text".
                "text" or "short_text" or "url" or "email" or "phone"
                    => Truncate(ScalarString(value)),
                _ => CompactFallback(value),
            };
        }
        catch
        {
            return CompactFallback(value);
        }
    }

    // A drop-down's value is the selected option's orderindex (number) or its id (string); resolve to
    // the option's display name via type_config.options, falling back to the raw selection.
    private static string DropDownValue(JsonElement value, IReadOnlyList<CustomFieldOption> options)
    {
        CustomFieldOption? match = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var idx)
                => options.FirstOrDefault(o => o.OrderIndex is { } oi && oi == idx),
            JsonValueKind.String => options.FirstOrDefault(o => o.Id == value.GetString()),
            _ => null,
        };
        return match?.Name ?? ScalarString(value);
    }

    // A labels/multi-select value is an array of option ids; map each to its option name.
    private static string LabelsValue(JsonElement value, IReadOnlyList<CustomFieldOption> options)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return CompactFallback(value);
        if (value.GetArrayLength() == 0)
            return ""; // no labels selected → omitted by the caller
        var names = value.EnumerateArray()
            .Select(id => id.ValueKind == JsonValueKind.String ? id.GetString() : ScalarString(id))
            .Select(id => options.FirstOrDefault(o => o.Id == id)?.Name ?? id ?? "")
            .Where(n => n.Length > 0);
        var joined = string.Join(", ", names);
        return Truncate(joined.Length > 0 ? joined : CompactFallback(value));
    }

    // A users value is an array of user objects; show username, then email, then id.
    private static string UsersValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return CompactFallback(value);
        if (value.GetArrayLength() == 0)
            return ""; // no users assigned → omitted by the caller
        var names = value.EnumerateArray()
            .Where(u => u.ValueKind == JsonValueKind.Object)
            .Select(u => Prop(u, "username") ?? Prop(u, "email") ?? Prop(u, "id") ?? "")
            .Where(n => n.Length > 0);
        var joined = string.Join(", ", names);
        return Truncate(joined.Length > 0 ? joined : CompactFallback(value));
    }

    private static string DateValue(JsonElement value)
    {
        long? ms = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var n) => (long)n,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
        return ms is { } v ? FormatDate(v) : ScalarString(value);
    }

    private static string CheckboxValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase) ? "Yes"
                              : string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase) ? "No"
                              : ScalarString(value),
        _ => ScalarString(value),
    };

    // Progress fields carry an object like { "percent_complete": 42, ... }.
    private static string ProgressValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("percent_complete", out var pc)
            && pc.ValueKind == JsonValueKind.Number
            && pc.TryGetDouble(out var percent))
            return FormatNumber(percent) + "%";
        return CompactFallback(value);
    }

    private static string NumberValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var n) => FormatNumber(n),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => FormatNumber(n),
        _ => ScalarString(value),
    };

    // The scalar as a human string: JSON strings without quotes, everything else via its raw text.
    private static string ScalarString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    // Last-resort rendering for an unexpected shape: compact, single-line, truncated JSON.
    private static string CompactFallback(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return Truncate(string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

    private static string FormatNumber(double n)
        => n.ToString("0.############", CultureInfo.InvariantCulture);

    private static string? Prop(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p)
            ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.GetRawText())
            : null;

    private static string Truncate(string value)
        => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "…";

    private static string Coalesce(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value!;

    private static string FormatDateOrDash(long? ms) => ms is { } v ? FormatDate(v) : "—";

    private static string FormatDate(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MMM d, yyyy HH:mm");
}
