using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for <see cref="ClickUpClient.DeleteCommentAsync"/> (#594, the deferred comment half of the
/// contextual-Delete slice #543). They drive the real generated client through a capturing
/// <see cref="HttpMessageHandler"/> (no token, no network), asserting the outgoing
/// <c>DELETE /comment/{comment_id}</c> method + URL (a body-less, path-only mutation aimed at the comment
/// endpoint, not the <c>/reply</c> thread endpoint), that a non-author permission error surfaces as a caught
/// <see cref="ClickUpApiException"/> rather than a raw Kiota exception, and that a blank id fails fast at the
/// boundary before any transport call — mirroring <see cref="ClickUpClientDeleteTaskTests"/>.
/// </summary>
public sealed class ClickUpClientCommentDeleteTests
{
    [Fact]
    public async Task DeleteComment_SendsDelete_ToCommentEndpoint_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.DeleteCommentAsync("c1");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/comment/c1", handler.RequestUri);
        Assert.DoesNotContain("/reply", handler.RequestUri); // the comment endpoint, not the thread endpoint
        Assert.True(handler.BodyWasEmpty, "delete-comment is a path-only mutation and must send no request body.");
    }

    [Fact]
    public async Task DeleteComment_NonAuthorPermissionError_SurfacesAsClickUpApiException()
    {
        // ClickUp rejects deleting someone else's comment; the facade wraps it (like every other write) so
        // the caller can revert its optimistic removal and flash, rather than seeing a raw Kiota exception.
        var handler = new CapturingHandler("""{ "err": "You do not have permission", "ECODE": "OAUTH_027" }""", HttpStatusCode.Forbidden);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.DeleteCommentAsync("c1"));

        Assert.Equal(403, ex.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DeleteComment_BlankId_ThrowsWithoutTransport(string? commentId)
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        // ThrowsAny: a null id throws ArgumentNullException, a blank/whitespace one ArgumentException — both
        // derive from ArgumentException and both must reject before any transport call.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.DeleteCommentAsync(commentId!));

        Assert.False(handler.WasCalled, "a blank comment id must be rejected before any transport call.");
    }

    /// <summary>Records the outgoing request (method, URI, whether a body was present, whether it was called
    /// at all) and returns a canned response with a configurable status code.</summary>
    private sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public bool BodyWasEmpty { get; private set; }
        public bool WasCalled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            BodyWasEmpty = request.Content is null
                || (await request.Content.ReadAsStringAsync(cancellationToken)).Length == 0;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
