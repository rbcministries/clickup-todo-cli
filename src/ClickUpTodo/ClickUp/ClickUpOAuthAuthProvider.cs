using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Authenticates Kiota requests with a ClickUp <em>OAuth</em> access token.
/// <para>
/// Unlike a personal token (see <see cref="ClickUpTokenAuthProvider"/>, which sends the token
/// <em>verbatim</em> with no prefix), an OAuth access token is sent with the standard
/// <c>Bearer </c> prefix — <c>Authorization: Bearer &lt;access_token&gt;</c>. A caller-supplied
/// <c>Bearer </c> prefix is normalised away so the header is never doubled.
/// </para>
/// </summary>
public sealed class ClickUpOAuthAuthProvider : IAuthenticationProvider
{
    private const string AuthorizationHeader = "Authorization";
    private const string BearerScheme = "Bearer";
    private const string BearerPrefix = BearerScheme + " ";
    private static readonly AllowedHostsValidator AllowedHosts = new(["api.clickup.com"]);

    private readonly string _accessToken;

    public ClickUpOAuthAuthProvider(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token must not be empty.", nameof(accessToken));

        // Normalise a caller-supplied "Bearer " prefix so we never emit "Bearer Bearer …". Only
        // strip when "Bearer" is followed by whitespace (or nothing), so a token that merely starts
        // with the letters "Bearer" is left intact.
        var value = accessToken.Trim();
        if (value.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[BearerScheme.Length..];
            if (rest.Length == 0 || char.IsWhiteSpace(rest[0]))
                value = rest.Trim();
        }

        if (value.Length == 0)
            throw new ArgumentException("Access token must not be empty.", nameof(accessToken));

        _accessToken = value;
    }

    public Task AuthenticateRequestAsync(
        RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Only attach the token to ClickUp hosts, never to a redirected third-party host.
        if (AllowedHosts.IsUrlHostValid(request.URI))
        {
            request.Headers.Remove(AuthorizationHeader);
            request.Headers.Add(AuthorizationHeader, BearerPrefix + _accessToken);
        }

        return Task.CompletedTask;
    }
}
