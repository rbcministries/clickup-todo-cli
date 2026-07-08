using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Setup;

/// <summary>
/// Orchestrates the interactive ClickUp OAuth authorization-code flow (#52): generate a CSRF
/// <c>state</c>, build the authorize URL, launch the browser, capture the <c>code</c> via the
/// loopback listener (with a paste-code fallback when the listener can't bind or times out), and
/// exchange it for an access token. All I/O is injected — the browser launcher, callback listener,
/// token exchange, and console read/write — so the decision logic is unit-testable; only the real
/// browser launch and live redirect can't be verified headlessly.
/// </summary>
public sealed class OAuthSignIn
{
    private readonly IOAuthCallbackListener _listener;
    private readonly IBrowserLauncher _browser;
    private readonly Func<OAuthAppCredentials, string, CancellationToken, Task<string>> _exchangeCode;
    private readonly Action<string> _write;
    private readonly Func<string?> _readLine;
    private readonly Func<string> _newState;
    private readonly TimeSpan _listenerTimeout;

    public OAuthSignIn(
        IOAuthCallbackListener listener,
        IBrowserLauncher browser,
        Func<OAuthAppCredentials, string, CancellationToken, Task<string>> exchangeCode,
        Action<string> write,
        Func<string?> readLine,
        Func<string>? newState = null,
        TimeSpan? listenerTimeout = null)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _exchangeCode = exchangeCode ?? throw new ArgumentNullException(nameof(exchangeCode));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _readLine = readLine ?? throw new ArgumentNullException(nameof(readLine));
        _newState = newState ?? (() => Guid.NewGuid().ToString("N"));
        _listenerTimeout = listenerTimeout ?? TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// Runs the flow and returns the ClickUp OAuth access token, or <see langword="null"/> if the
    /// user cancelled or any step failed (the caller then falls back to the personal-token path).
    /// </summary>
    public async Task<string?> RunAsync(OAuthAppCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var state = _newState();
        var bound = _listener.TryStart();
        var redirectUri = _listener.RedirectUri;
        var authorizeUrl = ClickUpOAuth.BuildAuthorizeUrl(credentials.ClientId, redirectUri, state);

        _write("Opening ClickUp in your browser to authorize…");
        if (!_browser.TryOpen(authorizeUrl))
        {
            _write("Couldn't open a browser automatically. Open this URL to authorize:");
            _write("  " + authorizeUrl);
        }

        string? code;
        if (bound)
        {
            _write($"Waiting for the browser redirect to {redirectUri} …");
            try
            {
                code = await _listener.WaitForCodeAsync(state, _listenerTimeout, ct).ConfigureAwait(false);
            }
            catch (OAuthCallbackException ex)
            {
                // A denied authorization or a failed CSRF-state check is a hard stop, not a fallback.
                _write("OAuth sign-in failed: " + ex.Message);
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _write("Timed out waiting for the browser redirect.");
                code = PromptForCode();
            }
        }
        else
        {
            code = PromptForCode();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            _write("No authorization code provided. OAuth sign-in cancelled.");
            return null;
        }

        try
        {
            var token = await _exchangeCode(credentials, code, ct).ConfigureAwait(false);
            _write("Signed in with ClickUp OAuth.");
            return token;
        }
        catch (ClickUpOAuthException ex)
        {
            _write("OAuth token exchange failed: " + ex.Message);
            return null;
        }
    }

    private string? PromptForCode()
    {
        _write("Paste the authorization code (or the full redirect URL) here and press Enter:");
        return LoopbackOAuthCallbackListener.ExtractCode(_readLine());
    }
}
