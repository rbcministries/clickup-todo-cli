using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the custom-id task lookup on the <see cref="ClickUpClient"/> facade (#303, Ctrl+O
/// quick-open). They drive the real generated client through a capturing <see cref="HttpMessageHandler"/>
/// (no token, no network), asserting the outgoing <c>GET /task/{id}?custom_task_ids=true&amp;team_id=…</c>
/// query shape and that the mapped <see cref="TaskDetail.Id"/> is the task's <b>plain</b> id (so the host
/// can then open it through the ordinary detail path). Mirrors <see cref="ClickUpClientListMembershipTests"/>.
/// </summary>
public sealed class ClickUpClientCustomIdLookupTests
{
    [Fact]
    public async Task GetByCustomId_SendsCustomTaskIdsAndTeamId_AndMapsToPlainId()
    {
        // The custom id in the URL is "ABC-123", but the response carries the task's real id "86plain".
        var handler = new CapturingHandler(
            """
            {
              "id": "86plain",
              "custom_id": "ABC-123",
              "name": "A task with a custom id",
              "url": "https://app.clickup.com/t/86plain",
              "status": { "status": "open", "color": "#d3d3d3", "type": "open" }
            }
            """);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var detail = await client.GetTaskDetailByCustomIdAsync("ABC-123", "team9");

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Contains("/v2/task/ABC-123", handler.RequestUri);
        Assert.Contains("custom_task_ids=true", handler.RequestUri);
        Assert.Contains("team_id=team9", handler.RequestUri);

        // Mapped from the response: the real plain id, not the custom id we queried with.
        Assert.Equal("86plain", detail.Id);
        Assert.Equal("ABC-123", detail.CustomId);
        Assert.Equal("A task with a custom id", detail.Name);
    }

    [Fact]
    public async Task GetByCustomId_NotFound_SurfacesAsClickUpApiException()
    {
        var handler = new CapturingHandler(
            """{ "err": "Task not found", "ECODE": "ITEM_100" }""", HttpStatusCode.NotFound);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ClickUpApiException>(() =>
            client.GetTaskDetailByCustomIdAsync("NOPE-1", "team9"));

        Assert.Equal(404, ex.StatusCode);
        Assert.False(ex.IsAuthFailure);
    }

    /// <summary>Records the outgoing request (method, full URI incl. query) and returns a canned body.</summary>
    private sealed class CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
