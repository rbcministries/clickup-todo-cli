using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The single-task launch mode's nudge view predicates (#377): the tab holds exactly one task, so
/// "in view" collapses to "is the launch task" and the held version is that one task's
/// <c>date_updated</c>. These pin the exact <see cref="ChangeMarkerConsumer.Advance"/> callbacks the
/// <c>SingleTaskApp</c> poll uses, in CI without a Terminal.Gui driver.
/// </summary>
public sealed class SingleTaskNudgePolicyTests
{
    private const string Launch = "launch-task";
    private const string Other = "other-task";
    private const string Me = "instance-me";
    private const string Them = "instance-other";

    private static ChangeMarker Marker(string taskId, long seq, string instanceId, long? serverMs = null)
        => new(taskId, seq, serverMs, [], instanceId, RecordedUtcMs: seq);

    // ── The predicates in isolation ─────────────────────────────────────────

    [Fact]
    public void IsInView_TrueOnlyForLaunchTask()
    {
        var policy = new SingleTaskNudgePolicy(Launch, () => null);

        Assert.True(policy.IsInView(Launch));
        Assert.False(policy.IsInView(Other));
        Assert.False(policy.IsInView(""));
    }

    [Fact]
    public void HeldVersion_ReturnsSupplierValueForLaunchTask_NullForAnyOther()
    {
        var policy = new SingleTaskNudgePolicy(Launch, () => 100L);

        Assert.Equal(100L, policy.HeldVersion(Launch));
        Assert.Null(policy.HeldVersion(Other));
    }

    [Fact]
    public void HeldVersion_LaunchTask_ReflectsALiveVersionChange()
    {
        long? held = 100L;
        var policy = new SingleTaskNudgePolicy(Launch, () => held);

        Assert.Equal(100L, policy.HeldVersion(Launch));

        held = 250L; // a refresh since launch advanced the held date_updated.
        Assert.Equal(250L, policy.HeldVersion(Launch));
    }

    [Fact]
    public void HeldVersion_LaunchTask_UnknownVersionIsNull()
    {
        var policy = new SingleTaskNudgePolicy(Launch, () => null);

        Assert.Null(policy.HeldVersion(Launch));
    }

    // ── Driving the real consumer through the policy (the poll's exact wiring) ──

    [Fact]
    public void Consumer_FetchesOnlyTheLaunchTask_SkippingForeignAndOwnMarkers()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var policy = new SingleTaskNudgePolicy(Launch, () => null);
        var markers = new[]
        {
            Marker(Other, 1, Them),   // foreign task — out of view for a single-task tab.
            Marker(Launch, 2, Them),  // another tab edited our launch task — fetch.
            Marker(Launch, 3, Me),    // our own write — no self-echo.
        };

        var fetched = consumer.Advance(markers, policy.IsInView, policy.HeldVersion);

        Assert.Equal([Launch], fetched);
        Assert.Equal(3, consumer.Cursor); // cursor advances past every marker, incl. the skipped ones.
    }

    [Fact]
    public void Consumer_SuppressesAFetchWhenHeldVersionIsAtOrBeyondTheMarker()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        // We already hold date_updated = 500 for the launch task.
        var policy = new SingleTaskNudgePolicy(Launch, () => 500L);

        // A marker whose server time is older than what we hold is redundant — suppress it.
        var stale = consumer.Advance([Marker(Launch, 1, Them, serverMs: 400L)], policy.IsInView, policy.HeldVersion);
        Assert.Empty(stale);
        Assert.Equal(1, consumer.Cursor);

        // A marker newer than what we hold still fetches.
        var fresh = consumer.Advance([Marker(Launch, 2, Them, serverMs: 600L)], policy.IsInView, policy.HeldVersion);
        Assert.Equal([Launch], fresh);
    }

    [Fact]
    public void Consumer_CommentMarkerWithNoServerTimeAlwaysFetches()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var policy = new SingleTaskNudgePolicy(Launch, () => 500L);

        // No server time (a comment-style marker) can't be version-suppressed — always fetch.
        var fetched = consumer.Advance([Marker(Launch, 1, Them, serverMs: null)], policy.IsInView, policy.HeldVersion);

        Assert.Equal([Launch], fetched);
    }

    [Fact]
    public void Consumer_FreshTabDoesNotReplayHistory()
    {
        var consumer = new ChangeMarkerConsumer(Me);
        var policy = new SingleTaskNudgePolicy(Launch, () => null);
        var history = new[]
        {
            Marker(Launch, 5, Them),
            Marker(Other, 7, Them),
        };

        consumer.Initialize(history); // fresh tab just did a full load — seed past all history.
        Assert.Equal(7, consumer.Cursor);

        var fetched = consumer.Advance(history, policy.IsInView, policy.HeldVersion);
        Assert.Empty(fetched);
    }
}
