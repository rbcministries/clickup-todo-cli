using System.Net;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for the rate-limit governor (#193): pure header→delay math, plus handler-level behaviour
/// through a scripted inner <see cref="HttpMessageHandler"/> (no sockets). Waits used in tests are
/// zero or a few hundred ms so the suite stays fast; assertions on elapsed time are lower bounds
/// (an overloaded CI machine can only make waits longer, never shorter), plus one deliberately
/// generous upper-bound sanity check in <c>HealthyBudget_DoesNotPause</c>.
/// </summary>
public sealed class ClickUpRateLimitHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    // ── RetryDelay: header → wait math (pure) ────────────────────────────────

    private static HttpResponseMessage Resp(HttpStatusCode code = HttpStatusCode.TooManyRequests,
        string? retryAfter = null, long? reset = null, long? remaining = null, long? limit = null)
    {
        var r = new HttpResponseMessage(code);
        if (retryAfter is not null)
            r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        if (reset is not null)
            r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.Value.ToString());
        if (remaining is not null)
            r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining.Value.ToString());
        if (limit is not null)
            r.Headers.TryAddWithoutValidation("X-RateLimit-Limit", limit.Value.ToString());
        return r;
    }

    [Fact]
    public void RetryDelay_UsesRetryAfterSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(30), ClickUpRateLimitHandler.RetryDelay(Resp(retryAfter: "30"), 0, Now));

    [Fact]
    public void RetryDelay_UsesRetryAfterHttpDate()
    {
        var resp = Resp(retryAfter: (Now + TimeSpan.FromSeconds(42)).ToString("R"));
        var delay = ClickUpRateLimitHandler.RetryDelay(resp, 0, Now);
        Assert.InRange(delay, TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(43)); // "R" truncates sub-second
    }

    [Fact]
    public void RetryDelay_FallsBackToRateLimitReset_EpochSeconds()
    {
        var resp = Resp(reset: Now.ToUnixTimeSeconds() + 25);
        Assert.Equal(TimeSpan.FromSeconds(25), ClickUpRateLimitHandler.RetryDelay(resp, 0, Now));
    }

    [Fact]
    public void RetryDelay_ReadsSmallResetAsDeltaSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(9), ClickUpRateLimitHandler.RetryDelay(Resp(reset: 9), 0, Now));

    [Fact]
    public void RetryDelay_NoHeaders_ExponentialBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ClickUpRateLimitHandler.RetryDelay(Resp(), 0, Now));
        Assert.Equal(TimeSpan.FromSeconds(2), ClickUpRateLimitHandler.RetryDelay(Resp(), 1, Now));
        Assert.Equal(TimeSpan.FromSeconds(4), ClickUpRateLimitHandler.RetryDelay(Resp(), 2, Now));
    }

    [Fact]
    public void RetryDelay_PastRetryAfterDate_ClampsToZero()
    {
        var resp = Resp(retryAfter: (Now - TimeSpan.FromSeconds(5)).ToString("R"));
        Assert.Equal(TimeSpan.Zero, ClickUpRateLimitHandler.RetryDelay(resp, 0, Now));
    }

    // ── NearBudgetExhaustion / ResetAt (pure) ────────────────────────────────

    [Theory]
    [InlineData(100, 100, false)] // plenty left
    [InlineData(11, 100, false)]  // just above the 10% floor
    [InlineData(10, 100, true)]   // at the floor
    [InlineData(0, 100, true)]
    public void NearBudgetExhaustion_ThresholdsOnTenPercent(long remaining, long limit, bool expected)
        => Assert.Equal(expected, ClickUpRateLimitHandler.NearBudgetExhaustion(
            Resp(HttpStatusCode.OK, remaining: remaining, limit: limit)));

    [Fact]
    public void NearBudgetExhaustion_NoHeaders_NoSignal()
        => Assert.False(ClickUpRateLimitHandler.NearBudgetExhaustion(Resp(HttpStatusCode.OK)));

    [Fact]
    public void NearBudgetExhaustion_NoLimitHeader_TriggersAtOne()
    {
        Assert.True(ClickUpRateLimitHandler.NearBudgetExhaustion(Resp(HttpStatusCode.OK, remaining: 1)));
        Assert.False(ClickUpRateLimitHandler.NearBudgetExhaustion(Resp(HttpStatusCode.OK, remaining: 2)));
    }

    [Fact]
    public void ResetAt_MalformedHeader_Null()
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK);
        r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "soon");
        Assert.Null(ClickUpRateLimitHandler.ResetAt(r, Now));
    }

    // ── Handler behaviour (scripted inner handler, no sockets) ──────────────

    /// <summary>Inner handler that dequeues scripted responses (repeating the last one) and can gate
    /// or observe each send.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _index = -1;
        public int Sends => _index + 1;
        public Func<Task>? OnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var i = Interlocked.Increment(ref _index);
            await (OnSend?.Invoke() ?? Task.CompletedTask);
            return script[Math.Min(i, script.Length - 1)]();
        }
    }

    private static HttpMessageInvoker Invoker(ScriptedHandler inner, int maxInFlight = ClickUpRateLimitHandler.MaxInFlight)
        => new(new ClickUpRateLimitHandler(maxInFlight: maxInFlight) { InnerHandler = inner });

    private static HttpRequestMessage Get() => new(HttpMethod.Get, "https://api.clickup.com/api/v2/team");

    [Fact]
    public async Task Retries429Read_ThenReturnsSuccess()
    {
        var inner = new ScriptedHandler(
            () => Resp(retryAfter: "0"),
            () => Resp(retryAfter: "0"),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner);

        var response = await invoker.SendAsync(Get(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Sends);
    }

    [Fact]
    public async Task PersistentlyThrottledRead_GivesUpAfterMaxRetries()
    {
        var inner = new ScriptedHandler(() => Resp(retryAfter: "0"));
        using var invoker = Invoker(inner);

        var response = await invoker.SendAsync(Get(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1 + ClickUpRateLimitHandler.MaxRetries, inner.Sends);
    }

    [Fact]
    public async Task Throttled429WithBody_NotRetriedHere()
    {
        // A write's content stream can't safely be resent from this handler; Kiota's RetryHandler
        // above owns that retry. The 429 must surface after exactly one send.
        var inner = new ScriptedHandler(() => Resp(retryAfter: "0"));
        using var invoker = Invoker(inner);
        var put = new HttpRequestMessage(HttpMethod.Put, "https://api.clickup.com/api/v2/task/t1")
        {
            Content = new StringContent("{}"),
        };

        var response = await invoker.SendAsync(put, CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, inner.Sends);
    }

    [Fact]
    public async Task WaitBeyondMaxWait_SurfacesThe429WithoutRetrying()
    {
        var inner = new ScriptedHandler(() => Resp(retryAfter: "300"));
        using var invoker = Invoker(inner);

        var response = await invoker.SendAsync(Get(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, inner.Sends);
    }

    [Fact]
    public async Task LowRemainingBudget_PausesTheNextRequestUntilReset()
    {
        var inner = new ScriptedHandler(
            // Delta-form reset (1s) keeps the test independent of wall-clock epoch math.
            () => Resp(HttpStatusCode.OK, remaining: 1, limit: 100, reset: 1),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner);

        (await invoker.SendAsync(Get(), CancellationToken.None)).Dispose();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        (await invoker.SendAsync(Get(), CancellationToken.None)).Dispose();
        stopwatch.Stop();

        // The reset delta is 1s; the second request must have waited a good chunk of it (lower
        // bound only — CI slowness can lengthen the wait, never shorten it).
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"second request was not paused (elapsed {stopwatch.ElapsedMilliseconds} ms)");
        Assert.Equal(2, inner.Sends);
    }

    [Fact]
    public async Task HealthyBudget_DoesNotPause()
    {
        var inner = new ScriptedHandler(() => Resp(HttpStatusCode.OK, remaining: 90, limit: 100, reset: 60));
        using var invoker = Invoker(inner);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
            (await invoker.SendAsync(Get(), CancellationToken.None)).Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "healthy responses must not throttle");
        Assert.Equal(3, inner.Sends);
    }

    [Fact]
    public async Task PausedRequest_HonoursCancellation()
    {
        var inner = new ScriptedHandler(
            () => Resp(HttpStatusCode.OK, remaining: 0, limit: 100, reset: 60), // pause ~60s
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner);
        (await invoker.SendAsync(Get(), CancellationToken.None)).Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.SendAsync(Get(), cts.Token));
    }

    [Fact]
    public async Task Gate_BoundsConcurrentSends()
    {
        var inFlight = 0;
        var peak = 0;
        var inner = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        inner.OnSend = async () =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
        };
        using var invoker = Invoker(inner, maxInFlight: 3);

        var sends = Enumerable.Range(0, 12).Select(async _ =>
            (await invoker.SendAsync(Get(), CancellationToken.None)).Dispose());
        await Task.WhenAll(sends);

        Assert.Equal(12, inner.Sends);
        Assert.True(peak <= 3, $"peak in-flight {peak} exceeded the gate width");
    }

    // ── ShouldGiveUp / factory retry predicate (pure) ────────────────────────

    [Fact]
    public void ShouldGiveUp_CumulativeWaitBudget_StopsRetrying()
    {
        var half = TimeSpan.FromTicks(ClickUpRateLimitHandler.MaxWait.Ticks / 2);

        Assert.False(ClickUpRateLimitHandler.ShouldGiveUp(0, hasBody: false, half, TimeSpan.Zero));
        Assert.False(ClickUpRateLimitHandler.ShouldGiveUp(1, hasBody: false, half, half));
        // A third half-budget wait would push the cumulative total past MaxWait.
        Assert.True(ClickUpRateLimitHandler.ShouldGiveUp(2, hasBody: false, half, half + half));
    }

    [Theory]
    [InlineData(0, true, true)]    // a body is never retried here
    [InlineData(3, false, true)]   // attempts exhausted (MaxRetries = 3)
    public void ShouldGiveUp_BodyAndAttemptRules(int attempt, bool hasBody, bool expected)
        => Assert.Equal(expected,
            ClickUpRateLimitHandler.ShouldGiveUp(attempt, hasBody, TimeSpan.FromSeconds(1), TimeSpan.Zero));

    [Fact]
    public void ShouldGiveUp_SingleWaitBeyondMaxWait()
        => Assert.True(ClickUpRateLimitHandler.ShouldGiveUp(
            0, hasBody: false, ClickUpRateLimitHandler.MaxWait + TimeSpan.FromSeconds(1), TimeSpan.Zero));

    [Fact]
    public void KiotaShouldRetry_LeavesRead429sToTheGovernor()
    {
        // Read (no body) 429: Kiota's RetryHandler must NOT retry — the governor owns it, and the
        // final 429 must reach the adapter to surface as ClickUpApiException(429).
        var read429 = Resp();
        read429.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.clickup.com/x");
        Assert.False(ClickUpClientFactory.KiotaShouldRetry(1, 1, read429));

        // Write (bodied) 429: RetryHandler keeps it (it can clone content; the governor defers it).
        var write429 = Resp();
        write429.RequestMessage = new HttpRequestMessage(HttpMethod.Put, "https://api.clickup.com/x")
        {
            Content = new StringContent("{}"),
        };
        Assert.True(ClickUpClientFactory.KiotaShouldRetry(1, 1, write429));

        // Kiota's other retriable statuses keep stock behaviour.
        foreach (var code in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout })
        {
            var retriable = Resp(code);
            retriable.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.clickup.com/x");
            Assert.True(ClickUpClientFactory.KiotaShouldRetry(1, 1, retriable), $"{code} should retry");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]              // the startup GetAuthorizedUser read — MUST NOT retry
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void KiotaShouldRetry_NeverRetriesNonRetriableStatuses(HttpStatusCode code)
    {
        // In Kiota 2.0 this predicate is the RetryHandler's sole gate, so a `true` here retries the
        // response. A `!= 429` form wrongly retried every 200 OK to exhaustion (the launch crash).
        var response = Resp(code);
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.clickup.com/x");
        Assert.False(ClickUpClientFactory.KiotaShouldRetry(1, 1, response));
    }

    [Fact]
    public async Task GovernedRetryHandler_DoesNotRetryASuccessfulRead()
    {
        // End-to-end guard: the predicate wired into a real Kiota RetryHandler must send a 200 OK
        // exactly once and return it — not loop to "Too many retries". Mirrors the production wiring
        // (ClickUpClientFactory.CreateHttpClient) at the RetryHandler layer.
        var inner = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"user\":{\"id\":1}}"),
        });
        using var retry = new Microsoft.Kiota.Http.HttpClientLibrary.Middleware.RetryHandler(
            new Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options.RetryHandlerOption
            {
                ShouldRetry = ClickUpClientFactory.KiotaShouldRetry,
            })
        { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(retry);

        var response = await invoker.SendAsync(Get(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Sends);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target))
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
