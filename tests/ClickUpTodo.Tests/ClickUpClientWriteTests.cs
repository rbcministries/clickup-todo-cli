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
        Assert.False(handler.Body.RootElement.TryGetProperty("assignees", out _), "a priority write must not touch assignees.");
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
        Assert.False(handler.Body.RootElement.TryGetProperty("priority", out _), "an assignee write must not touch priority.");
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
        Assert.False(handler.Body.RootElement.TryGetProperty("priority", out _), "an assignee write must not touch priority.");
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

    [Fact]
    public async Task SetTaskDescription_SendsStringDescription_AndReturnsServerConfirmedText()
    {
        // Distinct request text vs response text_content proves the return is read back from the
        // response, not an echo of the argument — and that text_content is preferred over description.
        var handler = new CapturingHandler(
            """{ "id": "t1", "text_content": "server plain text", "description": "server raw" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var confirmed = await client.SetTaskDescriptionAsync("t1", "new description");

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/task/t1", handler.RequestUri);
        var description = handler.Body!.RootElement.GetProperty("description");
        Assert.Equal(JsonValueKind.String, description.ValueKind);
        Assert.Equal("new description", description.GetString());
        Assert.False(handler.Body.RootElement.TryGetProperty("status", out _), "a description write must not touch status.");
        Assert.False(handler.Body.RootElement.TryGetProperty("priority", out _), "a description write must not touch priority.");
        Assert.False(handler.Body.RootElement.TryGetProperty("assignees", out _), "a description write must not touch assignees.");
        Assert.False(handler.Body.RootElement.TryGetProperty("markdown_description", out _), "we write the plain 'description' field, never markdown_description.");
        Assert.False(handler.Body.RootElement.TryGetProperty("text_content", out _), "text_content is a read-only response field and must not be echoed into the write body.");
        Assert.Equal("server plain text", confirmed);
    }

    [Fact]
    public async Task SetTaskDescription_FallsBackToDescription_WhenNoTextContent()
    {
        var handler = new CapturingHandler("""{ "id": "t1", "description": "server raw only" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var confirmed = await client.SetTaskDescriptionAsync("t1", "x");

        Assert.Equal("server raw only", confirmed);
    }

    [Fact]
    public async Task SetTaskDescription_Empty_SendsExplicitEmptyString_ToClear()
    {
        // Clearing a description means sending "description": "" — a *missing* key would leave it
        // untouched. Kiota writes a non-null string, so the empty string round-trips as a real clear.
        var handler = new CapturingHandler("""{ "id": "t1", "text_content": "" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var confirmed = await client.SetTaskDescriptionAsync("t1", "");

        var description = handler.Body!.RootElement.GetProperty("description");
        Assert.Equal(JsonValueKind.String, description.ValueKind);
        Assert.Equal("", description.GetString());
        Assert.Null(confirmed); // whitespace/empty text_content and no description → null
    }

    [Fact]
    public async Task SetTaskDescription_NullArgument_Throws_BeforeAnyRequest()
    {
        var handler = new CapturingHandler("""{ "id": "t1" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SetTaskDescriptionAsync("t1", null!));

        Assert.Null(handler.Method); // never hit the transport
    }

    [Fact]
    public async Task SetTaskName_SendsStringName_AndReturnsServerConfirmedName()
    {
        // Distinct request name vs response name proves the return is read back from the response,
        // not an echo of the argument.
        var handler = new CapturingHandler("""{ "id": "t1", "name": "server confirmed name" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var confirmed = await client.SetTaskNameAsync("t1", "new name");

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/task/t1", handler.RequestUri);
        var name = handler.Body!.RootElement.GetProperty("name");
        Assert.Equal(JsonValueKind.String, name.ValueKind);
        Assert.Equal("new name", name.GetString());
        Assert.False(handler.Body.RootElement.TryGetProperty("status", out _), "a name write must not touch status.");
        Assert.False(handler.Body.RootElement.TryGetProperty("priority", out _), "a name write must not touch priority.");
        Assert.False(handler.Body.RootElement.TryGetProperty("description", out _), "a name write must not touch description.");
        Assert.False(handler.Body.RootElement.TryGetProperty("assignees", out _), "a name write must not touch assignees.");
        Assert.Equal("server confirmed name", confirmed);
    }

    [Fact]
    public async Task SetTaskName_NullArgument_Throws_BeforeAnyRequest()
    {
        var handler = new CapturingHandler("""{ "id": "t1" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SetTaskNameAsync("t1", null!));

        Assert.Null(handler.Method); // never hit the transport
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetTaskName_BlankArgument_Throws_BeforeAnyRequest(string blank)
    {
        // ClickUp has no empty-title concept, so a blank name fails fast client-side rather than
        // issuing a PUT the server would reject.
        var handler = new CapturingHandler("""{ "id": "t1" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetTaskNameAsync("t1", blank));

        Assert.Null(handler.Method); // never hit the transport
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
