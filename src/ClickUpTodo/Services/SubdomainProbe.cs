namespace ClickUpTodo.Services;

/// <summary>
/// Best-effort auto-detection of the workspace subdomain (#351, follow-up to #304). Issues a plain web
/// request to <see cref="ProbeUrl"/> (<c>app.clickup.com</c>), follows the redirect chain, and reports the
/// workspace label if it landed on a <c>{label}.clickup.com</c> host (via
/// <see cref="ClickUpUrl.SubdomainFromFinalUrl"/>). It's an <b>opt-in convenience</b>: the user-entered
/// <see cref="Configuration.AppConfig.WorkspaceSubdomain"/> (#304) stays the always-correct path, so any
/// probe failure — network error, timeout, or a redirect that stays on <c>app.clickup.com</c> — resolves to
/// <c>""</c> ("couldn't determine") rather than throwing into the UI.
/// <para>
/// Note: the <c>app.clickup.com</c> → workspace redirect is, in a browser, driven by the signed-in web
/// session cookie rather than the API token, so an anonymous probe may not redirect. Detection is therefore
/// surfaced as a manual "Detect" action that fails soft; see <c>docs/plans/auto-detect-workspace-subdomain.md</c>.
/// </para>
/// The <see cref="HttpClient"/> is injected (and must be configured to follow redirects) so the detection
/// logic unit-tests against a fake handler with no real network.
/// </summary>
public sealed class SubdomainProbe
{
    /// <summary>The host we probe; its post-redirect final host is what we read the subdomain from.</summary>
    public static readonly Uri ProbeUrl = new($"https://{ClickUpUrl.AppHost}/");

    private static readonly SubdomainProbe DefaultInstance = new(CreateRedirectFollowingClient());

    private readonly HttpClient _http;

    public SubdomainProbe(HttpClient http) => _http = http;

    /// <summary>
    /// The process-lifetime default probe over a redirect-following <see cref="HttpClient"/> — so the
    /// default wiring in <see cref="Tui.TodoApp"/> needs no new disposable to own. Tests and the E2E
    /// harness substitute their own detector delegate instead of using this.
    /// </summary>
    public static SubdomainProbe Default() => DefaultInstance;

    /// <summary>
    /// Probes <see cref="ProbeUrl"/> and returns the detected workspace subdomain label, or <c>""</c> when
    /// it can't be determined. Never throws: transport, timeout, cancellation, and parse failures all map to
    /// <c>""</c> so a caller can treat "no detection" and "failed" identically (leave the manual value).
    /// </summary>
    public async Task<string> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            // RequestMessage.RequestUri reflects the final URL after the client followed the redirect chain.
            return ClickUpUrl.SubdomainFromFinalUrl(response.RequestMessage?.RequestUri);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return "";
        }
    }

    private static HttpClient CreateRedirectFollowingClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }
}
