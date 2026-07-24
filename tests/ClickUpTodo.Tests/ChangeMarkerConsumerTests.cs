using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The nudge-channel consumer (#295): a monotonic cursor that turns another instance's change markers
/// into a per-task fetch list — skipping its own writes, out-of-view tasks, and versions it already
/// holds, and never replaying history on a fresh tab.
/// </summary>
public sealed class ChangeMarkerConsumerTests
{
    private const string Me = "instance-me";
    private const string Other = "instance-other";

    /// <summary>Builds a marker; server time defaults to null (a "comment"-style marker that can't be
    /// version-suppressed) so a test opts in to a server time only when it exercises suppression.</summary>
    private static ChangeMarker Marker(string taskId, long seq, string instanceId, long? serverMs = null)
        => new(taskId, seq, serverMs, [], instanceId, RecordedUtcMs: seq);

    /// <summary>Everything is in view unless a test says otherwise.</summary>
    private static bool AllInView(string _) => true;

    /// <summary>No held version known — so a fetch is never version-suppressed.</summary>
    private static long? NoHeldVersion(string _) => null;

    // ── Fresh-tab init (edge case 1) ────────────────────────────────────────

    [Fact]
    public void Initialize_EmptyStore_LeavesCursorAtZero()
    {
        var consumer = new ChangeMarkerConsumer(Me);

        consumer.Initialize([]);

        Assert.Equal(0, consumer.Cursor);
    }

    [Fact]
    public void Initialize_SetsCursorToMaxSeq_SoAFreshTabDoesNotReplayHistory()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var history = new[]
        {
            Marker("a", 3, Other),
            Marker("b", 7, Other),
            Marker("c", 5, Other),
        };

        consumer.Initialize(history);
        Assert.Equal(7, consumer.Cursor);

        // Re-scanning that same history now yields nothing — no burst of redundant fetches on launch.
        var fetched = consumer.Advance(history, AllInView, NoHeldVersion);
        Assert.Empty(fetched);
        Assert.Equal(7, consumer.Cursor);
    }

    // ── Basic scan + cursor advancement ─────────────────────────────────────

    [Fact]
    public void Advance_ForeignMarkerInView_IsFetched_AndCursorAdvances()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("task-1", 1, Other) };

        var fetched = consumer.Advance(markers, AllInView, NoHeldVersion);

        Assert.Equal(["task-1"], fetched);
        Assert.Equal(1, consumer.Cursor);
    }

    [Fact]
    public void Advance_OnlyProcessesMarkersPastTheCursor()
    {
        var consumer = new ChangeMarkerConsumer(Me);

        var first = consumer.Advance([Marker("a", 1, Other), Marker("b", 2, Other)], AllInView, NoHeldVersion);
        Assert.Equal(["a", "b"], first);
        Assert.Equal(2, consumer.Cursor);

        // A second scan that still includes the already-seen rows plus a new one only yields the new one.
        var second = consumer.Advance(
            [Marker("a", 1, Other), Marker("b", 2, Other), Marker("c", 3, Other)], AllInView, NoHeldVersion);
        Assert.Equal(["c"], second);
        Assert.Equal(3, consumer.Cursor);
    }

    [Fact]
    public void Advance_UnorderedInput_IsProcessedBySeq()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("c", 3, Other), Marker("a", 1, Other), Marker("b", 2, Other) };

        var fetched = consumer.Advance(markers, AllInView, NoHeldVersion);

        Assert.Equal(["a", "b", "c"], fetched);
        Assert.Equal(3, consumer.Cursor);
    }

    [Fact]
    public void Advance_EmptyMarkers_YieldsNothing_AndLeavesCursor()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        consumer.Advance([Marker("a", 4, Other)], AllInView, NoHeldVersion);

        var fetched = consumer.Advance([], AllInView, NoHeldVersion);

        Assert.Empty(fetched);
        Assert.Equal(4, consumer.Cursor);
    }

    // ── Self-echo filtering ─────────────────────────────────────────────────

    [Fact]
    public void Advance_OwnInstanceMarker_IsSkipped_ButCursorStillAdvances()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("mine", 1, Me), Marker("theirs", 2, Other) };

        var fetched = consumer.Advance(markers, AllInView, NoHeldVersion);

        Assert.Equal(["theirs"], fetched); // never re-fetch our own confirmed write.
        Assert.Equal(2, consumer.Cursor);  // ...but the cursor passes our own marker too.
    }

    [Fact]
    public void Advance_TrailingOwnMarker_DoesNotStrandTheCursor()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("theirs", 1, Other), Marker("mine", 2, Me) };

        consumer.Advance(markers, AllInView, NoHeldVersion);

        // The cursor advances past a trailing own-marker, so it isn't re-scanned every tick.
        Assert.Equal(2, consumer.Cursor);
        Assert.Empty(consumer.Advance(markers, AllInView, NoHeldVersion));
    }

    // ── Out-of-view (edge case 2) ───────────────────────────────────────────

    [Fact]
    public void Advance_OutOfViewMarker_AdvancesCursorWithoutFetching()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("hidden", 1, Other), Marker("shown", 2, Other) };

        var fetched = consumer.Advance(markers, id => id == "shown", NoHeldVersion);

        Assert.Equal(["shown"], fetched);
        Assert.Equal(2, consumer.Cursor); // the out-of-view marker's seq is passed, not left pending.
    }

    [Fact]
    public void Advance_MarkerWithNoTaskId_IsSkipped()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("", 1, Other), Marker("real", 2, Other) };

        var fetched = consumer.Advance(markers, AllInView, NoHeldVersion);

        Assert.Equal(["real"], fetched);
        Assert.Equal(2, consumer.Cursor);
    }

    // ── Version suppression ─────────────────────────────────────────────────

    [Fact]
    public void Advance_HeldVersionAtOrBeyondMarker_SuppressesTheFetch()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("t", 1, Other, serverMs: 1000) };

        // Held copy is already newer-or-equal than the marker's server time → redundant, suppress.
        var fetchedEqual = consumer.Advance(markers, AllInView, _ => 1000);
        Assert.Empty(fetchedEqual);
        Assert.Equal(1, consumer.Cursor);
    }

    [Fact]
    public void Advance_HeldVersionOlderThanMarker_StillFetches()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("t", 1, Other, serverMs: 2000) };

        var fetched = consumer.Advance(markers, AllInView, _ => 1000);

        Assert.Equal(["t"], fetched); // our copy is stale → fetch.
        Assert.Equal(1, consumer.Cursor);
    }

    [Fact]
    public void Advance_MarkerWithoutServerTime_AlwaysFetches_EvenWithAHeldVersion()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("t", 1, Other, serverMs: null) };

        // No server time to compare against (e.g. a comment post) → can't suppress, always fetch.
        var fetched = consumer.Advance(markers, AllInView, _ => 9999);

        Assert.Equal(["t"], fetched);
    }

    [Fact]
    public void Advance_InViewButUnknownHeldVersion_Fetches()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[] { Marker("t", 1, Other, serverMs: 1000) };

        var fetched = consumer.Advance(markers, AllInView, NoHeldVersion);

        Assert.Equal(["t"], fetched); // in view, version unknown → don't gamble, fetch.
    }

    // ── Coalescing / re-pickup ──────────────────────────────────────────────

    [Fact]
    public void Advance_MultipleMarkersForOneTask_CoalesceToASingleFetch()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        // The store is keyed-by-task so this is defensive, but the consumer must dedupe regardless.
        var markers = new[] { Marker("t", 1, Other), Marker("t", 2, Other), Marker("u", 3, Other) };

        var fetched = consumer.Advance(markers, AllInView, NoHeldVersion);

        Assert.Equal(["t", "u"], fetched);
        Assert.Equal(3, consumer.Cursor);
    }

    [Fact]
    public void Advance_ReEditedTaskAboveCursor_IsRePickedUp()
    {
        var consumer = new ChangeMarkerConsumer(Me);

        var first = consumer.Advance([Marker("t", 1, Other)], AllInView, NoHeldVersion);
        Assert.Equal(["t"], first);
        Assert.Equal(1, consumer.Cursor);

        // #294 upserts the same task with a higher seq on a re-edit → above the cursor → fetched again.
        var second = consumer.Advance([Marker("t", 5, Other)], AllInView, NoHeldVersion);
        Assert.Equal(["t"], second);
        Assert.Equal(5, consumer.Cursor);
    }

    [Fact]
    public void Advance_FirstSeenOrderIsPreserved()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var markers = new[]
        {
            Marker("z", 10, Other),
            Marker("y", 11, Other),
            Marker("x", 12, Other),
        };

        var fetched = consumer.Advance(markers, AllInView, NoHeldVersion);

        Assert.Equal(["z", "y", "x"], fetched); // ascending seq == emission order.
    }
}
