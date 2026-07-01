using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Verifies the <see cref="ClickUpClient"/> auth-provider seam end-to-end (offline): a provider
/// passed to the new constructor actually shapes the outgoing <c>Authorization</c> header on a real
/// generated call.
/// </summary>
public sealed class ClickUpClientAuthSeamTests
{
    [Fact]
    public async Task OAuthProvider_SendsBearerHeader_OnGeneratedCall()
    {
        var handler = new CapturingHandler("""{ "user": { "id": 42, "username": "tester" } }""");
        using var client = new ClickUpClient(new ClickUpOAuthAuthProvider("tok_seam"), new HttpClient(handler));

        var me = await client.GetMeAsync();

        Assert.Equal(42, me.Id);
        Assert.Equal("Bearer tok_seam", handler.CapturedAuthorization);
    }

    [Fact]
    public async Task PersonalTokenProvider_SendsRawHeader_ViaDelegatingCtor()
    {
        var handler = new CapturingHandler("""{ "user": { "id": 7, "username": "raw" } }""");
        using var client = new ClickUpClient("pk_raw_token", new HttpClient(handler));

        await client.GetMeAsync();

        // The personal-token path sends the token verbatim — no "Bearer" prefix.
        Assert.Equal("pk_raw_token", handler.CapturedAuthorization);
    }

    /// <summary>Captures the outgoing <c>Authorization</c> header and returns a canned JSON body.</summary>
    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? CapturedAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? string.Join(",", values)
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
