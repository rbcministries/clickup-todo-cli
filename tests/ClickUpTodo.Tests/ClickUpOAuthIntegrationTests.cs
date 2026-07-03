using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Live OAuth token-exchange test. Skipped automatically unless a user-registered app's
/// credentials <b>and</b> a fresh authorization code are supplied via environment variables, so CI
/// stays green without secrets. This is also the way to confirm, against the live API, whether the
/// OAuth access token is accepted with the <c>Bearer</c> prefix (issue #1's open question).
/// <para>
/// ClickUp authorization codes are single-use and short-lived: obtain one by visiting the URL from
/// <see cref="ClickUpOAuth.BuildAuthorizeUrl"/> in a browser, authorizing, and copying the
/// <c>code</c> query parameter from the redirect, then set it as <c>CLICKUP_OAUTH_CODE</c>.
/// </para>
/// </summary>
public sealed class ClickUpOAuthIntegrationTests
{
    private static string? ClientId => Environment.GetEnvironmentVariable(OAuthAppCredentialStore.ClientIdEnvVar);
    private static string? ClientSecret => Environment.GetEnvironmentVariable(OAuthAppCredentialStore.ClientSecretEnvVar);
    private static string? Code => Environment.GetEnvironmentVariable("CLICKUP_OAUTH_CODE");

    [SkippableFact]
    public async Task ExchangeCodeForToken_ReturnsUsableAccessToken()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret) || string.IsNullOrWhiteSpace(Code),
            $"Set {OAuthAppCredentialStore.ClientIdEnvVar}, {OAuthAppCredentialStore.ClientSecretEnvVar} and "
            + "CLICKUP_OAUTH_CODE (a fresh authorization code) to run the live OAuth exchange test.");

        using var http = new HttpClient();
        var oauth = new ClickUpOAuth(http);

        var accessToken = await oauth.ExchangeCodeForTokenAsync(new OAuthAppCredentials(ClientId!, ClientSecret!), Code!);
        Assert.False(string.IsNullOrWhiteSpace(accessToken));

        // The exchanged token should drive an authenticated call (confirms the Bearer scheme works).
        using var client = new ClickUpClient(new ClickUpOAuthAuthProvider(accessToken));
        var me = await client.GetMeAsync();
        Assert.True(me.Id > 0);
    }
}
