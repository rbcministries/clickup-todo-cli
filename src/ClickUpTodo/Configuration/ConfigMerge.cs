using System.Text.Json.Nodes;

namespace ClickUpTodo.Configuration;

/// <summary>
/// Pure three-way merge for the settings document (#293), so a config save from one tab doesn't
/// clobber a field another tab changed concurrently. Operates on the top-level JSON object: each
/// top-level property — including nested objects (<c>view</c>, <c>agentDispatch</c>) and most arrays —
/// is one "field", merged last-writer-wins as a unit. A field this process changed since it loaded
/// wins; an untouched field defers to the freshly-read on-disk value (which may carry another tab's
/// change). Kept free of I/O so it is unit-testable; <see cref="ConfigStore"/> supplies the three
/// serialised snapshots and persists the result.
/// <para>
/// The one exception is <c>pinnedTaskIds</c> (#335): whole-field LWW there loses a concurrent pin when
/// two tabs pin different tasks in the same load→save window (whichever saves second overwrites the
/// other's array). It instead gets an element-level three-way <b>set union</b> — additions on either
/// side (vs. baseline) are unioned, while a genuine unpin (an id in baseline dropped on a side) is
/// honored rather than resurrected. This mirrors the collection-union precedent in
/// <see cref="Services.AssigneeFrequency"/>/<see cref="Services.ListFrequency"/> <c>Merge</c>.
/// </para>
/// </summary>
internal static class ConfigMerge
{
    /// <summary>
    /// Top-level array fields that three-way <b>set-union</b> their elements instead of merging
    /// whole-field last-writer-wins. Keyed by the serialised (camelCase) property name. Only
    /// string-element sets belong here; a map field (<c>taskWorkingDirectories</c>) or a
    /// migration-only shim (<c>excludedStatuses</c>) must not be listed.
    /// </summary>
    private static readonly HashSet<string> SetUnionArrayFields =
        new(StringComparer.Ordinal) { "pinnedTaskIds" };

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

            if (SetUnionArrayFields.Contains(key))
            {
                // Element-level three-way set union (#335) — never whole-field LWW for these.
                result[key] = MergeStringSetThreeWay(baseVal, curVal, diskVal);
                continue;
            }

            var diskHas = onDisk.ContainsKey(key);
            // "This process changed the field" ⇒ current differs from the baseline it loaded.
            var chosen = !JsonEquals(curVal, baseVal) ? curVal : (diskHas ? diskVal : curVal);
            result[key] = chosen?.DeepClone();
        }
        return result.ToJsonString(StateJson.Options);
    }

    /// <summary>
    /// Three-way set-union of a string array against its <paramref name="baseline"/> (#335): an id is
    /// kept when it was <b>added</b> on either side (present now, absent in baseline) or <b>survived</b>
    /// in baseline without being removed on either side (an id in baseline dropped on a side is an
    /// unpin, honored — not resurrected by the union). Order preserves <paramref name="current"/>'s
    /// sequence for its included ids, then appends <paramref name="onDisk"/>-only additions in disk
    /// order; de-dup is ordinal. Any side that is null / not a JSON array (a legacy or absent value) is
    /// treated as the empty set, and non-string / blank elements are ignored defensively.
    /// </summary>
    private static JsonArray MergeStringSetThreeWay(JsonNode? baseline, JsonNode? current, JsonNode? onDisk)
    {
        var baseSet = ToStringSet(baseline);
        var curList = ToStringList(current);
        var curSet = new HashSet<string>(curList, StringComparer.Ordinal);
        var diskList = ToStringList(onDisk);
        var diskSet = new HashSet<string>(diskList, StringComparer.Ordinal);

        // Kept from baseline only when neither side removed it (present on both current and disk).
        bool Included(string id)
        {
            var inCur = curSet.Contains(id);
            var inDisk = diskSet.Contains(id);
            var inBase = baseSet.Contains(id);
            var added = !inBase && (inCur || inDisk);
            var keptFromBase = inBase && inCur && inDisk;
            return added || keptFromBase;
        }

        var result = new JsonArray();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in curList)
            if (emitted.Add(id) && Included(id))
                result.Add(id);
        foreach (var id in diskList)
            if (emitted.Add(id) && Included(id))
                result.Add(id);
        return result;
    }

    private static List<string> ToStringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray arr)
            return list;
        foreach (var el in arr)
            if (el is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                list.Add(s);
        return list;
    }

    private static HashSet<string> ToStringSet(JsonNode? node)
        => new(ToStringList(node), StringComparer.Ordinal);

    private static JsonObject AsObject(string json)
        => JsonNode.Parse(json) as JsonObject ?? [];

    private static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);
    }
}
