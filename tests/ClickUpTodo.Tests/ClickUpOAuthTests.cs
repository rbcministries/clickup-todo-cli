using System.Net;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

public sealed class ClickUpOAuthTests
{
    private static readonly OAuthAppCredentials Creds = new("cid 1", "csecret/2");

    // ── BuildAuthorizeUrl ───────────────────────────────────────────────────

    [Fact]
    public void BuildAuthorizeUrl_ComposesAndEscapes()
    {
        var url = ClickUpOAuth.BuildAuthorizeUrl("client id", "https://localhost:1234/callback");

        Assert.Equal(
            "https://app.clickup.com/api?client_id=client%20id&redirect_uri=https%3A%2F%2Flocalhost%3A1234%2Fcallback&response_type=code",
            url.AbsoluteUri);
    }

    [Fact]
    public void BuildAuthorizeUrl_IncludesState_WhenProvided()
    {
        var url = ClickUpOAuth.BuildAuthorizeUrl("cid", "https://localhost/callback", "xyz/state");

        Assert.Contains("&state=xyz%2Fstate", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "https://localhost/callback")]
    [InlineData("cid", "")]
    [InlineData("  ", "https://localhost/callback")]
    public void BuildAuthorizeUrl_Throws_OnEmptyArgs(string clientId, string redirectUri)
        => Assert.Throws<ArgumentException>(() => ClickUpOAuth.BuildAuthorizeUrl(clientId, redirectUri));

    // ── ExchangeCodeForTokenAsync ───────────────────────────────────────────

    [Fact]
    public async Task Exchange_ReturnsAccessToken_AndSendsCredentials()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{ "access_token": "tok_abc", "token_type": "Bearer" }""");
        var oauth = new ClickUpOAuth(new HttpClient(handler));

        var token = await oauth.ExchangeCodeForTokenAsync(Creds, "the code");

        Assert.Equal("tok_abc", token);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        var requested = handler.LastRequest.RequestUri!.AbsoluteUri;
        Assert.StartsWith(ClickUpOAuth.TokenEndpoint, requested, StringComparison.Ordinal);
        Assert.Contains("client_id=cid%201", requested, StringComparison.Ordinal);
        Assert.Contains("client_secret=csecret%2F2", requested, StringComparison.Ordinal);
        Assert.Contains("code=the%20code", requested, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_Throws_OnNonSuccess_WithBody()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{ "err": "Oauth code already used", "ECODE": "OAUTH_017" }""");
        var oauth = new ClickUpOAuth(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpOAuthException>(
            () => oauth.ExchangeCodeForTokenAsync(Creds, "code"));

        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OAUTH_017", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_Throws_WhenAccessTokenMissing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{ "token_type": "Bearer" }""");
        var oauth = new ClickUpOAuth(new HttpClient(handler));

        await Assert.ThrowsAsync<ClickUpOAuthException>(
            () => oauth.ExchangeCodeForTokenAsync(Creds, "code"));
    }

    [Fact]
    public async Task Exchange_Throws_OnEmptyCode()
    {
        var oauth = new ClickUpOAuth(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")));

        await Assert.ThrowsAsync<ArgumentException>(
            () => oauth.ExchangeCodeForTokenAsync(Creds, "  "));
    }

    /// <summary>Captures the outgoing request and returns a canned response — no network.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
