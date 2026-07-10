using System.Net;
using ClickUpTodo.Configuration;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Builds a <see cref="ClickUpClient"/> from a stored token, selecting the Kiota auth provider that
/// matches the config's <see cref="AuthMode"/> (#52): OAuth tokens go through
/// <see cref="ClickUpOAuthAuthProvider"/> (<c>Authorization: Bearer</c>); everything else (the
/// default) through <see cref="ClickUpTokenAuthProvider"/> (raw header). This is the single place
/// startup and the setup wizard decide how to authenticate, so the personal-token and OAuth paths
/// never diverge elsewhere.
/// </summary>
public static class ClickUpClientFactory
{
    /// <summary>Selects the auth provider for a stored token given the active <paramref name="mode"/>.</summary>
    public static IAuthenticationProvider AuthProviderFor(AuthMode mode, string token) => mode switch
    {
        AuthMode.OAuth => new ClickUpOAuthAuthProvider(token),
        _ => new ClickUpTokenAuthProvider(token),
    };

    /// <summary>Constructs a client for the token using the provider implied by <paramref name="config"/>.
    /// Unless the caller supplies its own <paramref name="httpClient"/>, the client is built by
    /// <see cref="CreateHttpClient"/> so all app traffic flows through the rate-limit governor (#193).</summary>
    public static ClickUpClient Create(AppConfig config, string token, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        // A factory-created HttpClient is owned (and disposed) by the ClickUpClient; a caller-supplied
        // one stays the caller's to manage, as before.
        return httpClient is null
            ? new ClickUpClient(AuthProviderFor(config.AuthMode, token), CreateHttpClient(), ownsHttpClient: true)
            : new ClickUpClient(AuthProviderFor(config.AuthMode, token), httpClient);
    }

    /// <summary>
    /// The app's standard <see cref="HttpClient"/> for ClickUp traffic: Kiota's default middleware
    /// (retry/redirect/decoding/…, the same stack the adapter would build on its own) with
    /// <see cref="ClickUpRateLimitHandler"/> appended innermost, so the shared in-flight gate and
    /// 429/throttle handling see every physical request — including Kiota-middleware retries.
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handlers = KiotaClientFactory.CreateDefaultHandlers();

        // Kiota's stock RetryHandler also retries 429s. Left alone it would compose with the
        // governor below it on reads — up to 4×4 physical sends under sustained throttling, ending
        // in an opaque AggregateException ("Too many retries") that Guard doesn't translate, instead
        // of a surfaced 429 → ClickUpApiException. Reads' 429s are therefore owned exclusively by
        // the governor; the stock handler keeps 429 only for bodied writes (whose content it can
        // clone — the governor deliberately defers those) plus its stock 503/504 behaviour.
        for (var i = 0; i < handlers.Count; i++)
        {
            if (handlers[i] is RetryHandler)
            {
                handlers[i] = new RetryHandler(new RetryHandlerOption { ShouldRetry = KiotaShouldRetry });
                break;
            }
        }

        handlers.Add(new ClickUpRateLimitHandler());
        var httpClient = KiotaClientFactory.Create(handlers);
        // Must exceed the governor's worst-case added latency (a ≤70s shared pause on entry plus a
        // ≤70s cumulative retry budget — see ClickUpRateLimitHandler.ShouldGiveUp) with headroom for
        // the sends themselves; the stock 100s default guillotined legitimate waits mid-sleep and
        // surfaced them as cancellations.
        httpClient.Timeout = TimeSpan.FromSeconds(200);
        return httpClient;
    }

    /// <summary>The de-conflicted retry predicate for Kiota's stock <c>RetryHandler</c>: everything it
    /// would normally retry (503/504, and 429s on bodied writes) except read 429s, which belong to
    /// <see cref="ClickUpRateLimitHandler"/>. internal for unit tests.</summary>
    internal static bool KiotaShouldRetry(int delay, int executionCount, HttpResponseMessage response)
        => response.StatusCode != HttpStatusCode.TooManyRequests || response.RequestMessage?.Content is not null;
}
