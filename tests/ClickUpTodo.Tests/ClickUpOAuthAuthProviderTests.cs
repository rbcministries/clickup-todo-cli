using ClickUpTodo.ClickUp;
using Microsoft.Kiota.Abstractions;

namespace ClickUpTodo.Tests;

public sealed class ClickUpOAuthAuthProviderTests
{
    private static RequestInformation Request(string url)
        => new() { HttpMethod = Method.GET, URI = new Uri(url) };

    private static string? AuthHeader(RequestInformation request)
        => request.Headers.TryGetValue("Authorization", out var values) ? values.Single() : null;

    [Fact]
    public async Task AddsBearerHeader_ForClickUpHost()
    {
        var provider = new ClickUpOAuthAuthProvider("access_123");
        var request = Request("https://api.clickup.com/api/v2/user");

        await provider.AuthenticateRequestAsync(request);

        Assert.Equal("Bearer access_123", AuthHeader(request));
    }

    [Fact]
    public async Task SkipsHeader_ForNonClickUpHost()
    {
        var provider = new ClickUpOAuthAuthProvider("access_123");
        var request = Request("https://example.com/redirected");

        await provider.AuthenticateRequestAsync(request);

        Assert.Null(AuthHeader(request));
    }

    [Fact]
    public async Task ReplacesExistingHeader_WithoutDuplicating()
    {
        var provider = new ClickUpOAuthAuthProvider("access_123");
        var request = Request("https://api.clickup.com/api/v2/user");
        request.Headers.Add("Authorization", "stale");

        await provider.AuthenticateRequestAsync(request);

        Assert.Equal("Bearer access_123", AuthHeader(request));
    }

    [Fact]
    public async Task StripsCallerSuppliedBearerPrefix()
    {
        var provider = new ClickUpOAuthAuthProvider("Bearer access_123");
        var request = Request("https://api.clickup.com/api/v2/user");

        await provider.AuthenticateRequestAsync(request);

        Assert.Equal("Bearer access_123", AuthHeader(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer ")]
    public void Constructor_Throws_OnEmptyToken(string token)
        => Assert.Throws<ArgumentException>(() => new ClickUpOAuthAuthProvider(token));
}
