using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// A thread-safe, TTL'd cache of per-list status options, decoupled from the ClickUp client so it
/// can be exercised without the network. A list's statuses almost never change, so an entry stays
/// fresh for <paramref name="ttl"/> (default 10 minutes) before the next access refetches it.
/// Concurrent fetches for the same list are de-duplicated, so a prefetch already in flight is
/// awaited rather than duplicated when the user opens the picker.
/// </summary>
public sealed class StatusCache(
    Func<string, CancellationToken, Task<IReadOnlyList<StatusOption>>> fetch,
    TimeProvider? timeProvider = null,
    TimeSpan? ttl = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Dictionary<string, Task<IReadOnlyList<StatusOption>>> _inFlight = new();
    private readonly Lock _gate = new();

    private readonly record struct Entry(IReadOnlyList<StatusOption> Statuses, DateTimeOffset FetchedAt);

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
            var statuses = await fetch(listId, ct).ConfigureAwait(false);
            lock (_gate)
                _entries[listId] = new Entry(statuses, _clock.GetUtcNow());
            return statuses;
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(listId);
        }
    }
}
