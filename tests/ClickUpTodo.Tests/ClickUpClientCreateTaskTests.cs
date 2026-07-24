using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the create-task write method on the <see cref="ClickUpClient"/> facade (#209).
/// They drive the real generated client through a capturing <see cref="HttpMessageHandler"/> (no token,
/// no network), asserting the outgoing <c>POST /list/{id}/task</c> body shape (full-field, optional-field
/// omission, and that <c>assignees</c> is a flat id array — unlike the add/rem of an update) and that the
/// facade returns the created task mapped from the response, mirroring <see cref="ClickUpClientWriteTests"/>.
/// </summary>
public sealed class ClickUpClientCreateTaskTests
{
    [Fact]
    public async Task CreateTask_SendsAllFields_AndMapsResponseToDomain()
    {
        // Response priority (id "2" → High) is deliberately different from the requested priority (3)
        // so the mapped result is proven to come from the response, not an echo of the argument.
        var handler = new CapturingHandler(
            """
            {
              "id": "abc123",
              "name": "Write the thing",
              "url": "https://app.clickup.com/t/abc123",
              "status": { "status": "open", "color": "#d3d3d3", "type": "open" },
              "priority": { "id": "2", "priority": "high", "color": "#ffcc00" },
              "list": { "id": "list1", "name": "Personal" },
              "assignees": [ { "id": 183, "username": "alice" } ]
            }
            """);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskAsync("list1", new NewTaskRequest
        {
            Name = "Write the thing",
            Description = "Details here",
            Assignees = [183, 456],
            PriorityLevel = 3,
            DueDateMs = 1_700_000_000_000,
        });

        // Request: POST to the list-task endpoint, full body shape.
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/list/list1/task", handler.RequestUri);
        var body = handler.Body!.RootElement;
        Assert.Equal("Write the thing", body.GetProperty("name").GetString());
        Assert.Equal("Details here", body.GetProperty("description").GetString());
        Assert.Equal(3, body.GetProperty("priority").GetInt32());
        Assert.Equal(JsonValueKind.Number, body.GetProperty("priority").ValueKind);
        Assert.Equal(1_700_000_000_000, body.GetProperty("due_date").GetInt64());

        // assignees is a FLAT array of ids (create shape), not the { add, rem } object of an update.
        var assignees = body.GetProperty("assignees");
        Assert.Equal(JsonValueKind.Array, assignees.ValueKind);
        Assert.Equal([183L, 456L], assignees.EnumerateArray().Select(e => e.GetInt64()));

        // Response mapped back to the domain TaskItem the New Task screen will insert.
        Assert.Equal("abc123", created.Id);
        Assert.Equal("Write the thing", created.Name);
        Assert.Equal("https://app.clickup.com/t/abc123", created.Url);
        Assert.Equal("open", created.StatusName);
        Assert.Equal("list1", created.ListId);
        Assert.Equal(2, created.PriorityLevel);
        Assert.Equal("High", created.PriorityName);
        Assert.Equal([(183L, "alice")], created.Assignees.Select(a => (a.Id, a.Name)));
    }

    [Fact]
    public async Task CreateTask_OmitsUnsetOptionalFields_SendingOnlyName()
    {
        // Kiota drops null typed properties and a null collection, so an unset description/priority/
        // due-date and an empty assignee set must send no key — ClickUp then applies its list defaults.
        var handler = new CapturingHandler("""{ "id": "t1", "name": "Just a name" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskAsync("list1", new NewTaskRequest { Name = "Just a name" });

        var body = handler.Body!.RootElement;
        Assert.Equal("Just a name", body.GetProperty("name").GetString());
        Assert.False(body.TryGetProperty("description", out _), "unset description must send no key.");
        Assert.False(body.TryGetProperty("assignees", out _), "empty assignees must send no key.");
        Assert.False(body.TryGetProperty("priority", out _), "unset priority must send no key.");
        Assert.False(body.TryGetProperty("due_date", out _), "unset due date must send no key.");
        Assert.Equal("t1", created.Id);
    }

    [Fact]
    public async Task CreateTask_EmptyDescription_TreatedAsUnset()
    {
        // A blank (but non-null) description is normalized to omitted rather than sending "".
        var handler = new CapturingHandler("""{ "id": "t1", "name": "N" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.CreateTaskAsync("list1", new NewTaskRequest { Name = "N", Description = "" });

        Assert.False(handler.Body!.RootElement.TryGetProperty("description", out _),
            "an empty description must send no key.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreateTask_BlankName_Throws_WithoutHittingTheNetwork(string? name)
    {
        var handler = new CapturingHandler("""{ "id": "t1" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateTaskAsync("list1", new NewTaskRequest { Name = name! }));

        Assert.Null(handler.Method); // never reached the transport
    }

    [Fact]
    public async Task CreateTask_SendsCustomFields_AsIdValueArray_WithTypedValues()
    {
        // Values arrive pre-shaped from the pure CustomFieldValueSerializer as neutral JsonElements; the
        // facade must render them as ClickUp's custom_fields: [{ id, value }] with the JSON kind preserved
        // (string, number, bool, array) through the real generated client + Kiota serializer.
        var handler = new CapturingHandler("""{ "id": "t1", "name": "N" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.CreateTaskAsync("list1", new NewTaskRequest
        {
            Name = "N",
            CustomFields =
            [
                new CustomFieldValue("cf_text", JsonSerializer.SerializeToElement("hello")),
                new CustomFieldValue("cf_num", JsonSerializer.SerializeToElement(42L)),
                new CustomFieldValue("cf_bool", JsonSerializer.SerializeToElement(true)),
                new CustomFieldValue("cf_labels", JsonSerializer.SerializeToElement(new[] { "l1", "l2" })),
            ],
        });

        var custom = handler.Body!.RootElement.GetProperty("custom_fields");
        Assert.Equal(JsonValueKind.Array, custom.ValueKind);
        Assert.Equal(4, custom.GetArrayLength());

        Assert.Equal("cf_text", custom[0].GetProperty("id").GetString());
        Assert.Equal("hello", custom[0].GetProperty("value").GetString());

        Assert.Equal("cf_num", custom[1].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Number, custom[1].GetProperty("value").ValueKind);
        Assert.Equal(42L, custom[1].GetProperty("value").GetInt64());

        Assert.Equal("cf_bool", custom[2].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.True, custom[2].GetProperty("value").ValueKind);

        Assert.Equal("cf_labels", custom[3].GetProperty("id").GetString());
        Assert.Equal(["l1", "l2"], custom[3].GetProperty("value").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task CreateTask_CustomFieldValues_PreserveDoubleAndNullKinds()
    {
        // Covers the ToUntyped double (non-integral number) and null branches end-to-end through the
        // real serializer, alongside the string/int/bool/array kinds above.
        var handler = new CapturingHandler("""{ "id": "t1", "name": "N" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.CreateTaskAsync("list1", new NewTaskRequest
        {
            Name = "N",
            CustomFields =
            [
                new CustomFieldValue("cf_money", JsonSerializer.SerializeToElement(19.99)),
                new CustomFieldValue("cf_null", JsonSerializer.SerializeToElement((string?)null)),
            ],
        });

        var custom = handler.Body!.RootElement.GetProperty("custom_fields");
        Assert.Equal(JsonValueKind.Number, custom[0].GetProperty("value").ValueKind);
        Assert.Equal(19.99, custom[0].GetProperty("value").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, custom[1].GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task CreateTask_EmptyCustomFields_SendsNoKey()
    {
        var handler = new CapturingHandler("""{ "id": "t1", "name": "N" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.CreateTaskAsync("list1", new NewTaskRequest { Name = "N" });

        Assert.False(handler.Body!.RootElement.TryGetProperty("custom_fields", out _),
            "no custom fields must send no custom_fields key.");
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
