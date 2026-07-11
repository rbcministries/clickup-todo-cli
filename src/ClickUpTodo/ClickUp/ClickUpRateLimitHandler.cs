using System.Globalization;
using System.Net;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Rate-limit governor for all ClickUp traffic (#193), installed as the innermost handler on the
/// shared <see cref="HttpClient"/> (below Kiota's default middleware) so it sees every physical
/// request. Three cooperating behaviours:
/// <list type="number">
///   <item><b>One in-flight budget:</b> a gate caps concurrent requests at
///   <see cref="MaxInFlight"/> per handler instance — the app builds exactly one governed
///   <see cref="HttpClient"/> (see <see cref="ClickUpClientFactory.CreateHttpClient"/>), so the
///   budget is process-wide in practice — and overlapping fan-outs (refresh resolvers, the feed,
///   #192/#144) draw from that single budget instead of composing their per-stage caps.</item>
///   <item><b>Proactive throttle:</b> every response's <c>X-RateLimit-Remaining</c>/<c>-Limit</c>/
///   <c>-Reset</c> headers are observed; when the remaining budget drops below ~10% of the limit,
///   new requests pause until the reset instead of spending the budget to zero — refresh gets
///   slower under pressure rather than failing.</item>
///   <item><b>429 retry:</b> a throttled <em>read</em> (no request body — all the fetch volume) is
///   retried up to <see cref="MaxRetries"/> times after the server-indicated wait
///   (<c>Retry-After</c>, else <c>X-RateLimit-Reset</c>, else exponential backoff + jitter). A 429
///   with a body (a write) or a wait beyond <see cref="MaxWait"/> (single or cumulative) is
///   returned as-is: writes are re-driven by Kiota's <c>RetryHandler</c> above (it can clone request
///   content; resending a consumed body from here is not safe) with our raised pause still spacing
///   those retries out, while for reads the factory configures that handler to leave 429s alone —
///   so the response reaches the adapter and surfaces as a <c>ClickUpApiException</c>(429) instead
///   of an opaque retry-exhaustion error.</item>
/// </list>
/// All waits observe the request's <see cref="CancellationToken"/>, so shutdown or a superseded
/// refresh is never held hostage by a backoff sleep. Absent/malformed headers degrade gracefully:
/// no throttle signal means no pause, and a 429 without headers falls back to plain backoff.
/// This matters most where several users share one OAuth app registration — the client backs off
/// on the server's first signal instead of burning the shared goodwill (and its own token budget).
/// </summary>
public sealed class ClickUpRateLimitHandler : DelegatingHandler
{
    /// <summary>Process-wide cap on concurrent ClickUp requests (the single shared budget).</summary>
    internal const int MaxInFlight = 6;

    /// <summary>Additional attempts after the first response for a retryable 429.</summary>
    internal const int MaxRetries = 3;

    /// <summary>
    /// Longest wait honoured for a retry or pause — just over ClickUp's one-minute window. A 429
    /// demanding more than this is surfaced to the caller (whose error path can report it) rather
    /// than silently wedging a refresh, and a bogus reset far in the future can't stall the app.
    /// </summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(70);

    /// <summary>Cushion added to header-derived waits so a retry lands after the window rolls, not on
    /// its exact edge; also the jitter range that de-synchronizes clients sharing an OAuth app.</summary>
    internal static readonly TimeSpan Cushion = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _gate;
    private readonly TimeProvider _time;

    // Do-not-send-before timestamp (UTC ticks) shared by every request through this handler; 0 = no
    // pause. Only ever raised (Interlocked max) — it expires by the clock passing it.
    private long _pauseUntilTicks;

    /// <summary>Both knobs exist for tests; production uses the defaults.</summary>
    public ClickUpRateLimitHandler(TimeProvider? timeProvider = null, int maxInFlight = MaxInFlight)
    {
        _time = timeProvider ?? TimeProvider.System;
        _gate = new SemaphoreSlim(Math.Max(1, maxInFlight));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var alreadyWaited = TimeSpan.Zero; // intended retry waits so far — see ShouldGiveUp
        for (var attempt = 0; ; attempt++)
        {
            await WaitOutPauseAsync(ct).ConfigureAwait(false);

            HttpResponseMessage response;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                response = await base.SendAsync(request, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            var now = _time.GetUtcNow();

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryDelay(response, attempt, now)
                           + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Cushion.TotalMilliseconds));
                // Everyone backs off, even when this request won't retry (capped so a bogus header
                // can't wedge the app).
                RaisePause(now + Min(wait, MaxWait));

                if (ShouldGiveUp(attempt, hasBody: request.Content is not null, wait, alreadyWaited))
                    return response;

                alreadyWaited += wait;
                response.Dispose();
                continue; // the raised pause is the retry delay — WaitOutPauseAsync serves it
            }

            // Success path: if the window's remaining budget is nearly spent, pause new requests
            // until the reset so we glide to the window edge instead of slamming into it.
            if (NearBudgetExhaustion(response) && ResetAt(response, now) is { } resetAt && resetAt > now)
                RaisePause(Min(resetAt + Cushion, now + MaxWait));

            return response;
        }
    }

    /// <summary>
    /// Whether a 429 should be returned to the caller instead of retried here: retries exhausted, a
    /// request body we cannot safely resend, a single wait beyond <see cref="MaxWait"/>, or a
    /// <b>cumulative</b> intended wait beyond it. The cumulative cap bounds this handler's added
    /// latency per request to ~2×<see cref="MaxWait"/> worst case (one shared pause on entry + the
    /// retry budget), which the factory's <c>HttpClient.Timeout</c> is sized to accommodate — without
    /// it, back-to-back legit 60s waits could outlive any sane timeout mid-sleep and surface as an
    /// opaque cancellation instead of a 429. Pure; internal for unit tests.
    /// </summary>
    internal static bool ShouldGiveUp(int attempt, bool hasBody, TimeSpan wait, TimeSpan alreadyWaited)
        => attempt >= MaxRetries || hasBody || wait > MaxWait || alreadyWaited + wait > MaxWait;

    /// <summary>Delays until the shared pause (if any) has passed; re-checks in case it was raised
    /// again meanwhile. No-op on the common (unthrottled) path.</summary>
    private async Task WaitOutPauseAsync(CancellationToken ct)
    {
        while (true)
        {
            var until = Interlocked.Read(ref _pauseUntilTicks);
            var remaining = until - _time.GetUtcNow().UtcTicks;
            if (remaining <= 0)
                return;
            // The TimeProvider overload keeps the sleep on the same clock as the arithmetic above,
            // so a fake-clock test could drive the pause without real wall-time.
            await Task.Delay(TimeSpan.FromTicks(remaining), _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Raises the shared do-not-send-before mark to <paramref name="until"/> (never lowers it).</summary>
    private void RaisePause(DateTimeOffset until)
    {
        var target = until.UtcTicks;
        long current;
        while (target > (current = Interlocked.Read(ref _pauseUntilTicks))
               && Interlocked.CompareExchange(ref _pauseUntilTicks, target, current) != current)
        {
        }
    }

    /// <summary>
    /// The server-indicated wait before retrying a 429: <c>Retry-After</c> (delta or date), else
    /// <c>X-RateLimit-Reset</c>, else exponential backoff (1s · 2^attempt). Never negative. Pure —
    /// jitter is added by the caller. internal for unit tests.
    /// </summary>
    internal static TimeSpan RetryDelay(HttpResponseMessage response, int attempt, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
                return Max(delta, TimeSpan.Zero);
            if (retryAfter.Date is { } date)
                return Max(date - now, TimeSpan.Zero);
        }

        if (ResetAt(response, now) is { } resetAt)
            return Max(resetAt - now, TimeSpan.Zero);

        return TimeSpan.FromSeconds(1 << Math.Min(attempt, 5));
    }

    /// <summary>True when <c>X-RateLimit-Remaining</c> is at or below ~10% of <c>X-RateLimit-Limit</c>
    /// (or ≤ 1 when the limit header is absent). False when the headers are missing/malformed —
    /// no signal, no throttle. internal for unit tests.</summary>
    internal static bool NearBudgetExhaustion(HttpResponseMessage response)
    {
        if (HeaderInt(response, "X-RateLimit-Remaining") is not { } remaining)
            return false;
        var threshold = HeaderInt(response, "X-RateLimit-Limit") is { } limit and > 0
            ? Math.Max(1, limit / 10)
            : 1;
        return remaining <= threshold;
    }

    /// <summary>
    /// The window-reset instant from <c>X-RateLimit-Reset</c>, or null when absent/unparseable.
    /// ClickUp documents epoch seconds; a small value is defensively read as seconds-from-now in
    /// case the API (or a proxy) sends a delta instead. internal for unit tests.
    /// </summary>
    internal static DateTimeOffset? ResetAt(HttpResponseMessage response, DateTimeOffset now)
    {
        if (HeaderInt(response, "X-RateLimit-Reset") is not { } reset || reset < 0)
            return null;
        // Anything below ~4 months' worth of seconds can't be a plausible epoch timestamp → delta.
        return reset < 10_000_000 ? now + TimeSpan.FromSeconds(reset) : DateTimeOffset.FromUnixTimeSeconds(reset);
    }

    private static long? HeaderInt(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
           && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _gate.Dispose();
        base.Dispose(disposing);
    }
}
