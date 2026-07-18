using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

public sealed class ClickUpUrlTests
{
    // ── NormalizeSubdomain ──────────────────────────────────────────────────

    [Theory]
    [InlineData("odbm", "odbm")]                                  // bare label
    [InlineData("odbm.clickup.com", "odbm")]                      // full host
    [InlineData("https://odbm.clickup.com", "odbm")]             // URL, no trailing slash
    [InlineData("https://odbm.clickup.com/", "odbm")]            // URL, trailing slash
    [InlineData("http://odbm.clickup.com/12345/v/l/li", "odbm")] // URL with a path
    [InlineData("odbm.clickup.com:443", "odbm")]                 // host with a port
    [InlineData("  ODBM  ", "odbm")]                              // trimmed + lowercased
    [InlineData("my-team", "my-team")]                            // hyphens are valid in a label
    [InlineData("odbm.example.com", "odbm")]                      // first label of any host
    public void NormalizeSubdomain_ExtractsBareLabel(string input, string expected)
        => Assert.Equal(expected, ClickUpUrl.NormalizeSubdomain(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("app")]                 // ClickUp's generic web host — not a workspace subdomain
    [InlineData("app.clickup.com")]
    [InlineData("api")]                 // ClickUp's API host
    [InlineData("api.clickup.com")]
    [InlineData("bad_label")]           // underscore isn't a valid DNS-label char
    [InlineData("has space")]
    [InlineData("emoji😀")]
    public void NormalizeSubdomain_ReturnsBlankForUnsetOrInvalid(string? input)
        => Assert.Equal("", ClickUpUrl.NormalizeSubdomain(input));

    // ── RewriteHost ─────────────────────────────────────────────────────────

    [Fact]
    public void RewriteHost_SwapsHostAndPreservesPath()
        => Assert.Equal(
            "https://odbm.clickup.com/t/abc123",
            ClickUpUrl.RewriteHost("https://app.clickup.com/t/abc123", "odbm"));

    [Fact]
    public void RewriteHost_PreservesQueryAndFragment()
        => Assert.Equal(
            "https://odbm.clickup.com/t/9hz?comment=1#c2",
            ClickUpUrl.RewriteHost("https://app.clickup.com/t/9hz?comment=1#c2", "odbm"));

    [Fact]
    public void RewriteHost_DoesNotIntroduceAnExplicitDefaultPort()
        => Assert.DoesNotContain(":443", ClickUpUrl.RewriteHost("https://app.clickup.com/t/x", "odbm"));

    [Fact]
    public void RewriteHost_AcceptsAHostOrUrlAsTheSubdomain()
        => Assert.Equal(
            "https://odbm.clickup.com/t/x",
            ClickUpUrl.RewriteHost("https://app.clickup.com/t/x", "https://odbm.clickup.com/"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("app")]  // normalizes to blank ⇒ no rewrite
    public void RewriteHost_LeavesUrlUnchangedWhenSubdomainUnset(string? subdomain)
    {
        const string url = "https://app.clickup.com/t/abc123";
        Assert.Equal(url, ClickUpUrl.RewriteHost(url, subdomain));
    }

    [Theory]
    [InlineData("https://api.clickup.com/v2/task/abc")]          // not the app host
    [InlineData("https://odbm.clickup.com/t/abc")]               // already on the workspace host
    [InlineData("https://example.com/whatever")]                 // a non-ClickUp host
    [InlineData("ftp://app.clickup.com/t/abc")]                  // non-http scheme
    [InlineData("not a url")]                                     // unparseable
    [InlineData("/t/relative")]                                   // relative
    public void RewriteHost_LeavesNonAppUrlsUnchanged(string url)
        => Assert.Equal(url, ClickUpUrl.RewriteHost(url, "odbm"));

    [Fact]
    public void RewriteHost_MatchesAppHostCaseInsensitively()
        => Assert.Equal(
            "https://odbm.clickup.com/t/x",
            ClickUpUrl.RewriteHost("https://APP.ClickUp.com/t/x", "odbm"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RewriteHost_HandlesBlankUrl(string? url)
        => Assert.Equal("", ClickUpUrl.RewriteHost(url, "odbm"));
}
