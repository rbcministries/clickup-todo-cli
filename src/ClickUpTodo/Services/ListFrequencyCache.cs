using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>The persisted list-frequency pool. Carries the <see cref="WorkspaceId"/> it was captured
/// for — a mismatch on load is a clean miss (empty pool), so switching workspace never surfaces the
/// wrong lists (mirrors the assignee cache #155 / #124's reset-on-workspace-change) — and a
/// <see cref="SchemaVersion"/> guarding against an incompatible future shape. Each entry carries the
/// distinct task ids the list was seen on, so the count is reproducible and idempotent across restarts
/// (re-observing the same task never re-inflates it). Bounding the pool's growth over time (TTL /
/// eviction) is the epic-#118 cache-policy issue #124.</summary>
public sealed record ListFrequencyDocument(
    int SchemaVersion, string WorkspaceId, IReadOnlyList<ListFrequencyEntry> Entries);

/// <summary>
/// The stateful list-frequency cache (#238): owns the in-memory tally and persists it through the
/// <see cref="IStateStore"/> seam keyed per workspace, so the future List selector (#239) has a warm
/// candidate pool. A faithful mirror of the assignee-frequency cache (<see cref="AssigneeFrequencyCache"/>,
/// #155), with one deliberate difference — it owns <b>no fetch delegate</b>: the assignee cache
/// self-fetches workspace members in its deferred top-up, whereas the long-tail lists here are supplied
/// by the scheduled list-hierarchy walk (#236, <see cref="TaskService.ResolveWorkspaceListsAsync"/>)
/// which already runs on the refresh loop and hands its discovered lists to <see cref="Seed"/>.
/// <para>
/// Counting is by <b>distinct task id</b> (see <see cref="ListFrequency.Accumulate"/>), so
/// <see cref="RecordFromTasks"/> can be called on every refresh with the same working set without
/// inflating any list or rewriting the store — a steady-state poll that adds no new (list, task) pair
/// is a no-op.
/// </para>
/// </summary>
public sealed class ListFrequencyCache
{
    /// <summary>The persisted-shape version; bump when <see cref="ListFrequencyDocument"/> changes
    /// incompatibly so an old document is discarded rather than mis-read.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly IStateStore _store;
    private readonly string _workspaceId;
    private readonly Dictionary<string, ListFrequencyEntry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <param name="store">Persistence backend (shared with the rest of the app's state).</param>
    /// <param name="workspaceId">The workspace this pool is scoped to; a persisted document for a
    /// different workspace is ignored on load.</param>
    public ListFrequencyCache(IStateStore store, string workspaceId)
    {
        _store = store;
        _workspaceId = workspaceId ?? "";
        Load();
    }

    /// <summary>Number of candidate lists currently pooled (test/diagnostic hook).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    private void Load()
    {
        var doc = _store.Load<ListFrequencyDocument>(StateKeys.Lists);
        // A missing document, a different workspace, or an incompatible schema all mean "no warm pool" —
        // start empty rather than surface stale/foreign lists.
        if (doc is null
            || doc.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(doc.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            return;

        foreach (var entry in doc.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Name))
                _entries[entry.Id] = entry;
        }
    }

    /// <summary>Record the home lists on the just-loaded working set and persist if anything changed.
    /// Cheap and idempotent — safe to call from the refresh callback on every poll; a working set with
    /// no new (list, task) pair neither inflates the pool nor touches the store.</summary>
    public void RecordFromTasks(IReadOnlyList<TaskItem> tasks)
    {
        lock (_gate)
        {
            if (ListFrequency.Accumulate(_entries, tasks))
                Persist();
        }
    }

    /// <summary>
    /// Backfill the long tail: add <paramref name="lists"/> discovered by the scheduled list-hierarchy
    /// walk (#236) as count-0 candidates so the selector's empty state can offer lists the task feed
    /// never surfaces, without disturbing any list already tallied. Persists only when it added anyone.
    /// Called off the UI thread from the walk step; the <see cref="_gate"/> serialises it against
    /// <see cref="RecordFromTasks"/> on the UI thread.
    /// </summary>
    public void Seed(IReadOnlyList<NamedEntity> lists)
    {
        lock (_gate)
        {
            if (ListFrequency.Seed(_entries, lists))
                Persist();
        }
    }

    /// <summary>Top <paramref name="n"/> most-frequent candidate lists, excluding <paramref name="exclude"/>
    /// (typically the task's current list). See <see cref="ListFrequency.TopMostFrequent"/>.</summary>
    public IReadOnlyList<NamedEntity> TopMostFrequent(int n, ISet<string>? exclude = null)
    {
        lock (_gate)
            return ListFrequency.TopMostFrequent(_entries.Values, n, exclude);
    }

    /// <summary>Candidates whose name matches <paramref name="query"/> (case-insensitive substring;
    /// blank ⇒ the whole ranked pool). See <see cref="ListFrequency.Match"/>.</summary>
    public IReadOnlyList<NamedEntity> Match(string? query, ISet<string>? exclude = null)
    {
        lock (_gate)
            return ListFrequency.Match(_entries.Values, query, exclude);
    }

    // Caller holds _gate. Serialising the write under the lock is deliberate: it satisfies IStateStore's
    // "caller must serialise concurrent access to a key" contract (RecordFromTasks runs on the UI
    // thread, the walk's Seed completes on a background thread). Writes are rare — only on a genuinely
    // new (list, task) pair, a name change, or a newly discovered list — so holding the lock across the
    // write is not a hot path.
    private void Persist()
    {
        try
        {
            var doc = new ListFrequencyDocument(
                CurrentSchemaVersion, _workspaceId, _entries.Values.ToList());
            _store.Save(StateKeys.Lists, doc);
        }
        catch
        {
            // Best-effort warm-cache persistence: a failed write (read-only / full disk) must never break
            // the refresh loop that calls RecordFromTasks on the UI thread. The pool lives on in memory;
            // the next change retries.
        }
    }
}
