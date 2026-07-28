using LiteDB;

namespace ClickUpTodo.Configuration;

/// <summary>
/// LiteDB-backed <see cref="IChangeMarkerStore"/> (#294) — the producer half of the cross-process
/// nudge channel. Writes markers into a <c>changes</c> collection in the shared <c>state.db</c>
/// (the same file <see cref="LiteDbStateStore"/> opens), keyed by task so a re-edit supersedes rather
/// than appends.
/// <para>
/// <b>Atomic <see cref="ChangeMarker.Seq"/> allocation.</b> Each emission's <c>seq</c> must be unique
/// and monotonic even when two instances write at the same instant. Rather than a read-counter →
/// write-counter pair (two operations with a cross-process race window), the counter is a LiteDB
/// <b>auto-increment</b> <c>_id</c> in a tiny <c>changes_seq</c> collection: a single
/// <see cref="ILiteCollection{T}.Insert(T)"/> allocates the next id atomically under LiteDB's
/// collection lock — which, in <see cref="ConnectionType.Shared"/> mode, is the cross-process mutex —
/// so no two writers can ever get the same <c>seq</c>. The marker itself is a last-writer-wins upsert
/// keyed by task, which is exactly the desired supersede semantics (a higher <c>seq</c> wins), so the
/// allocation and the upsert need not be a single joint transaction.
/// </para>
/// <para>
/// Every write is wrapped so a LiteDB error (contention, full disk) is ultimately <b>swallowed</b>: a
/// nudge rides on an already-succeeded user edit and must never surface as a failure, exactly as #293
/// established for the shared store. Because <see cref="ConnectionType.Shared"/> reopens the file per
/// operation, a reopen can transiently fail under load (two tabs writing at once, a busy machine); to
/// keep a transient blip from silently dropping a nudge (#410) the write is retried a few times
/// (<see cref="WriteRetryPolicy"/>) before it gives up. The seq is allocated once and reused across
/// retries, so a retried write neither collides nor leaves a gap. An in-process lock serialises this
/// process's own emissions so the seq-table watermark trim and the count/TTL trims stay orderly.
/// </para>
/// This store does <b>not</b> own the <see cref="LiteDatabase"/> — it shares the connection
/// <see cref="LiteDbStateStore"/> holds for the process lifetime and must not dispose it.
/// </summary>
public sealed class LiteDbChangeMarkerStore : IChangeMarkerStore
{
    private const string ChangesCollection = "changes";
    private const string SeqCollection = "changes_seq";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<MarkerDocument> _changes;
    private readonly ILiteCollection<SeqDocument> _seq;
    private readonly ChangeMarkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly WriteRetryPolicy _retry;
    private readonly object _gate = new();

    /// <inheritdoc/>
    public string InstanceId { get; }

    /// <param name="db">The shared <c>state.db</c> connection (owned by <see cref="LiteDbStateStore"/>;
    /// not disposed here).</param>
    /// <param name="instanceId">This process's id, stamped on every marker (#295). Must be non-empty.</param>
    /// <param name="options">Table bounds (TTL + count cap). Defaults to <see cref="ChangeMarkerOptions.Default"/>.</param>
    /// <param name="timeProvider">Clock for TTL aging. Defaults to <see cref="TimeProvider.System"/>.</param>
    public LiteDbChangeMarkerStore(
        LiteDatabase db, string instanceId, ChangeMarkerOptions? options = null, TimeProvider? timeProvider = null)
        : this(db, instanceId, options, timeProvider, retryPolicy: null)
    {
    }

    /// <summary>Test seam (#410): lets a unit test inject an instant/short-circuit retry policy so the
    /// write's retry-and-reuse-seq behaviour is verifiable deterministically and without real back-off
    /// sleeps.</summary>
    internal LiteDbChangeMarkerStore(
        LiteDatabase db, string instanceId, ChangeMarkerOptions? options, TimeProvider? timeProvider,
        WriteRetryPolicy? retryPolicy)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrEmpty(instanceId))
            throw new ArgumentException("A non-empty instance id is required.", nameof(instanceId));
        InstanceId = instanceId;
        _options = options ?? ChangeMarkerOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retry = retryPolicy ?? new WriteRetryPolicy();

        _changes = _db.GetCollection<MarkerDocument>(ChangesCollection);
        _seq = _db.GetCollection<SeqDocument>(SeqCollection);
        // Ordering, trimming, and the consumer's cursor scan (#295) are all by Seq.
        _changes.EnsureIndex(x => x.Seq);
    }

    /// <inheritdoc/>
    public void Record(string taskId, long? serverDateUpdatedMs, IReadOnlyList<string> changedFields)
    {
        if (string.IsNullOrEmpty(taskId))
            return; // a marker with no task is meaningless — nothing to nudge on.

        lock (_gate)
        {
            // Allocate the seq once and reuse it across retries: the marker upsert is idempotent on
            // (taskId, seq), so a retried write re-runs with the *same* seq and never burns a number
            // (which would leave a gap). A seq is allocated only once the allocation itself succeeds
            // (see AllocateSeq), so a failed-then-retried allocation doesn't skip either.
            // The retry (incl. its short backoff) runs under _gate: the whole write must be serialised
            // so the seq allocation, marker upsert, and trims stay ordered. The backoff only ever runs
            // on a failing write and is bounded (tens of ms worst case), so briefly holding the gate is
            // acceptable for a background nudge.
            long? seq = null;
            _retry.Run(() =>
            {
                seq ??= AllocateSeq();
                var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                _changes.Upsert(new MarkerDocument
                {
                    TaskId = taskId,
                    Seq = seq.Value,
                    ServerDateUpdatedMs = serverDateUpdatedMs,
                    ChangedFields = changedFields is { Count: > 0 } ? [.. changedFields] : [],
                    InstanceId = InstanceId,
                    RecordedUtcMs = nowMs,
                });
                Trim(nowMs);
            });
            // If every attempt threw, the nudge is dropped — best-effort, exactly as before: a nudge
            // that can't persist must never break the already-succeeded edit it rides on. The retry
            // just means a transient Shared-mode contention blip no longer drops it on the first try.
        }
    }

    /// <summary>
    /// Allocates the next monotonic seq via LiteDB auto-increment: a single <see cref="Insert"/> into the
    /// <c>changes_seq</c> collection returns a freshly-assigned <c>_id</c>. The collection is then trimmed
    /// to just the watermark row (<c>_id &lt; new</c> deleted) so it never grows — the max row is always
    /// kept, so auto-increment keeps climbing from it rather than resetting.
    /// </summary>
    private long AllocateSeq()
    {
        var id = _seq.Insert(new SeqDocument()).AsInt64;
        // Keep only the just-allocated watermark row; older rows are dead weight. Never deletes the max,
        // so the next auto-id is always id+1 (a full-empty collection could reset the sequence).
        // Best-effort: the id is already allocated, so a trim failure must not surface as an allocation
        // failure — otherwise the write's retry (#410) would re-allocate and burn this id, leaving a gap.
        // Stale rows are harmless (the max survives, so auto-increment keeps climbing).
        try { _seq.DeleteMany(x => x.Id < id); }
        catch { /* best-effort watermark trim */ }
        return id;
    }

    /// <summary>Enforces the TTL and count caps (#294). Called on the write path, holding <see cref="_gate"/>.</summary>
    private void Trim(long nowMs)
    {
        // TTL: drop markers older than the window. The row just written carries RecordedUtcMs = nowMs and
        // the cutoff is strictly earlier, so a fresh marker is never trimmed by its own write.
        var cutoff = nowMs - (long)_options.Ttl.TotalMilliseconds;
        _changes.DeleteMany(x => x.RecordedUtcMs < cutoff);

        // Count cap: keep the newest MaxEntries by Seq; trim the oldest overflow.
        var overflow = _changes.Count() - _options.MaxEntries;
        if (overflow > 0)
        {
            var oldest = _changes.Query().OrderBy(x => x.Seq).Limit(overflow).ToList();
            foreach (var doc in oldest)
                _changes.Delete(doc.TaskId);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ChangeMarker> ReadAll()
    {
        try
        {
            return _changes.Query()
                .OrderBy(x => x.Seq)
                .ToList()
                .Select(d => new ChangeMarker(
                    d.TaskId, d.Seq, d.ServerDateUpdatedMs, d.ChangedFields ?? [], d.InstanceId, d.RecordedUtcMs))
                .ToList();
        }
        catch
        {
            // A read failure is a clean empty result, matching the store's cache-miss-on-read discipline.
            return [];
        }
    }

    /// <summary>The stored marker shape (keyed by task id).</summary>
    private sealed class MarkerDocument
    {
        [BsonId] public string TaskId { get; set; } = string.Empty;
        public long Seq { get; set; }
        public long? ServerDateUpdatedMs { get; set; }
        public List<string> ChangedFields { get; set; } = [];
        public string InstanceId { get; set; } = string.Empty;
        public long RecordedUtcMs { get; set; }
    }

    /// <summary>The auto-increment seq allocator's row shape — an id and nothing else.</summary>
    private sealed class SeqDocument
    {
        [BsonId] public long Id { get; set; }
    }
}
