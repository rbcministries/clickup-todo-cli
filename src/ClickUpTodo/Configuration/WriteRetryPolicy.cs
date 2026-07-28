namespace ClickUpTodo.Configuration;

/// <summary>
/// Bounded retry for the change-marker write path (#410). LiteDB's <see cref="LiteDB.ConnectionType.Shared"/>
/// mode opens and closes the database file per operation, so under load — two tabs writing at once, a
/// busy machine — a reopen can transiently fail. The marker write is best-effort (a nudge must never
/// break the edit it rides on), so without a retry the very first such blip silently drops the nudge.
/// This retries the write a handful of times before giving up, turning a transient failure into a
/// short pause rather than a lost marker, while still never propagating an exception.
/// <para>
/// The retry count and the inter-attempt delay are injectable so the policy is unit-testable
/// deterministically (tests pass a no-op delay); production uses a small escalating back-off, which
/// only ever runs on the rare failing write and adds no latency to the common success path.
/// </para>
/// </summary>
internal sealed class WriteRetryPolicy
{
    private readonly int _maxAttempts;
    private readonly Action<int> _delay;

    /// <param name="maxAttempts">Total attempts before giving up (clamped to at least 1).</param>
    /// <param name="delay">Called with the just-failed attempt number (1-based) to back off before the
    /// next try; never called after the final attempt. Defaults to a short escalating sleep.</param>
    public WriteRetryPolicy(int maxAttempts = 8, Action<int>? delay = null)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _delay = delay ?? DefaultDelay;
    }

    /// <summary>
    /// Runs <paramref name="write"/>, retrying on any exception up to the configured attempt limit.
    /// Returns <see langword="true"/> the first time it completes without throwing; returns
    /// <see langword="false"/> — passing the last exception to <paramref name="onGiveUp"/> — if every
    /// attempt threw. Never propagates: a write that can't persist is a dropped nudge, not a failure.
    /// </summary>
    public bool Run(Action write, Action<Exception>? onGiveUp = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return true;
            }
            catch (Exception ex)
            {
                if (attempt >= _maxAttempts)
                {
                    // Guarded so a throwing give-up hook can't resurrect the failure we just swallowed —
                    // Run's whole contract is that it never propagates.
                    try { onGiveUp?.Invoke(ex); }
                    catch { /* the give-up hook must not turn a dropped nudge into a thrown one */ }
                    return false;
                }
            }

            // Back off before the next attempt (never after the final one). Guarded too, so a delay
            // interruption (e.g. ThreadInterruptedException) is not mistaken for a write failure.
            try { _delay(attempt); }
            catch { /* a backoff interruption is not a write failure to propagate */ }
        }
    }

    // A transient Shared-mode reopen clears in well under a millisecond, so a few short, escalating
    // sleeps are enough to ride it out without adding meaningful latency to a background nudge.
    private static void DefaultDelay(int attempt) => Thread.Sleep(Math.Min(attempt, 5));
}
