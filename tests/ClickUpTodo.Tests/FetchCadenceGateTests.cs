using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The per-group minimum-age gate from the #246 ADR: a never-completed group is due immediately,
/// <c>MarkRan</c> holds it back for the group's minimum age (inclusive at the boundary, so a cycle
/// landing exactly on the age runs), and groups don't interfere.
/// </summary>
public sealed class FetchCadenceGateTests
{
    /// <summary>A TimeProvider whose clock only advances when the test moves it.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(30);

    [Fact]
    public void NeverCompletedGroup_IsDueImmediately()
    {
        var gate = new FetchCadenceGate(NewClock());

        Assert.True(gate.IsDue("walk", MinAge));
        // Asking is not running: the group stays due until MarkRan (stamp-at-completion means a
        // multi-cycle run keeps resuming).
        Assert.True(gate.IsDue("walk", MinAge));
    }

    [Fact]
    public void MarkRan_HoldsTheGroupUntilItsMinimumAge()
    {
        var clock = NewClock();
        var gate = new FetchCadenceGate(clock);

        gate.MarkRan("walk");
        Assert.False(gate.IsDue("walk", MinAge));

        clock.Advance(MinAge - TimeSpan.FromSeconds(1));
        Assert.False(gate.IsDue("walk", MinAge));

        clock.Advance(TimeSpan.FromSeconds(1)); // exactly the minimum age — inclusive boundary
        Assert.True(gate.IsDue("walk", MinAge));
    }

    [Fact]
    public void MarkRan_RestampsTheClock()
    {
        var clock = NewClock();
        var gate = new FetchCadenceGate(clock);

        gate.MarkRan("walk");
        clock.Advance(MinAge);
        Assert.True(gate.IsDue("walk", MinAge));

        gate.MarkRan("walk"); // the run that just happened resets the wait in full
        Assert.False(gate.IsDue("walk", MinAge));
        clock.Advance(MinAge);
        Assert.True(gate.IsDue("walk", MinAge));
    }

    [Fact]
    public void Groups_AreIndependent()
    {
        var clock = NewClock();
        var gate = new FetchCadenceGate(clock);

        gate.MarkRan("walk");

        Assert.False(gate.IsDue("walk", MinAge));
        Assert.True(gate.IsDue("statuses", MinAge)); // untouched group unaffected by the other's stamp
    }

    [Fact]
    public void SameGroup_CanBeAskedWithDifferentAges()
    {
        var clock = NewClock();
        var gate = new FetchCadenceGate(clock);

        gate.MarkRan("walk");
        clock.Advance(TimeSpan.FromMinutes(10));

        // The age is the caller's per-ask policy, not stored state.
        Assert.True(gate.IsDue("walk", TimeSpan.FromMinutes(5)));
        Assert.False(gate.IsDue("walk", MinAge));
    }
}
