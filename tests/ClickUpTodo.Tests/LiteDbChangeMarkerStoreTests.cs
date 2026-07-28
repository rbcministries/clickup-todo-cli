using ClickUpTodo.Configuration;
using LiteDB;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the producer half of the cross-process nudge channel (#294): the
/// <see cref="LiteDbChangeMarkerStore"/> upserts one marker per task (supersede, not append), allocates
/// a unique monotonic <see cref="ChangeMarker.Seq"/> even under concurrent writers, keeps the table
/// bounded (TTL + count cap), and swallows store failures so a nudge never breaks the edit it rides on.
/// The store is built through <see cref="LiteDbStateStore.CreateChangeMarkerStore"/> so the same seam
/// the composition root uses is exercised.
/// </summary>
public sealed class LiteDbChangeMarkerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "state.db");

    /// <summary>A clock whose "now" can be advanced, for exercising the TTL trim.</summary>
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A clock that throws on its first <paramref name="throwFirst"/> reads — a stand-in for a
    /// transient write-path failure (#410) that clears on retry — then returns a fixed time.</summary>
    private sealed class TransientlyThrowingClock(int throwFirst) : TimeProvider
    {
        private int _remaining = throwFirst;

        /// <summary>Total <see cref="GetUtcNow"/> calls seen, including the ones that threw.</summary>
        public int Reads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            if (_remaining > 0)
            {
                _remaining--;
                throw new IOException("transient reopen failure");
            }

            return DateTimeOffset.UnixEpoch;
        }
    }

    private static readonly string[] StatusFields = ["status"];

    [Fact]
    public void Record_ThenReadAll_RoundTripsAllFields()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var store = backend.CreateChangeMarkerStore("inst-1");

        store.Record("t1", serverDateUpdatedMs: 1234, changedFields: StatusFields);

        var marker = Assert.Single(store.ReadAll());
        Assert.Equal("t1", marker.TaskId);
        Assert.Equal(1, marker.Seq);
        Assert.Equal(1234, marker.ServerDateUpdatedMs);
        Assert.Equal(["status"], marker.ChangedFields);
        Assert.Equal("inst-1", marker.InstanceId);
    }

    [Fact]
    public void Record_SameTaskTwice_SupersedesRow_KeepingNewerSeqAndValue()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var store = backend.CreateChangeMarkerStore("inst-1");

        store.Record("t1", serverDateUpdatedMs: 100, changedFields: ["status"]);
        store.Record("t1", serverDateUpdatedMs: 200, changedFields: ["priority"]);

        // One row per task — the re-edit supersedes rather than appends.
        var marker = Assert.Single(store.ReadAll());
        Assert.Equal(2, marker.Seq);
        Assert.Equal(200, marker.ServerDateUpdatedMs);
        Assert.Equal(["priority"], marker.ChangedFields);
    }

    [Fact]
    public void Record_DistinctTasks_AllocatesMonotonicSeq()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var store = backend.CreateChangeMarkerStore("inst-1");

        store.Record("a", null, StatusFields);
        store.Record("b", null, StatusFields);
        store.Record("c", null, StatusFields);

        Assert.Equal([1L, 2L, 3L], store.ReadAll().Select(m => m.Seq));
        Assert.Equal(["a", "b", "c"], store.ReadAll().Select(m => m.TaskId));
    }

    [Fact]
    public void Record_EmptyTaskId_IsNoOp()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var store = backend.CreateChangeMarkerStore("inst-1");

        store.Record("", 1, StatusFields);

        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void Record_NullServerDate_AndEmptyChangedFields_StoreCleanly()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var store = backend.CreateChangeMarkerStore("inst-1");

        store.Record("t1", serverDateUpdatedMs: null, changedFields: []);

        var marker = Assert.Single(store.ReadAll());
        Assert.Null(marker.ServerDateUpdatedMs);
        Assert.Empty(marker.ChangedFields);
    }

    [Fact]
    public void ConcurrentRecords_AcrossTwoConnections_NeverCollideOnSeq()
    {
        // Two store instances over one file = two LiteDB connections; in Shared mode they contend on the
        // cross-process mutex, so this exercises the exact path two tabs would take. Every emission's seq
        // must be unique and the whole set contiguous (1..N) — no collisions, no gaps.
        using var backendA = new LiteDbStateStore(DbPath);
        using var backendB = new LiteDbStateStore(DbPath);
        var storeA = backendA.CreateChangeMarkerStore("A");
        var storeB = backendB.CreateChangeMarkerStore("B");

        const int threadsPerStore = 4;
        const int perThread = 25;

        void Hammer(IChangeMarkerStore store, string tag)
        {
            Parallel.For(0, threadsPerStore, t =>
            {
                for (var i = 0; i < perThread; i++)
                    store.Record($"{tag}-{t}-{i}", serverDateUpdatedMs: i, changedFields: StatusFields);
            });
        }

        Parallel.Invoke(
            () => Hammer(storeA, "A"),
            () => Hammer(storeB, "B"));

        // Read back from a third connection so nothing is served from an in-memory cache.
        using var reader = new LiteDbStateStore(DbPath);
        var seqs = reader.CreateChangeMarkerStore("R").ReadAll().Select(m => m.Seq).ToList();

        var total = threadsPerStore * perThread * 2; // distinct task ids, so no supersede
        Assert.Equal(total, seqs.Count);
        Assert.Equal(total, seqs.Distinct().Count());               // no collisions
        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), seqs.OrderBy(s => s)); // contiguous 1..N
    }

    [Fact]
    public void Trim_DropsMarkersOlderThanTtl_OnWrite()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        using var backend = new LiteDbStateStore(DbPath);
        var store = backend.CreateChangeMarkerStore(
            "inst-1", new ChangeMarkerOptions(Ttl: TimeSpan.FromMinutes(10), MaxEntries: 500), clock);

        store.Record("old", 1, StatusFields);
        clock.Advance(TimeSpan.FromMinutes(11)); // push "old" past the TTL window
        store.Record("fresh", 2, StatusFields);  // the write that triggers the trim

        var marker = Assert.Single(store.ReadAll());
        Assert.Equal("fresh", marker.TaskId);
    }

    [Fact]
    public void Trim_EnforcesCountCap_KeepingNewestBySeq()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var store = backend.CreateChangeMarkerStore(
            "inst-1", new ChangeMarkerOptions(Ttl: TimeSpan.FromHours(1), MaxEntries: 3));

        for (var i = 1; i <= 5; i++)
            store.Record($"t{i}", i, StatusFields);

        // Newest 3 by seq survive; the two oldest are trimmed.
        Assert.Equal(["t3", "t4", "t5"], store.ReadAll().Select(m => m.TaskId));
    }

    [Fact]
    public void Markers_PersistAcrossStoreInstances_OverTheSameFile()
    {
        using (var backend = new LiteDbStateStore(DbPath))
            backend.CreateChangeMarkerStore("inst-1").Record("t1", 42, StatusFields);

        // A fresh connection (a later app launch, or another tab) sees the earlier marker.
        using var reopened = new LiteDbStateStore(DbPath);
        var marker = Assert.Single(reopened.CreateChangeMarkerStore("inst-2").ReadAll());
        Assert.Equal("t1", marker.TaskId);
        Assert.Equal(42, marker.ServerDateUpdatedMs);
    }

    [Fact]
    public void MarkersAndState_CoexistInTheSameDatabase()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var markers = backend.CreateChangeMarkerStore("inst-1");

        backend.Save(StateKeys.Config, new AppConfig { WorkspaceId = "ws" });
        markers.Record("t1", 1, StatusFields);

        Assert.Equal("ws", backend.Load<AppConfig>(StateKeys.Config)!.WorkspaceId);
        Assert.Single(markers.ReadAll());
    }

    [Fact]
    public void Record_WhenStoreFails_IsSwallowed_NotThrown()
    {
        // A nudge rides on an already-succeeded edit, so a store failure must never propagate. Forced
        // here by disposing the underlying database out from under the store (a memory-backed, non-shared
        // db so Dispose really closes it — a Shared file connection reopens per op and wouldn't fault).
        var db = new LiteDatabase(new MemoryStream());
        // Inject an instant, few-attempt retry policy so this always-failing write doesn't grind through
        // the default back-off sleeps (#410) — the behaviour under test is only that it's swallowed.
        var store = new LiteDbChangeMarkerStore(
            db, "inst-1", options: null, timeProvider: null,
            retryPolicy: new WriteRetryPolicy(maxAttempts: 2, delay: _ => { }));
        db.Dispose();

        var ex = Record.Exception(() => store.Record("t1", 1, StatusFields));
        Assert.Null(ex);
        Assert.Empty(store.ReadAll()); // a read failure degrades to empty, too
    }

    [Fact]
    public void Record_WriteFailsThenSucceeds_ReusesAllocatedSeq_NoBurnNoLoss()
    {
        // The crux of #410: when the write throws *after* the seq is allocated (a transient Shared-mode
        // reopen under load) and the retry then succeeds, it must re-run with the SAME seq — not allocate
        // a fresh one. Otherwise the first seq is burned (a gap) and, if every attempt failed, the marker
        // is lost. Induce exactly one transient failure via a clock that throws on its first read (the
        // read that stamps RecordedUtcMs, right after allocation), with an instant retry policy.
        using var db = new LiteDatabase(new MemoryStream());
        var clock = new TransientlyThrowingClock(throwFirst: 1);
        var store = new LiteDbChangeMarkerStore(
            db, "inst-1", options: null, timeProvider: clock,
            retryPolicy: new WriteRetryPolicy(maxAttempts: 5, delay: _ => { }));

        store.Record("t1", serverDateUpdatedMs: 100, changedFields: StatusFields);

        Assert.Equal(2, clock.Reads);                       // one throwing read + one that succeeded = a single retry
        var first = Assert.Single(store.ReadAll());
        Assert.Equal("t1", first.TaskId);
        Assert.Equal(1, first.Seq);                          // reused seq 1 — a re-allocation would show seq 2

        store.Record("t2", serverDateUpdatedMs: 200, changedFields: StatusFields); // a normal follow-up write

        // Contiguous 1..2: the retried first write neither burned seq 1 nor lost its marker.
        Assert.Equal([1L, 2L], store.ReadAll().OrderBy(m => m.Seq).Select(m => m.Seq));
    }

    [Fact]
    public void CreateChangeMarkerStore_RejectsEmptyInstanceId()
    {
        using var backend = new LiteDbStateStore(DbPath);
        Assert.Throws<ArgumentException>(() => backend.CreateChangeMarkerStore(""));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
