using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The consumer (#295) over the <b>real</b> <see cref="LiteDbChangeMarkerStore"/> — the store↔consumer
/// seam the composition root wires. Two marker stores over one shared <c>state.db</c> stand in for two
/// instances (tab A writes, tab B consumes): a fresh tab B initialises past all history and doesn't
/// replay it, then picks up tab A's later edits by task while skipping its own writes. Complements the
/// pure-logic <see cref="ChangeMarkerConsumerTests"/> (fakes) and the producer's own store tests.
/// </summary>
public sealed class ChangeMarkerConsumerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "state.db");

    private static readonly string[] StatusFields = ["status"];

    private static bool AllInView(string _) => true;
    private static long? NoHeldVersion(string _) => null;

    [Fact]
    public void FreshTab_InitialisesPastHistory_ThenPicksUpLaterForeignEdits()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var tabA = backend.CreateChangeMarkerStore("A");
        var tabB = backend.CreateChangeMarkerStore("B");
        var consumerB = new ChangeMarkerConsumer(tabB.InstanceId);

        // History already in the channel when tab B launches.
        tabA.Record("t1", serverDateUpdatedMs: null, StatusFields);

        // Fresh-tab init (edge case 1): cursor jumps to the current max — no replay of t1.
        consumerB.Initialize(tabB.ReadAll());
        Assert.Empty(consumerB.Advance(tabB.ReadAll(), AllInView, NoHeldVersion));

        // A later edit from tab A upserts a higher-seq marker → tab B picks up just that task.
        tabA.Record("t2", serverDateUpdatedMs: null, StatusFields);
        Assert.Equal(["t2"], consumerB.Advance(tabB.ReadAll(), AllInView, NoHeldVersion));
    }

    [Fact]
    public void ConsumerSkipsItsOwnWrites_OverTheRealStore()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var tabA = backend.CreateChangeMarkerStore("A");
        var tabB = backend.CreateChangeMarkerStore("B");
        var consumerB = new ChangeMarkerConsumer(tabB.InstanceId);
        consumerB.Initialize(tabB.ReadAll()); // empty store → cursor 0.

        tabB.Record("mine", serverDateUpdatedMs: null, StatusFields);   // B's own write — no self-echo.
        tabA.Record("theirs", serverDateUpdatedMs: null, StatusFields); // A's write — B should fetch.

        Assert.Equal(["theirs"], consumerB.Advance(tabB.ReadAll(), AllInView, NoHeldVersion));
    }

    [Fact]
    public void ReEditedTask_SupersedesAboveCursor_AndIsRePickedUp()
    {
        using var backend = new LiteDbStateStore(DbPath);
        var tabA = backend.CreateChangeMarkerStore("A");
        var tabB = backend.CreateChangeMarkerStore("B");
        var consumerB = new ChangeMarkerConsumer(tabB.InstanceId);
        consumerB.Initialize(tabB.ReadAll());

        tabA.Record("t", serverDateUpdatedMs: null, StatusFields);
        Assert.Equal(["t"], consumerB.Advance(tabB.ReadAll(), AllInView, NoHeldVersion));

        // The keyed-by-task upsert supersedes t's row with a higher seq (above B's cursor) → re-picked-up.
        tabA.Record("t", serverDateUpdatedMs: null, StatusFields);
        Assert.Equal(["t"], consumerB.Advance(tabB.ReadAll(), AllInView, NoHeldVersion));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
