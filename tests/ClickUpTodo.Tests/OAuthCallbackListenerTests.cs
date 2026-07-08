using System.Net;
using System.Net.Sockets;
using ClickUpTodo.Setup;

namespace ClickUpTodo.Tests;

/// <summary>
/// The OAuth loopback callback capture (#52): pure parsing of the redirect URL and pasted input
/// (always-on), plus one real local HTTP round-trip through <see cref="LoopbackOAuthCallbackListener"/>
/// (skipped if the environment won't let an <see cref="HttpListener"/> bind — no ClickUp needed).
/// </summary>
public sealed class OAuthCallbackListenerTests
{
    // ── ParseCallback ──────────────────────────────────────────────────────

    [Fact]
    public void ParseCallback_CodeWithMatchingState_ReturnsCode()
    {
        var result = LoopbackOAuthCallbackListener.ParseCallback(
            new Uri("http://localhost:53682/callback?code=abc123&state=s1"), "s1");

        Assert.Equal("abc123", result.Code);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseCallback_EmptyExpectedState_SkipsStateCheck()
    {
        var result = LoopbackOAuthCallbackListener.ParseCallback(
            new Uri("http://localhost:53682/callback?code=abc123"), expectedState: "");

        Assert.Equal("abc123", result.Code);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseCallback_MismatchedState_IsError()
    {
        var result = LoopbackOAuthCallbackListener.ParseCallback(
            new Uri("http://localhost:53682/callback?code=abc123&state=wrong"), "s1");

        Assert.Null(result.Code);
        Assert.Contains("state did not match", result.Error);
    }

    [Fact]
    public void ParseCallback_MissingState_WhenStateExpected_IsError()
    {
        var result = LoopbackOAuthCallbackListener.ParseCallback(
            new Uri("http://localhost:53682/callback?code=abc123"), "s1");

        Assert.Null(result.Code);
        Assert.Contains("state did not match", result.Error);
    }

    [Fact]
    public void ParseCallback_ErrorParam_IsError()
    {
        var result = LoopbackOAuthCallbackListener.ParseCallback(
            new Uri("http://localhost:53682/callback?error=access_denied&state=s1"), "s1");

        Assert.Null(result.Code);
        Assert.Contains("access_denied", result.Error);
    }

    [Fact]
    public void ParseCallback_NoCode_IsError()
    {
        var result = LoopbackOAuthCallbackListener.ParseCallback(
            new Uri("http://localhost:53682/favicon.ico"), "s1");

        Assert.Null(result.Code);
        Assert.Contains("did not include an authorization code", result.Error);
    }

    [Fact]
    public void ParseCallback_UrlEncodedCode_IsUnescaped()
    {
        var result = LoopbackOAuthCallbackListener.ParseCallback(
            new Uri("http://localhost:53682/callback?code=a%2Bb%3Dc&state=s1"), "s1");

        Assert.Equal("a+b=c", result.Code);
    }

    // ── ExtractCode (paste fallback) ───────────────────────────────────────

    [Fact]
    public void ExtractCode_BareCode_ReturnsItVerbatim()
    {
        Assert.Equal("abc123", LoopbackOAuthCallbackListener.ExtractCode("abc123"));
        Assert.Equal("abc123", LoopbackOAuthCallbackListener.ExtractCode("  abc123  "));
    }

    [Fact]
    public void ExtractCode_FullRedirectUrl_PullsCode()
    {
        Assert.Equal(
            "abc123",
            LoopbackOAuthCallbackListener.ExtractCode("http://localhost:53682/callback?code=abc123&state=s1"));
    }

    [Fact]
    public void ExtractCode_QueryFragment_PullsCode()
    {
        Assert.Equal("abc123", LoopbackOAuthCallbackListener.ExtractCode("code=abc123&state=s1"));
    }

    [Fact]
    public void ExtractCode_Blank_ReturnsNull()
    {
        Assert.Null(LoopbackOAuthCallbackListener.ExtractCode(""));
        Assert.Null(LoopbackOAuthCallbackListener.ExtractCode("   "));
        Assert.Null(LoopbackOAuthCallbackListener.ExtractCode(null));
    }

    // ── ResolveRedirectUri ─────────────────────────────────────────────────

    [Fact]
    public void ResolveRedirectUri_NoEnv_UsesDefault()
    {
        Assert.Equal(
            LoopbackOAuthCallbackListener.DefaultRedirectUri,
            LoopbackOAuthCallbackListener.ResolveRedirectUri(_ => null));
    }

    [Fact]
    public void ResolveRedirectUri_EnvOverride_Wins()
    {
        var over = "http://localhost:9000/cb";
        Assert.Equal(over, LoopbackOAuthCallbackListener.ResolveRedirectUri(name =>
            name == LoopbackOAuthCallbackListener.RedirectUriEnvVar ? over : null));
    }

    // ── Live loopback round-trip ───────────────────────────────────────────

    [SkippableFact]
    public async Task WaitForCodeAsync_ReceivesCodeFromLoopbackRedirect()
    {
        var redirectUri = $"http://localhost:{FreeLoopbackPort()}/callback";
        using var listener = new LoopbackOAuthCallbackListener(redirectUri);
        Skip.IfNot(listener.TryStart(), "HttpListener could not bind a loopback port in this environment.");

        var waitTask = listener.WaitForCodeAsync("st8", TimeSpan.FromSeconds(10));

        using var http = new HttpClient();
        using var response = await http.GetAsync($"{redirectUri}?code=live_code&state=st8");

        Assert.Equal("live_code", await waitTask);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task WaitForCodeAsync_StateMismatch_Throws()
    {
        var redirectUri = $"http://localhost:{FreeLoopbackPort()}/callback";
        using var listener = new LoopbackOAuthCallbackListener(redirectUri);
        Skip.IfNot(listener.TryStart(), "HttpListener could not bind a loopback port in this environment.");

        var waitTask = listener.WaitForCodeAsync("expected", TimeSpan.FromSeconds(10));

        using var http = new HttpClient();
        using var response = await http.GetAsync($"{redirectUri}?code=live_code&state=tampered");

        await Assert.ThrowsAsync<OAuthCallbackException>(() => waitTask);
    }

    /// <summary>Grabs a currently-free loopback TCP port so the listener test doesn't collide.</summary>
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
