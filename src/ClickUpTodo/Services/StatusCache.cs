using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>The persisted per-list status-options snapshot (#125), written under
/// <see cref="StateKeys.Statuses"/>. Carries the <see cref="WorkspaceId"/> it was captured for — a
/// mismatch on load is a clean miss, so switching workspace never warms foreign lists' statuses — and
/// a <see cref="SchemaVersion"/> guarding an incompatible future shape. Each entry keeps its capture
/// timestamp so the cache's TTL applies unchanged to a persisted entry (a stale one is refetched, never
/// served past expiry).</summary>
public sealed record StatusCacheDocument(
    int SchemaVersion, string WorkspaceId, IReadOnlyList<StatusCacheEntryDto> Entries);

/// <summary>One persisted list's status options plus the epoch-ms UTC time they were fetched.</summary>
public sealed record StatusCacheEntryDto(string ListId, IReadOnlyList<StatusOption> Statuses, long FetchedAtMs);

/// <summary>
/// A thread-safe, TTL'd cache of per-list status options, decoupled from the ClickUp client so it
/// can be exercised without the network. A list's statuses almost never change, so an entry stays
/// fresh for <c>ttl</c> (default 10 minutes) before the next access refetches it. Concurrent fetches
/// for the same list are de-duplicated, so a prefetch already in flight is awaited rather than
/// duplicated when the user opens the picker.
/// <para>
/// When an <see cref="IStateStore"/> is supplied (#125), the cache warms from the persisted snapshot on
/// construction — seeding each entry with its <em>persisted</em> fetch time so the TTL still governs it
/// (a persisted entry older than the TTL is a miss and gets refetched) — and rewrites the snapshot after
/// each successful fetch. With no store it is purely in-memory, exactly as before.
/// </para>
/// </summary>
public sealed class StatusCache
{
    /// <summary>The persisted-shape version; bump when <see cref="StatusCacheDocument"/> changes
    /// incompatibly so an old document is discarded rather than mis-read.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<StatusOption>>> _fetch;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly IStateStore? _store;
    private readonly string _workspaceId;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Dictionary<string, Task<IReadOnlyList<StatusOption>>> _inFlight = new();
    private readonly Lock _gate = new();

    private readonly record struct Entry(IReadOnlyList<StatusOption> Statuses, DateTimeOffset FetchedAt);

    /// <param name="fetch">Fetches a list's statuses when the cache misses.</param>
    /// <param name="timeProvider">Clock for TTL comparisons (defaults to the system clock).</param>
    /// <param name="ttl">How long an entry stays fresh (default 10 minutes).</param>
    /// <param name="store">Optional persistence backend; when supplied the cache warms from and writes
    /// back to <see cref="StateKeys.Statuses"/>. Omit for a purely in-memory cache.</param>
    /// <param name="workspaceId">The workspace the persisted snapshot is scoped to; a document for a
    /// different workspace is ignored on load.</param>
    public StatusCache(
        Func<string, CancellationToken, Task<IReadOnlyList<StatusOption>>> fetch,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null,
        IStateStore? store = null,
        string workspaceId = "")
    {
        _fetch = fetch;
        _clock = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _store = store;
        _workspaceId = workspaceId ?? "";
        WarmFromStore();
    }

    /// <summary>
    /// Returns a cached value synchronously when present and still fresh (within the TTL); false for
    /// a missing or stale entry. Used by the picker's "open immediately if cached" path.
    /// </summary>
    public bool TryGetFresh(string listId, out IReadOnlyList<StatusOption> statuses)
    {
        lock (_gate)
            return TryGetFreshLocked(listId, out statuses);
    }

    /// <summary>Returns a fresh cached value, or fetches (de-duping concurrent fetches) and caches it.</summary>
    public Task<IReadOnlyList<StatusOption>> GetAsync(string listId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (TryGetFreshLocked(listId, out var fresh))
                return Task.FromResult(fresh);
            if (_inFlight.TryGetValue(listId, out var pending))
                return pending;

            var task = FetchAndStoreAsync(listId, ct);
            _inFlight[listId] = task;
            return task;
        }
    }

    /// <summary>
    /// Best-effort warm-up: fetches only the lists that are missing or stale, so a later picker-open
    /// is served from cache. Per-list failures are swallowed (nothing is cached for a failed list, so
    /// it is retried on demand) and never fail the whole prefetch.
    /// </summary>
    public Task PrefetchAsync(IEnumerable<string> listIds, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var listId in listIds.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(listId))
                continue;
            tasks.Add(SwallowAsync(GetAsync(listId, ct)));
        }
        return Task.WhenAll(tasks);

        static async Task SwallowAsync(Task<IReadOnlyList<StatusOption>> task)
        {
            try { await task.ConfigureAwait(false); }
            catch { /* best-effort warm-up; on-demand GetAsync will surface real errors */ }
        }
    }

    private bool TryGetFreshLocked(string listId, out IReadOnlyList<StatusOption> statuses)
    {
        if (_entries.TryGetValue(listId, out var entry) && _clock.GetUtcNow() - entry.FetchedAt < _ttl)
        {
            statuses = entry.Statuses;
            return true;
        }
        statuses = [];
        return false;
    }

    private async Task<IReadOnlyList<StatusOption>> FetchAndStoreAsync(string listId, CancellationToken ct)
    {
        // Yield first so the body runs on a continuation rather than synchronously inside the caller's
        // lock in GetAsync — this keeps the store/remove `lock (_gate)` below from ever nesting.
        await Task.Yield();
        try
        {
            var statuses = await _fetch(listId, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _entries[listId] = new Entry(statuses, _clock.GetUtcNow());
                PersistLocked();
            }
            return statuses;
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(listId);
        }
    }

    // Warm the in-memory entries from the persisted snapshot, keeping each entry's captured fetch time
    // so TryGetFreshLocked's TTL check still applies (a persisted entry past its TTL is simply a miss).
    private void WarmFromStore()
    {
        if (_store is null)
            return;

        StatusCacheDocument? doc;
        try
        {
            doc = _store.Load<StatusCacheDocument>(StateKeys.Statuses);
        }
        catch (JsonException)
        {
            // A corrupt/truncated snapshot (quit or crash mid-write) is a miss, never a crash — this runs
            // before the UI loop, so a throw would brick launch. On-demand fetches repopulate it.
            return;
        }

        // Missing document, a different workspace, or an incompatible schema all mean "no warm cache".
        if (doc is null
            || doc.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(doc.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            return;

        foreach (var entry in doc.Entries)
        {
            if (!string.IsNullOrEmpty(entry.ListId) && entry.Statuses is not null)
                _entries[entry.ListId] = new Entry(
                    entry.Statuses, DateTimeOffset.FromUnixTimeMilliseconds(entry.FetchedAtMs));
        }
    }

    // Caller holds _gate. Rewrites the whole snapshot (the list count is tiny). Serialising the write
    // under the lock satisfies IStateStore's "serialise access per key" contract — concurrent fetches
    // (prefetch runs several at once) each store under the same lock. Best-effort: a failed write
    // (read-only / full disk) must never break the picker; the in-memory cache lives on and the next
    // fetch retries the write.
    private void PersistLocked()
    {
        if (_store is null)
            return;
        try
        {
            var doc = new StatusCacheDocument(
                CurrentSchemaVersion,
                _workspaceId,
                _entries.Select(kv => new StatusCacheEntryDto(
                    kv.Key, kv.Value.Statuses, kv.Value.FetchedAt.ToUnixTimeMilliseconds())).ToList());
            _store.Save(StateKeys.Statuses, doc);
        }
        catch
        {
            // Swallowed — see contract note above.
        }
    }
}
