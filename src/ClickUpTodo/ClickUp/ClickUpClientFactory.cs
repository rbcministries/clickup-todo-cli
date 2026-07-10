using ClickUpTodo.Configuration;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

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
        return new ClickUpClient(AuthProviderFor(config.AuthMode, token), httpClient ?? CreateHttpClient());
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
        handlers.Add(new ClickUpRateLimitHandler());
        return KiotaClientFactory.Create(handlers);
    }
}
