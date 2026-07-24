using System.Net;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

public sealed class SubdomainProbeTests
{
    /// <summary>
    /// A fake handler that simulates a followed redirect: it returns a response whose
    /// <see cref="HttpResponseMessage.RequestMessage"/> points at <paramref name="finalUrl"/> — exactly what
    /// a real redirect-following <see cref="HttpClient"/> leaves behind — so the probe's host extraction can
    /// be exercised with no network. A null <paramref name="finalUrl"/> models a probe that never resolved a
    /// final URL; <paramref name="throwOf"/> models a transport failure.
    /// </summary>
    private sealed class FakeHandler(string? finalUrl, Exception? throwOf = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throwOf is not null)
                throw throwOf;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = finalUrl is null ? null : new HttpRequestMessage(HttpMethod.Get, finalUrl),
            });
        }
    }

    private static SubdomainProbe ProbeReturning(string? finalUrl, Exception? throwOf = null)
        => new(new HttpClient(new FakeHandler(finalUrl, throwOf)));

    [Fact]
    public async Task DetectAsync_ReturnsLabelWhenProbeRedirectsToWorkspaceHost()
    {
        var probe = ProbeReturning("https://odbm.clickup.com/12345/home");
        Assert.Equal("odbm", await probe.DetectAsync());
    }

    [Fact]
    public async Task DetectAsync_ReturnsBlankWhenProbeStaysOnAppHost()
    {
        var probe = ProbeReturning("https://app.clickup.com/login");
        Assert.Equal("", await probe.DetectAsync());
    }

    [Fact]
    public async Task DetectAsync_ReturnsBlankWhenNoFinalUrl()
    {
        var probe = ProbeReturning(null);
        Assert.Equal("", await probe.DetectAsync());
    }

    [Fact]
    public async Task DetectAsync_ReturnsBlankOnTransportFailure()
    {
        var probe = ProbeReturning(null, new HttpRequestException("no network"));
        Assert.Equal("", await probe.DetectAsync());
    }

    [Fact]
    public async Task DetectAsync_ReturnsBlankOnTimeout()
    {
        // HttpClient surfaces its timeout as a TaskCanceledException; the probe treats it as "not detected".
        var probe = ProbeReturning(null, new TaskCanceledException("timed out"));
        Assert.Equal("", await probe.DetectAsync());
    }
}
