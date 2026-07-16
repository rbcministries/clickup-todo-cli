using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the task↔list membership writes on the <see cref="ClickUpClient"/> facade (#237,
/// ClickUp's "Tasks in Multiple Lists"). They drive the real generated client through a capturing
/// <see cref="HttpMessageHandler"/> (no token, no network), asserting the outgoing HTTP method + URL
/// shape (<c>/v2/list/{list_id}/task/{task_id}</c>, no body) and that a non-2xx response surfaces as a
/// typed <see cref="ClickUpApiException"/> — the shape the disabled-ClickApp case takes at the
/// downstream call sites (#241/#242).
/// </summary>
public sealed class ClickUpClientListMembershipTests
{
    [Fact]
    public async Task AddTaskToList_SendsPost_ToListTaskUrl_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.AddTaskToListAsync("t1", "l9");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/list/l9/task/t1", handler.RequestUri);
        Assert.True(string.IsNullOrEmpty(handler.RequestBody), "add-to-list must not send a request body.");
    }

    [Fact]
    public async Task RemoveTaskFromList_SendsDelete_ToListTaskUrl_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.RemoveTaskFromListAsync("t1", "l9");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/list/l9/task/t1", handler.RequestUri);
        Assert.True(string.IsNullOrEmpty(handler.RequestBody), "remove-from-list must not send a request body.");
    }

    [Fact]
    public async Task AddTaskToList_ClickAppDisabled_ThrowsTypedClickUpApiException()
    {
        // The "Tasks in Multiple Lists" ClickApp being disabled fails the add with a non-2xx (400 here);
        // the facade's Guard must translate the Kiota ApiException into our typed exception carrying the
        // status, so call sites can flash it rather than crash.
        var handler = new CapturingHandler("""{ "err": "Multiple List feature is not enabled", "ECODE": "OAUTH_058" }""", HttpStatusCode.BadRequest);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.AddTaskToListAsync("t1", "l9"));

        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task RemoveTaskFromList_ApiError_ThrowsTypedClickUpApiException()
    {
        var handler = new CapturingHandler("""{ "err": "not found" }""", HttpStatusCode.NotFound);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.RemoveTaskFromListAsync("t1", "l9"));

        Assert.Equal(404, ex.StatusCode);
    }

    /// <summary>Records the outgoing request (method, URI, raw body) and returns a canned body/status.</summary>
    private sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
