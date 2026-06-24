using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Authenticates Kiota requests with a ClickUp personal API token.
/// <para>
/// ClickUp personal tokens are sent as the <c>Authorization</c> header value <em>verbatim</em>
/// (e.g. <c>Authorization: pk_12345_ABCDE</c>) — there is no <c>Bearer</c> prefix. Kiota's built-in
/// <c>BaseBearerTokenAuthenticationProvider</c> always prepends <c>Bearer </c>, so this custom
/// provider is required to set the raw header instead.
/// </para>
/// </summary>
public sealed class ClickUpTokenAuthProvider(string token) : IAuthenticationProvider
{
    private const string AuthorizationHeader = "Authorization";
    private static readonly AllowedHostsValidator AllowedHosts = new(["api.clickup.com"]);

    private readonly string _token = string.IsNullOrWhiteSpace(token)
        ? throw new ArgumentException("Token must not be empty.", nameof(token))
        : token.Trim();

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
            request.Headers.Add(AuthorizationHeader, _token);
        }

        return Task.CompletedTask;
    }
}
