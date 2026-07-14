using System.Text.Json;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>The persisted per-list color-chip snapshot (#125), written under
/// <see cref="StateKeys.ListColors"/>. Carries the <see cref="WorkspaceId"/> it was captured for (a
/// mismatch on load is a clean miss, so a workspace switch never warms foreign lists' colors) and a
/// <see cref="SchemaVersion"/> guarding an incompatible future shape. Each entry keeps its capture
/// timestamp so a persisted color expires after the color TTL.</summary>
public sealed record ListColorDocument(
    int SchemaVersion, string WorkspaceId, IReadOnlyList<ListColorEntry> Entries);

/// <summary>One persisted list's color chip (null = "fetched, but the list has no color set") plus the
/// epoch-ms UTC time it was resolved.</summary>
public sealed record ListColorEntry(string ListId, string? Color, long FetchedAtMs);

/// <summary>
/// The per-list color-chip cache (#125): owns the in-memory <c>listId → color</c> map
/// <see cref="TaskService"/> uses to tint List-grouped headers, and — when an <see cref="IStateStore"/>
/// is supplied — persists it so a restart's first render can tint headers without re-resolving every
/// color. A null value is a real "fetched, no color set" (cached so it isn't refetched), matching the
/// dictionary this replaced.
/// <para>
/// Within a session a color is held for the process lifetime (a list's color can't change under us),
/// exactly as before. The TTL governs only the <em>persisted</em> warm-up: colors change even more
/// rarely than statuses, so the default is a long 7 days — long enough to be worthwhile, short enough
/// that a recolored list self-corrects within a week even if never otherwise refetched. A stale
/// persisted entry is dropped on load, so it is neither warmed nor carried into the next write.
/// </para>
/// With no store the cache is purely in-memory — the same behavior as the plain dictionary it replaced.
/// All access is guarded so the background color resolve and the constructor's warm-up can't race.
/// </summary>
public sealed class ListColorCache
{
    /// <summary>The persisted-shape version; bump when <see cref="ListColorDocument"/> changes
    /// incompatibly so an old document is discarded rather than mis-read.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly IStateStore? _store;
    private readonly string _workspaceId;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Lock _persistGate = new();

    private readonly record struct Entry(string? Color, long FetchedAtMs);

    /// <param name="store">Optional persistence backend; when supplied the cache warms from and writes
    /// back to <see cref="StateKeys.ListColors"/>. Omit for a purely in-memory cache.</param>
    /// <param name="workspaceId">The workspace this snapshot is scoped to; a document for a different
    /// workspace is ignored on load.</param>
    /// <param name="timeProvider">Clock for TTL comparisons (defaults to the system clock).</param>
    /// <param name="ttl">How long a persisted color stays warmable (default 7 days).</param>
    public ListColorCache(
        IStateStore? store = null, string workspaceId = "", TimeProvider? timeProvider = null, TimeSpan? ttl = null)
    {
        _store = store;
        _workspaceId = workspaceId ?? "";
        _clock = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromDays(7);
        if (_store is not null)
            Load();
    }

    /// <summary>Whether <paramref name="listId"/>'s color has already been resolved (including a resolved
    /// "no color"), so the caller can skip refetching it.</summary>
    public bool Contains(string listId)
    {
        lock (_gate)
            return _entries.ContainsKey(listId);
    }

    /// <summary>The current colors (listId → color) for tinting; a null value is a resolved "no color".</summary>
    public IReadOnlyDictionary<string, string?> Snapshot()
    {
        lock (_gate)
            return _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Color, StringComparer.Ordinal);
    }

    /// <summary>Record newly-resolved colors (stamped now) into the in-memory map, then — when persisting —
    /// merge them into the stored snapshot and rewrite it once. Best-effort: a failed write must never
    /// break the color resolve. A no-op when <paramref name="resolved"/> is empty.</summary>
    public void Save(IReadOnlyDictionary<string, string?> resolved)
    {
        if (resolved.Count == 0)
            return;

        lock (_gate)
        {
            var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var (listId, color) in resolved)
            {
                if (!string.IsNullOrWhiteSpace(listId))
                    _entries[listId] = new Entry(color, nowMs);
            }
        }
        Persist(); // off _gate — see Persist
    }

    // Warm the in-memory entries from the persisted snapshot, dropping any past their TTL so a stale
    // color is neither warmed nor carried forward into the next write.
    private void Load()
    {
        ListColorDocument? doc;
        try
        {
            doc = _store!.Load<ListColorDocument>(StateKeys.ListColors);
        }
        catch (JsonException)
        {
            // A corrupt/truncated snapshot is a miss, never a crash — this runs before the UI loop.
            return;
        }

        if (doc is null
            || doc.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(doc.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            return;

        var now = _clock.GetUtcNow();
        foreach (var entry in doc.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ListId))
                continue;
            // A structurally-valid but out-of-range timestamp (a hand-tampered file) makes
            // FromUnixTimeMilliseconds throw — skip that entry rather than crash the launch this guard
            // protects (our own writes are always in range).
            DateTimeOffset fetchedAt;
            try { fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.FetchedAtMs); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (now - fetchedAt >= _ttl)
                continue; // stale on load → drop; refetched on demand.
            _entries[entry.ListId] = new Entry(entry.Color, entry.FetchedAtMs);
        }
    }

    // Rewrites the whole snapshot. The disk write runs under a dedicated _persistGate, not _gate, so it
    // never blocks Contains/Snapshot (and stays consistent with StatusCache, whose reads are on the UI
    // fast-path). _persistGate serialises writes per key (IStateStore's contract); the snapshot is copied
    // under _gate — briefly, in memory — so each serialised write persists the latest state. Best-effort:
    // a failed write leaves the in-memory cache intact.
    private void Persist()
    {
        if (_store is null)
            return;
        lock (_persistGate)
        {
            ListColorDocument doc;
            lock (_gate)
                doc = new ListColorDocument(
                    CurrentSchemaVersion,
                    _workspaceId,
                    _entries.Select(kv => new ListColorEntry(kv.Key, kv.Value.Color, kv.Value.FetchedAtMs)).ToList());
            try { _store.Save(StateKeys.ListColors, doc); }
            catch { /* see note above */ }
        }
    }
}
