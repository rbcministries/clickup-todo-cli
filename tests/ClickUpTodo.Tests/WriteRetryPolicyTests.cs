using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers <see cref="WriteRetryPolicy"/> (#410) — the bounded retry that keeps a transient LiteDB
/// Shared-mode reopen failure from silently dropping a change-marker nudge. The delay hook is made a
/// no-op so the tests are deterministic and fast.
/// </summary>
public sealed class WriteRetryPolicyTests
{
    private static WriteRetryPolicy Policy(int maxAttempts, Action<int>? delay = null)
        => new(maxAttempts, delay ?? (_ => { }));

    [Fact]
    public void Run_SucceedsFirstTry_RunsOnce_ReturnsTrue_NoGiveUp()
    {
        var calls = 0;
        Exception? gaveUp = null;

        var ok = Policy(8).Run(() => calls++, ex => gaveUp = ex);

        Assert.True(ok);
        Assert.Equal(1, calls);
        Assert.Null(gaveUp);
    }

    [Fact]
    public void Run_TransientThenSuccess_RetriesUntilItSucceeds()
    {
        var calls = 0;
        Exception? gaveUp = null;

        // Throws on the first three attempts, then the fourth succeeds.
        var ok = Policy(8).Run(
            () => { calls++; if (calls <= 3) throw new IOException("file is locked"); },
            ex => gaveUp = ex);

        Assert.True(ok);
        Assert.Equal(4, calls);
        Assert.Null(gaveUp); // it never gave up — the transient cleared within the attempt budget.
    }

    [Fact]
    public void Run_AlwaysThrows_GivesUpAfterExactlyMaxAttempts_Swallowed()
    {
        var calls = 0;
        Exception? gaveUp = null;

        var ok = Policy(5).Run(
            () => { calls++; throw new IOException("still locked"); },
            ex => gaveUp = ex);

        Assert.False(ok);
        Assert.Equal(5, calls);                 // exactly maxAttempts tries, no more.
        Assert.IsType<IOException>(gaveUp);      // the last exception is handed to onGiveUp...
        // ...and Run never propagates: reaching this line at all proves nothing was thrown.
    }

    [Fact]
    public void Run_DelaysBetweenAttempts_ButNotAfterTheFinalFailure()
    {
        var delayedFor = new List<int>();

        Policy(3, delayedFor.Add).Run(() => throw new InvalidOperationException(), _ => { });

        // Three attempts all threw: a back-off ran before attempts 2 and 3, but not after the final
        // give-up (no point sleeping when we're about to return).
        Assert.Equal([1, 2], delayedFor);
    }

    [Fact]
    public void Run_MaxAttemptsBelowOne_IsClampedToASingleAttempt()
    {
        var calls = 0;

        var ok = Policy(0).Run(() => { calls++; throw new IOException("nope"); });

        Assert.False(ok);
        Assert.Equal(1, calls); // clamped to at least one attempt rather than looping zero times.
    }

    [Fact]
    public void Run_OnGiveUpIsOptional_StillSwallows()
    {
        // No onGiveUp callback supplied: an always-throwing write must still be swallowed, not thrown.
        var ok = Policy(2).Run(() => throw new IOException("boom"));

        Assert.False(ok);
    }

    [Fact]
    public void Run_WhenGiveUpHookItselfThrows_StillDoesNotPropagate()
    {
        // A throwing give-up hook must not resurrect the failure Run just swallowed — reaching the
        // assertion at all proves nothing propagated out of Run.
        var ok = Policy(1).Run(
            () => throw new IOException("write"),
            _ => throw new InvalidOperationException("hook blew up"));

        Assert.False(ok);
    }

    [Fact]
    public void Run_WhenDelayHookThrows_KeepsRetrying_AndDoesNotPropagate()
    {
        var calls = 0;

        // The back-off hook throwing must not abort the retry loop or propagate: the write still gets
        // its later attempt and eventually succeeds.
        var ok = new WriteRetryPolicy(maxAttempts: 3, delay: _ => throw new InvalidOperationException("delay"))
            .Run(() => { calls++; if (calls < 2) throw new IOException("transient"); });

        Assert.True(ok);
        Assert.Equal(2, calls);
    }
}
