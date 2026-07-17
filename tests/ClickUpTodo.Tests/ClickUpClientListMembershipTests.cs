using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the task↔list membership writes on the <see cref="ClickUpClient"/> facade (#237) —
/// ClickUp's "Tasks in Multiple Lists" feature. They drive the real generated client through a capturing
/// <see cref="HttpMessageHandler"/> (no token, no network), asserting the outgoing
/// <c>POST</c>/<c>DELETE /list/{list_id}/task/{task_id}</c> method + URL (a body-less, path-only mutation),
/// and that a non-2xx "feature disabled" response surfaces as a caught <see cref="ClickUpApiException"/>
/// rather than a raw Kiota exception, mirroring <see cref="ClickUpClientCreateTaskTests"/>.
/// </summary>
public sealed class ClickUpClientListMembershipTests
{
    [Fact]
    public async Task AddTaskToList_SendsPost_ToListTaskEndpoint_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.AddTaskToListAsync("task1", "list1");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/list/list1/task/task1", handler.RequestUri);
        Assert.True(handler.BodyWasEmpty, "add-to-list is a path-only mutation and must send no request body.");
    }

    [Fact]
    public async Task RemoveTaskFromList_SendsDelete_ToListTaskEndpoint_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.RemoveTaskFromListAsync("task1", "list1");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/list/list1/task/task1", handler.RequestUri);
        Assert.True(handler.BodyWasEmpty, "remove-from-list is a path-only mutation and must send no request body.");
    }

    [Fact]
    public async Task AddTaskToList_UsesTaskAndListInTheRightPositions()
    {
        // The domain signature is (taskId, listId) but ClickUp's URL nests list-then-task — guard the
        // mapping so a future refactor can't silently swap them.
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.AddTaskToListAsync("theTask", "theList");

        Assert.Contains("/v2/list/theList/task/theTask", handler.RequestUri);
    }

    [Fact]
    public async Task AddTaskToList_FeatureDisabled_SurfacesAsClickUpApiException()
    {
        // ClickUp returns HTTP 400 (error OV_016) when the "Tasks in Multiple Lists" ClickApp is off.
        // The facade's Guard must translate the Kiota ApiException into our domain type so a caller can
        // catch and flash it — it must not escape as a raw transport exception.
        var handler = new CapturingHandler(
            """{ "err": "Tasks in Multiple Lists is not enabled", "ECODE": "OV_016" }""",
            HttpStatusCode.BadRequest);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() =>
            client.AddTaskToListAsync("task1", "list1"));

        Assert.Equal(400, ex.StatusCode);
        Assert.False(ex.IsAuthFailure);
    }

    [Fact]
    public async Task RemoveTaskFromList_ApiError_SurfacesAsClickUpApiException()
    {
        var handler = new CapturingHandler("""{ "err": "not found", "ECODE": "ITEM_100" }""", HttpStatusCode.NotFound);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() =>
            client.RemoveTaskFromListAsync("task1", "list1"));

        Assert.Equal(404, ex.StatusCode);
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
