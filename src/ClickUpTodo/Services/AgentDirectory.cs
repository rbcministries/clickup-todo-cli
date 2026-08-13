namespace ClickUpTodo.Services;

/// <summary>Where a <see cref="AgentDirectoryEntry"/> came from — a user's hand-pinned config seed, or
/// live discovery through an <see cref="IAgentDiscoverySource"/>.</summary>
public enum AgentEntrySource
{
    /// <summary>Pinned by the user in <c>config.json</c> (<c>SuperAgentSettings.Agents</c>); authoritative
    /// and never auto-evicted.</summary>
    Seeded,

    /// <summary>Populated from a discovery source; TTL'd and evictable when its cached id goes stale.</summary>
    Discovered,
}

/// <summary>One agent in the local registry: its (negative) id, display name, an optional purpose blurb
/// the picker may show, and where the entry came from.</summary>
public sealed record AgentDirectoryEntry(long Id, string Name, string? Purpose, AgentEntrySource Source);

/// <summary>
/// Pure, side-effect-free logic for the agent registry (<see cref="AgentDirectoryCache"/>), split out so
/// the merge/validity rules are unit-testable without a clock or a store — mirroring
/// <see cref="AssigneeFrequency"/> / <see cref="ListFrequency"/> sitting beside their caches.
/// </summary>
public static class AgentDirectory
{
    /// <summary>
    /// Whether an entry is keepable in an <em>agent</em> directory: an <see cref="AgentIdentity.IsAgentId">
    /// agent id</see> with a non-blank name. Applied at ingest to both seeded and discovered entries so a
    /// human/system id (or a nameless row) never enters the registry.
    /// </summary>
    public static bool IsValid(long id, string? name)
        => AgentIdentity.IsAgentId(id) && !string.IsNullOrWhiteSpace(name);

    /// <summary>
    /// Merges the seeded and discovered layers into the registry's display list: <b>seeded first (in seed
    /// order), then discovered not already seeded (in the given order)</b>, deduped by id with the
    /// <b>seed winning</b> on a collision. A manual pin is authoritative — it is how a user overrides a
    /// stale or wrong discovery — so a discovered entry sharing a seeded id is dropped in favour of the
    /// pin. Inputs are assumed already validated (<see cref="IsValid"/>); this method only decides
    /// precedence and order.
    /// </summary>
    public static IReadOnlyList<AgentDirectoryEntry> Merge(
        IReadOnlyList<AgentDirectoryEntry> seeded, IReadOnlyList<AgentDirectoryEntry> discovered)
    {
        var seen = new HashSet<long>();
        var result = new List<AgentDirectoryEntry>(seeded.Count + discovered.Count);
        foreach (var entry in seeded)
            if (seen.Add(entry.Id))
                result.Add(entry);
        foreach (var entry in discovered)
            if (seen.Add(entry.Id))
                result.Add(entry);
        return result;
    }
}
