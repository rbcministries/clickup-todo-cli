using System.Globalization;
using System.Text.Json;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Reads a task checklist's loosely-typed <c>items</c> array out of the raw JSON of a ClickUp
/// <c>Checklist</c> (#454). Kept pure (a <see cref="JsonElement"/> in, plain records out) so it is
/// unit-testable with hand-built JSON and never touches a Kiota-generated type — the one line that turns
/// a generated <c>Checklist</c> into that <see cref="JsonElement"/> lives in <see cref="ClickUpClient"/>,
/// exactly as <see cref="CustomFieldReader"/> does for custom-field values (#35).
/// <para>The container fields (id/name/orderindex/resolved counts) are cleanly typed by the spec and read
/// as generated properties; only the item shape lives here because it varies by API version: an item's
/// <c>orderindex</c> is a number <b>or</b> a numeric string, its <c>assignee</c> is <c>null</c> / a bare
/// user id / a full user object, and nesting is expressed via a <c>parent</c> id-pointer and/or a
/// populated <c>children</c> array — this reader tolerates all of them.</para>
/// </summary>
public static class ChecklistReader
{
    /// <summary>
    /// Extracts a checklist's <c>items</c> as domain <see cref="TaskChecklistItem"/>s — the top level of
    /// the array in API order, each carrying its <c>parent</c> id and recursively-read <c>children</c>.
    /// Returns an empty list when <paramref name="checklist"/> is not an object or has no <c>items</c>
    /// array; items with no <c>id</c> are skipped.
    /// </summary>
    public static IReadOnlyList<TaskChecklistItem> ReadItems(JsonElement checklist)
    {
        if (checklist.ValueKind != JsonValueKind.Object)
            return [];
        if (!checklist.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return ReadItemArray(items);
    }

    private static IReadOnlyList<TaskChecklistItem> ReadItemArray(JsonElement array)
    {
        var result = new List<TaskChecklistItem>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            var item = ReadItem(element);
            if (item is not null)
                result.Add(item);
        }
        return result;
    }

    private static TaskChecklistItem? ReadItem(JsonElement o)
    {
        var id = GetString(o, "id");
        if (string.IsNullOrEmpty(id))
            return null; // an item without an id is unusable — drop it rather than surface a blank row.

        var children = o.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array
            ? ReadItemArray(ch)
            : [];

        return new TaskChecklistItem(
            Id: id,
            Name: GetString(o, "name") ?? "",
            Resolved: ReadBool(o, "resolved"),
            OrderIndex: ReadOrderIndex(o, "orderindex"),
            ParentId: GetString(o, "parent"),
            Assignee: ReadAssignee(o, "assignee"),
            Children: children);
    }

    /// <summary>The optional per-item assignee. Tolerates the three shapes ClickUp has used: absent /
    /// JSON-null → no assignee; a bare numeric (or numeric-string) user id → id with an empty name; a full
    /// user object → id plus a username → email-local → empty display name. Reuses <see cref="TaskAssignee"/>.</summary>
    private static TaskAssignee? ReadAssignee(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var a) || a.ValueKind == JsonValueKind.Null)
            return null;

        switch (a.ValueKind)
        {
            case JsonValueKind.Number when a.TryGetInt64(out var idNum):
                return new TaskAssignee(idNum, "");
            case JsonValueKind.String when long.TryParse(a.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idStr):
                return new TaskAssignee(idStr, "");
            case JsonValueKind.Object:
                var id = ReadUserId(a);
                var display = AssigneeDisplayName(a);
                return id == 0 && display.Length == 0 ? null : new TaskAssignee(id, display);
            default:
                return null;
        }
    }

    private static long ReadUserId(JsonElement user)
    {
        if (!user.TryGetProperty("id", out var id))
            return 0;
        return id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(id.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        };
    }

    /// <summary>Best display name for a checklist-item assignee object: username, then the email's local
    /// part, else empty (a bare-id assignee has none — a later slice resolves it).</summary>
    private static string AssigneeDisplayName(JsonElement user)
    {
        var username = GetString(user, "username");
        if (!string.IsNullOrWhiteSpace(username))
            return username.Trim();
        var email = GetString(user, "email")?.Trim() ?? "";
        var at = email.IndexOf('@');
        var local = at >= 0 ? email[..at] : email;
        return local;
    }

    /// <summary>Reads a number-or-numeric-string <c>orderindex</c> to a <see cref="double"/>; null when
    /// absent or unparseable. ClickUp reports checklist-item orderindex either way depending on the API.</summary>
    private static double? ReadOrderIndex(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    /// <summary>An item's <c>resolved</c> flag. Primarily a JSON boolean, but tolerates a numeric
    /// (non-zero ⇒ resolved) or a string ("true"/"1") in case the API varies; anything else ⇒ false.</summary>
    private static bool ReadBool(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v))
            return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when v.TryGetDouble(out var d) => d != 0,
            JsonValueKind.String => v.GetString() is var s
                && (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1"),
            _ => false,
        };
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
