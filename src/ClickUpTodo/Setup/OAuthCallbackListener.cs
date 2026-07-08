using System.Net;
using System.Text;

namespace ClickUpTodo.Setup;

/// <summary>The outcome of parsing an OAuth redirect: exactly one of <see cref="Code"/> / <see cref="Error"/>.</summary>
public sealed record CallbackResult(string? Code, string? Error);

/// <summary>Raised when the OAuth callback carries an error or fails the <c>state</c> (CSRF) check.</summary>
public sealed class OAuthCallbackException(string message) : Exception(message);

/// <summary>
/// Captures the <c>code</c> ClickUp sends to the app's registered redirect URL after the user
/// authorizes in the browser (#52). ClickUp OAuth apps register a <b>single, fixed</b> redirect URL,
/// so we bind a loopback listener on that exact host/port (default
/// <see cref="DefaultRedirectUri"/>, overridable via <see cref="RedirectUriEnvVar"/>) rather than a
/// random port. When the bind fails — port busy, or a locked-down environment — <see cref="TryStart"/>
/// returns <see langword="false"/> and the caller falls back to having the user paste the code.
/// </summary>
public interface IOAuthCallbackListener : IDisposable
{
    /// <summary>The registered redirect URL sent as <c>redirect_uri</c> on the authorize URL.</summary>
    string RedirectUri { get; }

    /// <summary>Attempts to bind the loopback listener; <see langword="false"/> if it can't (use paste fallback).</summary>
    bool TryStart();

    /// <summary>
    /// Waits for the browser redirect and returns the authorization <c>code</c>, validating the
    /// <c>state</c>. Throws <see cref="OAuthCallbackException"/> on an error/denied callback or a
    /// <c>state</c> mismatch, and <see cref="OperationCanceledException"/> on timeout/cancellation.
    /// </summary>
    Task<string> WaitForCodeAsync(string expectedState, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Default <see cref="IOAuthCallbackListener"/> backed by a loopback <see cref="HttpListener"/>.</summary>
public sealed class LoopbackOAuthCallbackListener : IOAuthCallbackListener
{
    /// <summary>The redirect URL a user should register for their ClickUp OAuth app by default.</summary>
    public const string DefaultRedirectUri = "http://localhost:53682/callback";

    /// <summary>Env var to override <see cref="DefaultRedirectUri"/> (must match the app's registered URL).</summary>
    public const string RedirectUriEnvVar = "CLICKUP_OAUTH_REDIRECT_URI";

    private readonly string _prefix;
    private HttpListener? _listener;

    public LoopbackOAuthCallbackListener(string redirectUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        RedirectUri = redirectUri.Trim();
        // HttpListener prefixes are matched by path and must end in '/'. Bind the host/port root so
        // the listener catches "/callback?code=…" (and stray requests like /favicon.ico) alike.
        var uri = new Uri(RedirectUri);
        _prefix = $"{uri.Scheme}://{uri.Host}:{uri.Port}/";
    }

    public string RedirectUri { get; }

    /// <summary>Resolves the redirect URL from <see cref="RedirectUriEnvVar"/>, else the default.</summary>
    public static string ResolveRedirectUri(Func<string, string?>? readEnv = null)
    {
        readEnv ??= Environment.GetEnvironmentVariable;
        var configured = readEnv(RedirectUriEnvVar);
        return string.IsNullOrWhiteSpace(configured) ? DefaultRedirectUri : configured.Trim();
    }

    public bool TryStart()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            return true;
        }
        catch (Exception ex) when (ex is HttpListenerException or PlatformNotSupportedException or ObjectDisposedException or ArgumentException)
        {
            _listener?.Close();
            _listener = null;
            return false;
        }
    }

    public async Task<string> WaitForCodeAsync(string expectedState, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_listener is null)
            throw new InvalidOperationException("Listener is not started; call TryStart() first.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Loop so incidental requests (e.g. /favicon.ico) don't consume the wait — only a request
        // that actually carries a code or error resolves it.
        while (true)
        {
            var context = await GetContextAsync(timeoutCts.Token).ConfigureAwait(false);
            var result = ParseCallback(context.Request.Url!, expectedState);

            if (result.Code is null && result.Error is null)
            {
                Respond(context, HttpStatusCode.NoContent, body: null);
                continue;
            }

            Respond(
                context,
                result.Error is null ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                result.Error is null ? SuccessHtml : ErrorHtml(result.Error));

            return result.Error is not null
                ? throw new OAuthCallbackException(result.Error)
                : result.Code!;
        }
    }

    private async Task<HttpListenerContext> GetContextAsync(CancellationToken ct)
    {
        var getContext = _listener!.GetContextAsync();
        var completed = await Task.WhenAny(getContext, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        if (completed != getContext)
            throw new OperationCanceledException(ct);
        return await getContext.ConfigureAwait(false);
    }

    public void Dispose() => _listener?.Close();

    // ── Pure helpers (unit-tested) ─────────────────────────────────────────

    /// <summary>
    /// Interprets an OAuth redirect URL: an <c>error</c> param, a missing <c>code</c>, or (when
    /// <paramref name="expectedState"/> is non-empty) a mismatched <c>state</c> each yield an error;
    /// otherwise the <c>code</c>.
    /// </summary>
    public static CallbackResult ParseCallback(Uri requestUrl, string expectedState)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        var query = ParseQuery(requestUrl.Query);

        if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            return new CallbackResult(null, $"ClickUp reported an authorization error: {error}");

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            return new CallbackResult(null, "The callback did not include an authorization code.");

        if (!string.IsNullOrEmpty(expectedState))
        {
            query.TryGetValue("state", out var state);
            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                return new CallbackResult(null, "The callback state did not match the request (possible CSRF); sign-in aborted.");
        }

        return new CallbackResult(code.Trim(), null);
    }

    /// <summary>
    /// Extracts a <c>code</c> from what the user pastes at the fallback prompt: a full redirect URL,
    /// a bare <c>code=…</c> query fragment, or the raw code itself. Returns <see langword="null"/>
    /// only for blank input.
    /// </summary>
    public static string? ExtractCode(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;
        pasted = pasted.Trim();

        if (Uri.TryCreate(pasted, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query))
        {
            var q = ParseQuery(uri.Query);
            if (q.TryGetValue("code", out var fromUrl) && !string.IsNullOrWhiteSpace(fromUrl))
                return fromUrl.Trim();
        }

        if (pasted.Contains("code", StringComparison.OrdinalIgnoreCase) && pasted.Contains('='))
        {
            var q = ParseQuery(pasted);
            if (q.TryGetValue("code", out var fromFragment) && !string.IsNullOrWhiteSpace(fromFragment))
                return fromFragment.Trim();
        }

        // Assume the user pasted the bare code.
        return pasted;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        query = query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    private static void Respond(HttpListenerContext context, HttpStatusCode status, string? body)
    {
        try
        {
            context.Response.StatusCode = (int)status;
            if (body is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            context.Response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The browser hung up before we could reply — the code was still captured, so ignore.
        }
    }

    private const string SuccessHtml =
        "<!doctype html><meta charset=utf-8><title>Signed in</title>"
        + "<body style='font-family:system-ui;padding:3rem;text-align:center'>"
        + "<h2>✓ Signed in to ClickUp</h2><p>You can close this tab and return to the terminal.</p>";

    private static string ErrorHtml(string message) =>
        "<!doctype html><meta charset=utf-8><title>Sign-in failed</title>"
        + "<body style='font-family:system-ui;padding:3rem;text-align:center'>"
        + "<h2>Sign-in failed</h2><p>" + WebEncode(message) + "</p>"
        + "<p>Return to the terminal to try again.</p>";

    private static string WebEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
