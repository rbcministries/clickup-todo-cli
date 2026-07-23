using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for <see cref="ClickUpClient.DeleteTaskAsync"/>. They drive the real generated client
/// through a capturing <see cref="HttpMessageHandler"/> (no token, no network), asserting the outgoing
/// <c>DELETE /task/{task_id}</c> method + URL (a body-less, path-only mutation) and that a non-2xx
/// response surfaces as a caught <see cref="ClickUpApiException"/> rather than a raw Kiota exception,
/// mirroring <see cref="ClickUpClientListMembershipTests"/>.
/// </summary>
public sealed class ClickUpClientDeleteTaskTests
{
    [Fact]
    public async Task DeleteTask_SendsDelete_ToTaskEndpoint_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.DeleteTaskAsync("task1");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/task/task1", handler.RequestUri);
        Assert.True(handler.BodyWasEmpty, "delete-task is a path-only mutation and must send no request body.");
    }

    [Fact]
    public async Task DeleteTask_ApiError_SurfacesAsClickUpApiException()
    {
        var handler = new CapturingHandler("""{ "err": "not found", "ECODE": "ITEM_100" }""", HttpStatusCode.NotFound);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() => client.DeleteTaskAsync("task1"));

        Assert.Equal(404, ex.StatusCode);
        Assert.False(ex.IsAuthFailure);
    }

    /// <summary>Records the outgoing request (method, URI, whether a body was present) and returns a canned
    /// response with a configurable status code.</summary>
    private sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public bool BodyWasEmpty { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
