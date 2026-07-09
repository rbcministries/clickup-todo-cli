using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the priority + assignee write methods on the <see cref="ClickUpClient"/> facade
/// (#154). They drive the real generated client through a capturing <see cref="HttpMessageHandler"/>
/// (no token, no network), asserting both the outgoing <c>PUT /task/{id}</c> body shape and that the
/// facade returns the server-confirmed value parsed from the response — mirroring
/// <c>SetTaskStatusAsync</c>'s return-the-truth contract.
/// </summary>
public sealed class ClickUpClientWriteTests
{
    [Fact]
    public async Task SetTaskPriority_SendsIntegerLevel_AndReturnsServerConfirmedLevel()
    {
        // Distinct request level (2) vs response level (1) proves the return comes from the response,
        // not an echo of the argument.
        var handler = new CapturingHandler("""{ "id": "t1", "priority": { "id": "1", "priority": "urgent" } }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var confirmed = await client.SetTaskPriorityAsync("t1", 2);

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/task/t1", handler.RequestUri);
        Assert.Equal(JsonValueKind.Number, handler.Body!.RootElement.GetProperty("priority").ValueKind);
        Assert.Equal(2, handler.Body.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal(1, confirmed);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    public async Task SetTaskPriority_ReadsBackEachLevelFromResponse(string responseId, int expected)
    {
        var handler = new CapturingHandler($$"""{ "priority": { "id": "{{responseId}}" } }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var confirmed = await client.SetTaskPriorityAsync("t1", expected);

        Assert.Equal(expected, confirmed);
    }

    [Fact]
    public async Task SetTaskPriority_Clear_SendsExplicitJsonNull_AndReturnsNull()
    {
        // ClickUp clears a priority when the body carries an explicit `"priority": null`; a *missing*
        // key would leave it untouched. The response with no priority maps back to null.
        var handler = new CapturingHandler("""{ "id": "t1", "priority": null }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var confirmed = await client.SetTaskPriorityAsync("t1", null);

        var priority = handler.Body!.RootElement.GetProperty("priority");
        Assert.Equal(JsonValueKind.Null, priority.ValueKind);
        Assert.Null(confirmed);
    }

    [Fact]
    public async Task AddTaskAssignee_SendsAddOnly_AndReturnsReconciledSet()
    {
        var handler = new CapturingHandler(
            """{ "assignees": [ { "id": 123, "username": "alice" }, { "id": 456, "username": "bob" } ] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var assignees = await client.AddTaskAssigneeAsync("t1", 123);

        Assert.Equal(HttpMethod.Put, handler.Method);
        var body = handler.Body!.RootElement.GetProperty("assignees");
        Assert.Equal([123L], body.GetProperty("add").EnumerateArray().Select(e => e.GetInt64()));
        Assert.False(body.TryGetProperty("rem", out _), "add-only write must not send a 'rem' array.");
        Assert.Equal([(123L, "alice"), (456L, "bob")], assignees.Select(a => (a.Id, a.Name)));
    }

    [Fact]
    public async Task RemoveTaskAssignee_SendsRemOnly_AndReturnsReconciledSet()
    {
        var handler = new CapturingHandler("""{ "assignees": [ { "id": 123, "username": "alice" } ] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var assignees = await client.RemoveTaskAssigneeAsync("t1", 456);

        var body = handler.Body!.RootElement.GetProperty("assignees");
        Assert.Equal([456L], body.GetProperty("rem").EnumerateArray().Select(e => e.GetInt64()));
        Assert.False(body.TryGetProperty("add", out _), "remove-only write must not send an 'add' array.");
        Assert.Equal([(123L, "alice")], assignees.Select(a => (a.Id, a.Name)));
    }

    [Fact]
    public async Task RemoveTaskAssignee_EmptyResponse_ReturnsEmptySet()
    {
        var handler = new CapturingHandler("""{ "assignees": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var assignees = await client.RemoveTaskAssigneeAsync("t1", 123);

        Assert.Empty(assignees);
    }

    /// <summary>Records the outgoing request (method, URI, parsed JSON body) and returns a canned body.</summary>
    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public JsonDocument? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            if (request.Content is not null)
                Body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
