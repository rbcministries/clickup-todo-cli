using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the task↔list membership writes on the <see cref="ClickUpClient"/> facade (#237).
/// They drive the real generated client through a capturing <see cref="HttpMessageHandler"/> (no token,
/// no network), asserting the outgoing verb + URL for <c>POST</c>/<c>DELETE
/// /v2/list/{list_id}/task/{task_id}</c>, that no request body is sent, and that a failure (the "Tasks
/// in Multiple Lists" ClickApp being disabled) surfaces as a <see cref="ClickUpApiException"/> rather
/// than crashing.
/// </summary>
public sealed class ClickUpClientListMembershipTests
{
    [Fact]
    public async Task AddTaskToList_SendsPost_ToListTaskUrl_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.AddTaskToListAsync("t1", "list9");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/list/list9/task/t1", handler.RequestUri);
        Assert.False(handler.HadBody, "add-to-list is a bodyless POST — no request content should be sent.");
    }

    [Fact]
    public async Task RemoveTaskFromList_SendsDelete_ToListTaskUrl_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.RemoveTaskFromListAsync("t1", "list9");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/list/list9/task/t1", handler.RequestUri);
        Assert.False(handler.HadBody, "remove-from-list is a bodyless DELETE — no request content should be sent.");
    }

    [Fact]
    public async Task AddTaskToList_MapsDisabledFeatureError_ToClickUpApiException()
    {
        // ClickUp returns a 4xx when the "Tasks in Multiple Lists" ClickApp is off; the facade must
        // translate it into the app's own exception (carrying the status) so a caller can flash it,
        // never let a raw Kiota ApiException escape.
        var handler = new CapturingHandler(
            """{ "err": "Tasks in multiple lists is not enabled", "ECODE": "SUBCAT_016" }""",
            HttpStatusCode.BadRequest);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.AddTaskToListAsync("t1", "list9"));

        Assert.Equal(400, ex.StatusCode);
        Assert.False(ex.IsAuthFailure);
    }

    [Fact]
    public async Task RemoveTaskFromList_MapsApiError_ToClickUpApiException()
    {
        var handler = new CapturingHandler("""{ "err": "not found" }""", HttpStatusCode.NotFound);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.RemoveTaskFromListAsync("t1", "list9"));

        Assert.Equal(404, ex.StatusCode);
    }

    /// <summary>Records the outgoing request (method, URI, whether a body was sent) and returns a canned
    /// status + body — a bodyless variant of <c>ClickUpClientWriteTests.CapturingHandler</c>.</summary>
    private sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public bool HadBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            HadBody = request.Content is not null
                && (await request.Content.ReadAsStringAsync(cancellationToken)).Length > 0;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
