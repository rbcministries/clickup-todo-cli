using System.Text.Json;
using System.Text.Json.Serialization;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.ClickUp;

/// <summary>Raised when the ClickUp OAuth authorization-code exchange fails.</summary>
public sealed class ClickUpOAuthException(string message) : Exception(message);

/// <summary>
/// The ClickUp OAuth2 authorization-code flow (user-supplied app, issue #1):
/// <list type="bullet">
///   <item><see cref="BuildAuthorizeUrl"/> — the URL to send the user's browser to, where they
///   authorize the app and ClickUp redirects back with a <c>code</c>.</item>
///   <item><see cref="ExchangeCodeForTokenAsync"/> — swaps that <c>code</c> for an access token via
///   <c>POST /api/v2/oauth/token</c>.</item>
/// </list>
/// The <see cref="HttpClient"/> is injected so the exchange is fully unit-testable offline.
/// </summary>
public sealed class ClickUpOAuth(HttpClient httpClient)
{
    /// <summary>Where the user authorizes the app (ClickUp then redirects back with a <c>code</c>).</summary>
    public const string AuthorizeBaseUrl = "https://app.clickup.com/api";

    /// <summary>The token-exchange endpoint (<c>code</c> → access token).</summary>
    public const string TokenEndpoint = "https://api.clickup.com/api/v2/oauth/token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <summary>
    /// Builds the authorization URL to open in the user's browser:
    /// <c>https://app.clickup.com/api?client_id=…&amp;redirect_uri=…&amp;response_type=code</c> (with an
    /// optional <c>state</c>, recommended for CSRF protection). Query values are URL-escaped.
    /// <c>response_type=code</c> is the standard OAuth2 authorization-code grant; ClickUp defaults to
    /// it, but it is sent explicitly for forward-compatibility.
    /// </summary>
    public static Uri BuildAuthorizeUrl(string clientId, string redirectUri, string? state = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var query = $"client_id={Uri.EscapeDataString(clientId)}"
                  + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                  + "&response_type=code";
        if (!string.IsNullOrWhiteSpace(state))
            query += $"&state={Uri.EscapeDataString(state)}";

        return new Uri($"{AuthorizeBaseUrl}?{query}");
    }

    /// <summary>
    /// Exchanges an authorization <paramref name="code"/> for a ClickUp access token. Throws
    /// <see cref="ClickUpOAuthException"/> on a non-success response or a response missing
    /// <c>access_token</c>.
    /// </summary>
    public async Task<string> ExchangeCodeForTokenAsync(
        OAuthAppCredentials credentials, string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        // ClickUp takes the exchange parameters as query string on a POST (no request body).
        var url = $"{TokenEndpoint}?client_id={Uri.EscapeDataString(credentials.ClientId)}"
                + $"&client_secret={Uri.EscapeDataString(credentials.ClientSecret)}"
                + $"&code={Uri.EscapeDataString(code)}";

        using var response = await _httpClient.PostAsync(url, content: null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new ClickUpOAuthException(
                $"ClickUp OAuth token exchange failed with HTTP {(int)response.StatusCode}: {Summarize(body)}");

        string? accessToken;
        try
        {
            accessToken = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions)?.AccessToken;
        }
        catch (JsonException ex)
        {
            throw new ClickUpOAuthException($"ClickUp OAuth token response was not valid JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ClickUpOAuthException(
                $"ClickUp OAuth token response did not contain an access_token: {Summarize(body)}");

        return accessToken.Trim();
    }

    /// <summary>Trims a response body for inclusion in an error message.</summary>
    private static string Summarize(string body)
    {
        body = body.Trim();
        return body.Length <= 500 ? body : body[..500] + "…";
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
}
