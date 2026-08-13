using System.Text.Json;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>The persisted <b>discovered</b>-layer snapshot for the agent registry (#494), written under
/// <see cref="StateKeys.AgentDirectories"/>. Carries the <see cref="WorkspaceId"/> it was captured for
/// (a mismatch on load is a clean miss, so switching workspace never warms foreign agents) and a
/// <see cref="SchemaVersion"/> guarding an incompatible future shape. Seeded entries are <b>not</b>
/// persisted here — they live in <see cref="SuperAgentSettings.Agents"/> and are re-merged each run.</summary>
public sealed record AgentDirectoryDocument(
    int SchemaVersion, string WorkspaceId, IReadOnlyList<AgentDirectoryEntryDto> Entries);

/// <summary>One persisted discovered agent plus the epoch-ms UTC time it was fetched, so the TTL applies
/// unchanged to a persisted entry (a stale one is dropped on load, never served past expiry).</summary>
public sealed record AgentDirectoryEntryDto(long Id, string Name, string? Purpose, long FetchedAtMs);

/// <summary>
/// A thread-safe local <b>agent directory</b> (#494) — the <c>name → negative id</c> registry that
/// substitutes for ClickUp's missing agent-enumeration endpoint. It merges two layers: a
/// <b>config seed</b> (<see cref="SuperAgentSettings.Agents"/>) the user pins by hand, and a
/// <b>discovered</b> layer populated through an injectable <see cref="IAgentDiscoverySource"/>. The seed
/// is authoritative (it wins on an id collision and is never auto-evicted); the discovered layer is TTL'd
/// and evictable, because a cached negative id is valid only for that agent's current lifetime and points
/// at nothing once the agent is recreated (spike Finding 3).
/// <para>
/// With no discovery source the cache runs <b>seed-only</b>: <see cref="RefreshAsync"/> is a no-op and the
/// pins are still served — the settled fallback for "discovery unavailable". When an
/// <see cref="IStateStore"/> is supplied the discovered layer warms from the persisted snapshot on
/// construction (dropping entries already past the TTL) and is rewritten after each refresh/eviction.
/// Wiring a real discovery source onto <c>getChatChannelMembers</c> is #493's deferred work — this class
/// is the seam it plugs into.
/// </para>
/// </summary>
public sealed class AgentDirectoryCache
{
    /// <summary>The persisted-shape version; bump when <see cref="AgentDirectoryDocument"/> changes
    /// incompatibly so an old document is discarded rather than mis-read.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Default freshness window for a discovered entry. Moderate by design: negative ids look
    /// recreation-stable but are not guaranteed (spike Finding 3), so they are refreshed periodically
    /// rather than hard-cached forever.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(12);

    private readonly IAgentDiscoverySource? _discovery;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly IStateStore? _store;
    private readonly string _workspaceId;

    // The seed is immutable for the cache's lifetime (it comes from config, re-read each run), already
    // validated and deduped by id in seed order.
    private readonly IReadOnlyList<AgentDirectoryEntry> _seed;
    private readonly Dictionary<long, DiscoveredEntry> _discovered = new();
    private readonly Lock _gate = new();
    private readonly Lock _persistGate = new();

    // When the discovered layer was last populated — by a refresh, or by warming from the persisted
    // snapshot (the newest loaded capture time). Drives NeedsRefresh so a successful-but-empty discovery
    // still counts as "fetched" (it doesn't re-arm on every tick), separate from whether any entry
    // survived. Guarded by _gate.
    private DateTimeOffset? _lastPopulatedAt;

    private readonly record struct DiscoveredEntry(AgentDirectoryEntry Entry, DateTimeOffset FetchedAt);

    /// <param name="seed">The user's hand-pinned agents (<see cref="SuperAgentSettings.Agents"/>). Invalid
    /// entries (non-agent id or blank name) are dropped; duplicate ids keep the first in seed order.</param>
    /// <param name="discovery">Optional live discovery source. Omit for a seed-only registry, in which
    /// case <see cref="RefreshAsync"/> is a no-op.</param>
    /// <param name="timeProvider">Clock for TTL comparisons (defaults to the system clock).</param>
    /// <param name="ttl">How long a discovered entry stays fresh (default <see cref="DefaultTtl"/>).</param>
    /// <param name="store">Optional persistence backend; when supplied the discovered layer warms from and
    /// writes back to <see cref="StateKeys.AgentDirectories"/>. Omit for an in-memory registry.</param>
    /// <param name="workspaceId">The workspace the persisted snapshot is scoped to; a document for a
    /// different workspace is ignored on load.</param>
    public AgentDirectoryCache(
        IEnumerable<AgentSeedEntry>? seed = null,
        IAgentDiscoverySource? discovery = null,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null,
        IStateStore? store = null,
        string workspaceId = "")
    {
        _discovery = discovery;
        _clock = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
        _store = store;
        _workspaceId = workspaceId ?? "";
        _seed = BuildSeed(seed);
        WarmFromStore();
    }

    /// <summary>The merged registry: seeded pins first (in seed order), then fresh discovered agents (by
    /// name), deduped by id with the seed winning. Stale discovered entries are excluded until a refresh
    /// replaces them.</summary>
    public IReadOnlyList<AgentDirectoryEntry> Entries
    {
        get
        {
            lock (_gate)
                return AgentDirectory.Merge(_seed, FreshDiscoveredLocked());
        }
    }

    /// <summary>Whether a refresh would help: a discovery source is present and the discovered layer was
    /// either never populated or was populated longer than the TTL ago. Deliberately keyed on <em>when</em>
    /// the layer was last populated, not on whether it currently holds an entry — so a workspace that
    /// legitimately has no agents (a successful, empty discovery) reports <c>false</c> until the TTL
    /// elapses, rather than re-arming a refresh on every tick. Seed-only registries never need a refresh.</summary>
    public bool NeedsRefresh
    {
        get
        {
            if (_discovery is null)
                return false;
            lock (_gate)
                return _lastPopulatedAt is not { } last || _clock.GetUtcNow() - last >= _ttl;
        }
    }

    /// <summary>Look an agent up by id — a seeded pin wins over a discovered entry with the same id; a
    /// stale discovered entry is not returned. Null when unknown.</summary>
    public AgentDirectoryEntry? Find(long id)
    {
        lock (_gate)
        {
            var seeded = _seed.FirstOrDefault(e => e.Id == id);
            if (seeded is not null)
                return seeded;
            if (_discovered.TryGetValue(id, out var d) && IsFresh(d))
                return d.Entry;
            return null;
        }
    }

    /// <summary>Look an agent up by display name (case-insensitive), seed first then fresh discovered —
    /// the <c>name → negative id</c> resolution the mention picker (#495) and the write path consume.
    /// Null when unknown.</summary>
    public AgentDirectoryEntry? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var needle = name.Trim();
        lock (_gate)
            return AgentDirectory.Merge(_seed, FreshDiscoveredLocked())
                .FirstOrDefault(e => string.Equals(e.Name, needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Replace the discovered layer from the <see cref="IAgentDiscoverySource"/> and persist it. A no-op
    /// when no source is configured (the seed is left intact). On a source error the exception propagates
    /// and the existing discovered layer is left untouched (only a successful discovery replaces it), so a
    /// transient failure never blanks the registry. Invalid discovered entries (non-agent id / blank name)
    /// are dropped; the entries are stamped <see cref="AgentEntrySource.Discovered"/> and timestamped now.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_discovery is null)
            return;

        var discovered = await _discovery.DiscoverAsync(_workspaceId, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            _discovered.Clear();
            foreach (var entry in discovered)
            {
                if (entry is null || !AgentDirectory.IsValid(entry.Id, entry.Name))
                    continue;
                // Last-wins within one discovery batch on a duplicate id — a source shouldn't emit dups,
                // and cross-layer precedence (seed over discovered) is decided later in Merge regardless.
                _discovered[entry.Id] = new DiscoveredEntry(
                    new AgentDirectoryEntry(entry.Id, entry.Name.Trim(), Normalize(entry.Purpose), AgentEntrySource.Discovered),
                    now);
            }
            // Stamp the populate time even when the batch was empty, so NeedsRefresh treats a legitimately
            // agent-less workspace as "fetched" until the TTL elapses.
            _lastPopulatedAt = now;
        }
        Persist();
    }

    /// <summary>
    /// Drop a <b>discovered</b> entry whose cached id failed to resolve/notify on a write (per #494's
    /// evict-on-failure), so the next refresh re-discovers it. Returns whether an entry was removed. A
    /// <b>seeded</b> id is never evicted — a user's hand pin is authoritative and a transient failure must
    /// not silently unpin it (fix it in config instead), so eviction touches only the discovered layer.
    /// </summary>
    public bool Evict(long id)
    {
        bool removed;
        lock (_gate)
            removed = _discovered.Remove(id);
        if (removed)
            Persist();
        return removed;
    }

    private IReadOnlyList<AgentDirectoryEntry> FreshDiscoveredLocked()
        => _discovered.Values
            .Where(IsFresh)
            .Select(d => d.Entry)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id)
            .ToList();

    private bool IsFresh(DiscoveredEntry entry) => _clock.GetUtcNow() - entry.FetchedAt < _ttl;

    // Validate + dedup the config seed once: keep only agent-id/non-blank entries, first-wins on a
    // duplicate id, in seed order, stamped Seeded.
    private static IReadOnlyList<AgentDirectoryEntry> BuildSeed(IEnumerable<AgentSeedEntry>? seed)
    {
        if (seed is null)
            return [];
        var seen = new HashSet<long>();
        var result = new List<AgentDirectoryEntry>();
        foreach (var entry in seed)
        {
            if (entry is null || !AgentDirectory.IsValid(entry.Id, entry.Name) || !seen.Add(entry.Id))
                continue;
            result.Add(new AgentDirectoryEntry(entry.Id, entry.Name.Trim(), Normalize(entry.Purpose), AgentEntrySource.Seeded));
        }
        return result;
    }

    private static string? Normalize(string? purpose) => string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();

    // Warm the discovered layer from the persisted snapshot, dropping entries already past the TTL (a
    // stale persisted id is not resurrected) and keeping each fresh entry's captured fetch time so the
    // TTL still governs it.
    private void WarmFromStore()
    {
        if (_store is null)
            return;

        AgentDirectoryDocument? doc;
        try
        {
            doc = _store.Load<AgentDirectoryDocument>(StateKeys.AgentDirectories);
        }
        catch (JsonException)
        {
            // A corrupt/truncated snapshot (quit or crash mid-write) is a miss, never a crash — this runs
            // before the UI loop, so a throw would brick launch. A refresh repopulates it.
            return;
        }

        if (doc is null
            || doc.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(doc.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            return;

        var now = _clock.GetUtcNow();
        DateTimeOffset? newestLoaded = null;
        foreach (var entry in doc.Entries)
        {
            if (entry is null || !AgentDirectory.IsValid(entry.Id, entry.Name))
                continue;
            // A structurally-valid but out-of-range timestamp (a hand-tampered file) makes
            // FromUnixTimeMilliseconds throw — skip that entry rather than let it crash the launch this
            // guard exists to protect (our own writes are always in range).
            DateTimeOffset fetchedAt;
            try { fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.FetchedAtMs); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (now - fetchedAt >= _ttl)
                continue; // already stale on load — drop it (mirrors ListColorCache)
            _discovered[entry.Id] = new DiscoveredEntry(
                new AgentDirectoryEntry(entry.Id, entry.Name.Trim(), Normalize(entry.Purpose), AgentEntrySource.Discovered),
                fetchedAt);
            if (newestLoaded is null || fetchedAt > newestLoaded)
                newestLoaded = fetchedAt;
        }
        // Warming a fresh persisted layer counts as a populate, so NeedsRefresh stays false until the TTL
        // elapses (the persisted entries all share the last refresh's capture time). If everything was
        // stale/invalid, nothing loaded and _lastPopulatedAt stays null ⇒ NeedsRefresh re-arms.
        if (newestLoaded is { } loaded)
            _lastPopulatedAt = loaded;
    }

    // Rewrites the whole discovered snapshot (the set is tiny). The disk write runs under a dedicated
    // _persistGate, not _gate, so a read of Entries never stalls on I/O; the snapshot is copied under
    // _gate. Best-effort: a failed write leaves the in-memory registry intact and the next refresh retries.
    private void Persist()
    {
        if (_store is null)
            return;
        lock (_persistGate)
        {
            AgentDirectoryDocument doc;
            lock (_gate)
                doc = new AgentDirectoryDocument(
                    CurrentSchemaVersion,
                    _workspaceId,
                    _discovered.Values
                        .Select(d => new AgentDirectoryEntryDto(
                            d.Entry.Id, d.Entry.Name, d.Entry.Purpose, d.FetchedAt.ToUnixTimeMilliseconds()))
                        .ToList());
            try { _store.Save(StateKeys.AgentDirectories, doc); }
            catch { /* best-effort; see note above */ }
        }
    }
}
