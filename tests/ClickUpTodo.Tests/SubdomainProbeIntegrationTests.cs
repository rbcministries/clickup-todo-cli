using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// A live, opt-in check of the workspace-subdomain auto-detect probe (#351) against the real
/// <c>app.clickup.com</c>. It self-skips unless <c>CLICKUP_SUBDOMAIN_PROBE</c> is set, so it never runs in
/// normal CI — the probe hits the public web (not the API), the <c>app.clickup.com</c> → workspace redirect
/// is session-cookie-gated so an anonymous probe may not fire it, and sandboxed CI networks block
/// <c>app.clickup.com</c> outright. It exists so a maintainer can settle empirically whether the redirect
/// happens for an unauthenticated request on their own network.
/// <para>
/// Run it with <c>CLICKUP_SUBDOMAIN_PROBE=1</c>. Also set <c>CLICKUP_EXPECTED_SUBDOMAIN=&lt;label&gt;</c>
/// to assert the detected value; without it the test only asserts the probe completes and returns a
/// normalized label (possibly <c>""</c> when no redirect fired), and reports what it saw.
/// </para>
/// </summary>
public sealed class SubdomainProbeIntegrationTests
{
    private static bool Enabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLICKUP_SUBDOMAIN_PROBE"));

    private static string? Expected => Environment.GetEnvironmentVariable("CLICKUP_EXPECTED_SUBDOMAIN");

    [SkippableFact]
    public async Task DetectAsync_ProbesRealClickUp()
    {
        Skip.IfNot(Enabled, "Set CLICKUP_SUBDOMAIN_PROBE=1 to run the live subdomain-detect probe.");

        var detected = await SubdomainProbe.Default().DetectAsync();

        // Whatever comes back must be a clean workspace label or the "" sentinel — never a raw host.
        Assert.Equal(detected, ClickUpUrl.NormalizeSubdomain(detected));

        if (!string.IsNullOrWhiteSpace(Expected))
            Assert.Equal(ClickUpUrl.NormalizeSubdomain(Expected), detected);
    }
}
