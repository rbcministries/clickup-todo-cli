using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// <see cref="ClickUpClientFactory"/> selects the auth provider from <see cref="AppConfig.AuthMode"/>
/// (#52): OAuth ⇒ <c>Authorization: Bearer &lt;token&gt;</c>, default ⇒ the raw personal-token header.
/// Verified end-to-end on a real generated call via a capturing handler, mirroring
/// <see cref="ClickUpClientAuthSeamTests"/>.
/// </summary>
public sealed class ClickUpClientFactoryTests
{
    [Fact]
    public void AuthProviderFor_OAuth_IsBearerProvider()
    {
        Assert.IsType<ClickUpOAuthAuthProvider>(ClickUpClientFactory.AuthProviderFor(AuthMode.OAuth, "tok"));
    }

    [Fact]
    public void AuthProviderFor_PersonalToken_IsRawProvider()
    {
        Assert.IsType<ClickUpTokenAuthProvider>(ClickUpClientFactory.AuthProviderFor(AuthMode.PersonalToken, "pk_x"));
    }

    [Fact]
    public async Task Create_OAuthMode_SendsBearerHeader()
    {
        var handler = new CapturingHandler("""{ "user": { "id": 1, "username": "o" } }""");
        var config = new AppConfig { AuthMode = AuthMode.OAuth };

        using var client = ClickUpClientFactory.Create(config, "tok_oauth", new HttpClient(handler));
        await client.GetMeAsync();

        Assert.Equal("Bearer tok_oauth", handler.CapturedAuthorization);
    }

    [Fact]
    public async Task Create_PersonalTokenMode_SendsRawHeader()
    {
        var handler = new CapturingHandler("""{ "user": { "id": 1, "username": "p" } }""");
        var config = new AppConfig { AuthMode = AuthMode.PersonalToken };

        using var client = ClickUpClientFactory.Create(config, "pk_raw", new HttpClient(handler));
        await client.GetMeAsync();

        Assert.Equal("pk_raw", handler.CapturedAuthorization);
    }

    [Fact]
    public async Task Create_DefaultMode_SendsRawHeader()
    {
        // A fresh AppConfig (AuthMode unset) must behave as personal-token.
        var handler = new CapturingHandler("""{ "user": { "id": 1, "username": "d" } }""");

        using var client = ClickUpClientFactory.Create(new AppConfig(), "pk_default", new HttpClient(handler));
        await client.GetMeAsync();

        Assert.Equal("pk_default", handler.CapturedAuthorization);
    }

    [Fact]
    public void Create_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ClickUpClientFactory.Create(null!, "tok"));
    }

    [Fact]
    public void CreateHttpClient_BuildsTheGovernedPipeline()
    {
        // The default (no caller-supplied HttpClient) path must assemble Kiota's middleware with the
        // rate-limit governor appended — a construction smoke test so a Kiota API change here fails
        // loudly rather than at first refresh.
        using var httpClient = ClickUpClientFactory.CreateHttpClient();
        Assert.NotNull(httpClient);
    }

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
