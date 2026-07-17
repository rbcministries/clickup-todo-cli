using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>The persisted list-frequency pool. Carries the <see cref="WorkspaceId"/> it was captured
/// for — a mismatch on load is a clean miss (empty pool), so switching workspace never surfaces the
/// wrong lists (mirrors #155; aligns with #124's reset-on-workspace-change) — and a
/// <see cref="SchemaVersion"/> guarding against an incompatible future shape. Each entry carries the
/// distinct task ids the list was seen hosting, so the count is reproducible and idempotent across
/// restarts (re-observing the same task never re-inflates it). Bounding the pool's growth over time
/// (TTL / eviction) is the epic-#118 cache-policy issue #124.</summary>
public sealed record ListFrequencyDocument(
    int SchemaVersion, string WorkspaceId, IReadOnlyList<ListFrequencyEntry> Entries);

/// <summary>
/// The stateful list-frequency cache (#238): owns the in-memory tally, persists it through the
/// <see cref="IStateStore"/> seam keyed per workspace, and takes two feeds so the future List selector
/// (#239) has a full candidate pool. The pure tally / ranking / matching rules live in
/// <see cref="ListFrequency"/>; this class is the glue (load, persist).
/// <para>
/// Unlike the assignee cache (#155) this class owns <b>no</b> fetch delegate: the long-tail backfill
/// comes from the scheduled list-hierarchy walk (#236), which runs on the refresh loop and pushes its
/// enumerated lists in via <see cref="Seed"/> — the cache never triggers a fetch of its own.
/// </para>
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

    /// <summary>Number of candidates currently pooled (test/diagnostic hook).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    private void Load()
    {
        ListFrequencyDocument? doc;
        try
        {
            doc = _store.Load<ListFrequencyDocument>(StateKeys.Lists);
        }
        catch (JsonException)
        {
            // A malformed payload (an older/incompatible shape, or a torn write from a concurrent tab
            // — #293) is a clean miss, never a crash: Load() runs synchronously in the constructor, so
            // a throw here would brick the selector's owner. Start empty; the pool re-warms from the
            // next poll. Mirrors the corrupt-cache handling in TaskCache/FeedCache.
            return;
        }
        // A missing document, a different workspace, or an incompatible schema all mean "no warm pool"
        // — start empty rather than surface stale/foreign lists.
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

    /// <summary>Record the home lists of the just-loaded working set and persist if anything changed.
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

    /// <summary>Seed the long tail: add <paramref name="lists"/> as count-0 candidates so lists the
    /// task feed never surfaces are still searchable/selectable. The intake the scheduled list-hierarchy
    /// walk (#236) pushes into — additive only: lists already tallied keep their real count and name.
    /// Idempotent (the walk republishes its growing known-set each step, and re-seeding already-known
    /// lists is a no-op) and cheap to call on every walk step; persists only when it added a genuinely
    /// new list.</summary>
    public void SeedLists(IReadOnlyList<NamedEntity> lists)
    {
        lock (_gate)
        {
            if (ListFrequency.Seed(_entries, lists))
                Persist();
        }
    }

    /// <summary>Top <paramref name="n"/> most-frequent candidates, excluding <paramref name="exclude"/>.
    /// See <see cref="ListFrequency.TopMostFrequent"/>.</summary>
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
    // thread, SeedLists completes on the background refresh thread that runs the walk). Writes are rare
    // — only on a genuinely new (list, task) pair, a name change, or a newly-discovered list — so
    // holding the lock across the write is not a hot path.
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
            // Best-effort warm-cache persistence: a failed write (read-only / full disk) must never
            // break the refresh loop that calls RecordFromTasks on the UI thread. The pool lives on in
            // memory; the next change retries.
        }
    }
}
