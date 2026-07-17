using System.Text.Json.Nodes;

namespace ClickUpTodo.Configuration;

/// <summary>
/// Pure three-way merge for the settings document (#293), so a config save from one tab doesn't
/// clobber a field another tab changed concurrently. Operates on the top-level JSON object: each
/// top-level property — including nested objects (<c>view</c>, <c>agentDispatch</c>) and arrays
/// (<c>pinnedTaskIds</c>) — is one "field", merged last-writer-wins as a unit. A field this process
/// changed since it loaded wins; an untouched field defers to the freshly-read on-disk value (which may
/// carry another tab's change). Kept free of I/O so it is unit-testable; <see cref="ConfigStore"/>
/// supplies the three serialised snapshots and persists the result.
/// </summary>
internal static class ConfigMerge
{
    /// <summary>
    /// Merges <paramref name="currentJson"/> (this process's in-memory config) over
    /// <paramref name="onDiskJson"/> (the freshly re-read persisted config), using
    /// <paramref name="baselineJson"/> (what this process last synced) to decide which top-level fields
    /// this process actually changed: a field is taken from <c>current</c> when it differs from
    /// <c>baseline</c>, otherwise from <c>onDisk</c>. Returns the merged object as a JSON string. All
    /// three inputs must be JSON objects serialised with <see cref="StateJson.Options"/> (stable
    /// property order), so per-field equality is a plain canonical-JSON comparison.
    /// </summary>
    public static string ThreeWay(string baselineJson, string currentJson, string onDiskJson)
    {
        var baseline = AsObject(baselineJson);
        var current = AsObject(currentJson);
        var onDisk = AsObject(onDiskJson);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in current)
            keys.Add(kv.Key);
        foreach (var kv in onDisk)
            keys.Add(kv.Key);

        var result = new JsonObject();
        foreach (var key in keys)
        {
            current.TryGetPropertyValue(key, out var curVal);
            onDisk.TryGetPropertyValue(key, out var diskVal);
            baseline.TryGetPropertyValue(key, out var baseVal);

            var diskHas = onDisk.ContainsKey(key);
            // "This process changed the field" ⇒ current differs from the baseline it loaded.
            var chosen = !JsonEquals(curVal, baseVal) ? curVal : (diskHas ? diskVal : curVal);
            result[key] = chosen?.DeepClone();
        }
        return result.ToJsonString(StateJson.Options);
    }

    private static JsonObject AsObject(string json)
        => JsonNode.Parse(json) as JsonObject ?? [];

    private static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);
    }
}
