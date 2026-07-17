using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>The persisted assignee-frequency pool. Carries the <see cref="WorkspaceId"/> it was
/// captured for — a mismatch on load is a clean miss (empty pool), so switching workspace never
/// surfaces the wrong people (#155 note; aligns with #124's reset-on-workspace-change) — and a
/// <see cref="SchemaVersion"/> guarding against an incompatible future shape. Each entry carries the
/// distinct task ids the person was seen on, so the count is reproducible and idempotent across
/// restarts (re-observing the same task never re-inflates it). Bounding the pool's growth over time
/// (TTL / eviction) is the epic-#118 cache-policy issue #124.</summary>
public sealed record AssigneeFrequencyDocument(
    int SchemaVersion, string WorkspaceId, IReadOnlyList<AssigneeFrequencyEntry> Entries);

/// <summary>
/// The stateful assignee-frequency cache (#155): owns the in-memory tally, persists it through the
/// <see cref="IStateStore"/> seam keyed per workspace, and runs a best-effort deferred top-up from
/// the workspace members so the future Assignees pane (#158) has a full candidate pool even when few
/// people ride along on the loaded tasks. The pure tally / ranking / matching rules live in
/// <see cref="AssigneeFrequency"/>; this class is the glue (load, persist, fetch).
/// <para>
/// Counting is by <b>distinct task id</b> (see <see cref="AssigneeFrequency.Accumulate"/>), so
/// <see cref="RecordFromTasks"/> can be called on every refresh with the same working set without
/// inflating anyone or rewriting the store — a steady-state poll that adds no new (person, task) pair
/// is a no-op.
/// </para>
/// </summary>
public sealed class AssigneeFrequencyCache
{
    /// <summary>The persisted-shape version; bump when <see cref="AssigneeFrequencyDocument"/> changes
    /// incompatibly so an old document is discarded rather than mis-read.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly IStateStore _store;
    private readonly string _workspaceId;
    private readonly Func<CancellationToken, Task<IReadOnlyList<WorkspaceMember>>> _fetchMembers;
    private readonly Dictionary<long, AssigneeFrequencyEntry> _entries = [];
    private readonly Lock _gate = new();
    private bool _toppedUp;

    /// <param name="store">Persistence backend (shared with the rest of the app's state).</param>
    /// <param name="workspaceId">The workspace this pool is scoped to; a persisted document for a
    /// different workspace is ignored on load.</param>
    /// <param name="fetchMembers">Deferred workspace-members fetch for the top-up (off the UI thread;
    /// failures are non-fatal).</param>
    public AssigneeFrequencyCache(
        IStateStore store,
        string workspaceId,
        Func<CancellationToken, Task<IReadOnlyList<WorkspaceMember>>> fetchMembers)
    {
        _store = store;
        _workspaceId = workspaceId ?? "";
        _fetchMembers = fetchMembers;
        Load();
    }

    /// <summary>Number of candidates currently pooled (test/diagnostic hook).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    private void Load()
    {
        AssigneeFrequencyDocument? doc;
        try
        {
            doc = _store.Load<AssigneeFrequencyDocument>(StateKeys.Assignees);
        }
        catch
        {
            // Any read failure — a malformed payload (older/incompatible shape or a torn write from a
            // concurrent tab), or a LiteDB contention error under multi-tab startup (#293) — is a clean
            // miss, never a crash: Load() runs synchronously in the constructor, so a throw here would
            // brick the pane's owner. Start empty; the pool re-warms from the next poll.
            return;
        }
        // A missing document, a different workspace, or an incompatible schema all mean "no warm
        // pool" — start empty rather than surface stale/foreign people.
        if (doc is null
            || doc.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(doc.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            return;

        foreach (var entry in doc.Entries)
        {
            if (entry.Id > 0 && !string.IsNullOrWhiteSpace(entry.Name))
                _entries[entry.Id] = entry;
        }
    }

    /// <summary>Record the assignees on the just-loaded working set and persist if anything changed.
    /// Cheap and idempotent — safe to call from the refresh callback on every poll; a working set with
    /// no new (person, task) pair neither inflates the pool nor touches the store.</summary>
    public void RecordFromTasks(IReadOnlyList<TaskItem> tasks)
    {
        lock (_gate)
        {
            if (AssigneeFrequency.Accumulate(_entries, tasks))
                Persist();
        }
    }

    /// <summary>
    /// Best-effort one-shot top-up: when the pool has fewer than <paramref name="minCandidates"/>
    /// people, fetch the workspace members and seed them (count 0) so the pane's empty state can still
    /// fill. Attempts at most once per instance, off the UI thread; a fetch failure is swallowed (the
    /// pool simply stays as-is). Persists only when it added anyone.
    /// </summary>
    public async Task TopUpAsync(int minCandidates, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_toppedUp)
                return;
            _toppedUp = true; // consume the one-shot on first entry, so it never fetches twice.
            if (_entries.Count >= minCandidates)
                return;
        }

        IReadOnlyList<WorkspaceMember> members;
        try
        {
            members = await _fetchMembers(ct).ConfigureAwait(false);
        }
        catch
        {
            return; // deferred, non-fatal — the pane falls back to whatever rode along on the tasks.
        }

        var seeds = members
            .Select(m => new TaskAssignee(m.Id, MemberName(m)))
            .ToList();

        lock (_gate)
        {
            if (AssigneeFrequency.Seed(_entries, seeds))
                Persist();
        }
    }

    /// <summary>Top <paramref name="n"/> most-frequent candidates, excluding <paramref name="exclude"/>
    /// (typically the task's current assignees). See <see cref="AssigneeFrequency.TopMostFrequent"/>.</summary>
    public IReadOnlyList<TaskAssignee> TopMostFrequent(int n, ISet<long>? exclude = null)
    {
        lock (_gate)
            return AssigneeFrequency.TopMostFrequent(_entries.Values, n, exclude);
    }

    /// <summary>Candidates whose name matches <paramref name="query"/> (case-insensitive substring;
    /// blank ⇒ the whole ranked pool). See <see cref="AssigneeFrequency.Match"/>.</summary>
    public IReadOnlyList<TaskAssignee> Match(string? query, ISet<long>? exclude = null)
    {
        lock (_gate)
            return AssigneeFrequency.Match(_entries.Values, query, exclude);
    }

    // Caller holds _gate. Serialising the write under the lock is deliberate: it satisfies IStateStore's
    // "caller must serialise concurrent access to a key" contract (RecordFromTasks runs on the UI
    // thread, the top-up completes on a background thread). Writes are rare — only on a genuinely new
    // (person, task) pair or a name change — so holding the lock across the write is not a hot path.
    private void Persist()
    {
        // Re-read the current document and union in any entries a concurrent tab learned after we loaded
        // (#293), so this whole-set write doesn't clobber the other process's additions. A missing,
        // foreign-workspace, incompatible, or torn/corrupt document is ignored — we then just write our
        // own set. Merging our own just-written document back is a no-op (Merge is idempotent).
        try
        {
            var disk = _store.Load<AssigneeFrequencyDocument>(StateKeys.Assignees);
            if (disk is not null
                && disk.SchemaVersion == CurrentSchemaVersion
                && string.Equals(disk.WorkspaceId, _workspaceId, StringComparison.Ordinal))
                AssigneeFrequency.Merge(_entries, disk.Entries);
        }
        catch
        {
            // A torn/hand-tampered disk document, or a LiteDB contention error on the re-read (#293) —
            // skip the merge and write our own set. Best-effort; must never crash the refresh loop.
        }

        try
        {
            var doc = new AssigneeFrequencyDocument(
                CurrentSchemaVersion, _workspaceId, _entries.Values.ToList());
            _store.Save(StateKeys.Assignees, doc);
        }
        catch
        {
            // Best-effort warm-cache persistence: a failed write (read-only / full disk) must never
            // break the refresh loop that calls RecordFromTasks on the UI thread. The pool lives on in
            // memory; the next change retries.
        }
    }

    /// <summary>A display name for a workspace member: username, else the email's local part, else
    /// empty (a nameless member is skipped by <see cref="AssigneeFrequency.Seed"/>).</summary>
    private static string MemberName(WorkspaceMember member)
    {
        if (!string.IsNullOrWhiteSpace(member.Username))
            return member.Username.Trim();
        var email = member.Email?.Trim() ?? "";
        var at = email.IndexOf('@');
        return at >= 0 ? email[..at] : email; // local part; "@x" → "" (skipped as nameless).
    }
}
