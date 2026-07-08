using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Setup;

namespace ClickUpTodo.Tests;

/// <summary>
/// The <see cref="OAuthSignIn"/> orchestration decision logic (#52), exercised with fake seams:
/// listener success, hard-stop on a CSRF/denied callback, the paste fallback (no bind / timeout),
/// cancellation, and exchange failure. The real browser launch and live redirect aren't covered
/// (not headless-verifiable).
/// </summary>
public sealed class OAuthSignInTests
{
    private static readonly OAuthAppCredentials Creds = new("client_id", "client_secret");

    [Fact]
    public async Task ListenerReturnsCode_ExchangesAndReturnsToken()
    {
        string? seenState = null;
        string? exchangedCode = null;
        var listener = new FakeListener { OnWait = s => { seenState = s; return Task.FromResult("redirect_code"); } };
        var browser = new FakeBrowser();
        var flow = new OAuthSignIn(
            listener, browser,
            (_, code, _) => { exchangedCode = code; return Task.FromResult("access_tok"); },
            _ => { }, () => null, newState: () => "STATE123");

        var token = await flow.RunAsync(Creds);

        Assert.Equal("access_tok", token);
        Assert.Equal("redirect_code", exchangedCode);
        Assert.Equal("STATE123", seenState);
        // The authorize URL carries the client id, redirect uri, and CSRF state.
        Assert.Contains("client_id=client_id", browser.Opened!.Query);
        Assert.Contains("state=STATE123", browser.Opened.Query);
        Assert.Contains(Uri.EscapeDataString(listener.RedirectUri), browser.Opened.Query);
    }

    [Fact]
    public async Task CallbackError_IsHardStop_NoExchange()
    {
        var exchanged = false;
        var listener = new FakeListener { OnWait = _ => throw new OAuthCallbackException("state mismatch") };
        var flow = new OAuthSignIn(
            listener, new FakeBrowser(),
            (_, _, _) => { exchanged = true; return Task.FromResult("nope"); },
            _ => { }, () => null);

        var token = await flow.RunAsync(Creds);

        Assert.Null(token);
        Assert.False(exchanged);
    }

    [Fact]
    public async Task NoBind_UsesPasteFallback_ExtractsCodeFromPastedUrl()
    {
        string? exchangedCode = null;
        var listener = new FakeListener { StartResult = false };
        var flow = new OAuthSignIn(
            listener, new FakeBrowser(),
            (_, code, _) => { exchangedCode = code; return Task.FromResult("tok_pasted"); },
            _ => { },
            readLine: () => "http://localhost:53682/callback?code=pasted123&state=x");

        var token = await flow.RunAsync(Creds);

        Assert.Equal("tok_pasted", token);
        Assert.Equal("pasted123", exchangedCode);
    }

    [Fact]
    public async Task NoBind_BlankPaste_ReturnsNull()
    {
        var exchanged = false;
        var listener = new FakeListener { StartResult = false };
        var flow = new OAuthSignIn(
            listener, new FakeBrowser(),
            (_, _, _) => { exchanged = true; return Task.FromResult("x"); },
            _ => { }, readLine: () => "   ");

        Assert.Null(await flow.RunAsync(Creds));
        Assert.False(exchanged);
    }

    [Fact]
    public async Task ListenerTimeout_FallsBackToPaste()
    {
        string? exchangedCode = null;
        var listener = new FakeListener
        {
            OnWait = _ => throw new OperationCanceledException(),
        };
        var flow = new OAuthSignIn(
            listener, new FakeBrowser(),
            (_, code, _) => { exchangedCode = code; return Task.FromResult("tok_after_timeout"); },
            _ => { }, readLine: () => "code_after_timeout");

        var token = await flow.RunAsync(Creds);

        Assert.Equal("tok_after_timeout", token);
        Assert.Equal("code_after_timeout", exchangedCode);
    }

    [Fact]
    public async Task OuterCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var listener = new FakeListener { OnWait = _ => throw new OperationCanceledException(cts.Token) };
        var flow = new OAuthSignIn(
            listener, new FakeBrowser(),
            (_, _, _) => Task.FromResult("x"),
            _ => { }, () => null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.RunAsync(Creds, cts.Token));
    }

    [Fact]
    public async Task ExchangeFailure_ReturnsNull()
    {
        var listener = new FakeListener { OnWait = _ => Task.FromResult("code") };
        var flow = new OAuthSignIn(
            listener, new FakeBrowser(),
            (_, _, _) => throw new ClickUpOAuthException("bad code"),
            _ => { }, () => null);

        Assert.Null(await flow.RunAsync(Creds));
    }

    [Fact]
    public async Task BrowserFailsToOpen_StillProceedsViaListener()
    {
        var listener = new FakeListener { OnWait = _ => Task.FromResult("code") };
        var browser = new FakeBrowser { OpenResult = false };
        var flow = new OAuthSignIn(
            listener, browser,
            (_, _, _) => Task.FromResult("tok"),
            _ => { }, () => null);

        Assert.Equal("tok", await flow.RunAsync(Creds));
        Assert.NotNull(browser.Opened); // it still attempted (and would print the URL)
    }

    private sealed class FakeListener : IOAuthCallbackListener
    {
        public bool StartResult { get; init; } = true;
        public Func<string, Task<string>>? OnWait { get; init; }
        public string RedirectUri { get; init; } = "http://localhost:53682/callback";

        public bool TryStart() => StartResult;

        public Task<string> WaitForCodeAsync(string expectedState, TimeSpan timeout, CancellationToken ct = default)
            => OnWait!(expectedState);

        public void Dispose() { }
    }

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public bool OpenResult { get; init; } = true;
        public Uri? Opened { get; private set; }

        public bool TryOpen(Uri url)
        {
            Opened = url;
            return OpenResult;
        }
    }
}
