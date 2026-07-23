using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the list custom-field <b>definitions</b> fetch on the <see cref="ClickUpClient"/>
/// facade (#249). They drive the real generated client through a capturing
/// <see cref="HttpMessageHandler"/> (no token, no network), asserting the outgoing
/// <c>GET /list/{id}/field</c> request and that the response maps onto the stable
/// <see cref="CustomFieldDefinition"/> list — mirroring <see cref="ClickUpClientCreateTaskTests"/>.
/// </summary>
public sealed class ClickUpClientListCustomFieldsFetchTests
{
    [Fact]
    public async Task GetListCustomFields_GetsFieldEndpoint_AndMapsDefinitions()
    {
        var handler = new CapturingHandler(
            """
            {
              "fields": [
                { "id": "f1", "name": "Stage", "type": "drop_down", "required": true,
                  "type_config": { "options": [
                    { "id": "o0", "name": "Backlog", "orderindex": 0 },
                    { "id": "o1", "name": "Done", "orderindex": 1 } ] } },
                { "id": "f2", "name": "Estimate", "type": "number", "required": false },
                { "id": "f3", "name": "Notes", "type": "text" }
              ]
            }
            """);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var fields = await client.GetListCustomFieldsAsync("list1");

        // Request: GET to the list field-definitions endpoint.
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Contains("/v2/list/list1/field", handler.RequestUri);

        // Response mapped onto the domain records.
        Assert.Equal(3, fields.Count);
        Assert.Equal(["f1", "f2", "f3"], fields.Select(f => f.Id));
        Assert.Equal(["drop_down", "number", "text"], fields.Select(f => f.Type));

        var stage = fields[0];
        Assert.True(stage.Required);
        Assert.Equal(["Backlog", "Done"], stage.Options.Select(o => o.Name));

        Assert.False(fields[1].Required);
        Assert.Empty(fields[1].Options);
        Assert.False(fields[2].Required); // required absent ⇒ false
    }

    [Fact]
    public async Task GetListCustomFields_DropsFieldsWithBlankId()
    {
        // A field with no usable id can't have a value written back to it, so it's dropped.
        var handler = new CapturingHandler(
            """
            { "fields": [
                { "id": "keep", "name": "Keep", "type": "text" },
                { "id": "", "name": "Blank id", "type": "text" },
                { "name": "No id key", "type": "text" }
            ] }
            """);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var fields = await client.GetListCustomFieldsAsync("list1");

        Assert.Equal(["keep"], fields.Select(f => f.Id));
    }

    [Fact]
    public async Task GetListCustomFields_EmptyResponse_YieldsEmptyList()
    {
        var handler = new CapturingHandler("""{ "fields": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        Assert.Empty(await client.GetListCustomFieldsAsync("list1"));
    }

    /// <summary>Records the outgoing request (method, URI) and returns a canned body.</summary>
    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
