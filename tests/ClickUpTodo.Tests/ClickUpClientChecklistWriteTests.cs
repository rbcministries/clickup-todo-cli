using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the checklist-item toggle write on the <see cref="ClickUpClient"/> facade (D, #457).
/// They drive the real generated client through a capturing <see cref="HttpMessageHandler"/> (no token, no
/// network), asserting the outgoing <c>PUT /v2/checklist/{id}/checklist_item/{id}</c> URL + <c>{ resolved }</c>
/// body shape, and that the facade maps the wrapped <c>{ "checklist": { … } }</c> response back to a domain
/// <see cref="TaskChecklist"/> (items via <see cref="ChecklistReader"/>) — mirroring
/// <see cref="ClickUpClientCreateTaskTests"/>.
/// </summary>
public sealed class ClickUpClientChecklistWriteTests
{
    private const string ChecklistResponse =
        """
        {
          "checklist": {
            "id": "c1",
            "name": "Release steps",
            "orderindex": 0,
            "resolved": 2,
            "unresolved": 1,
            "items": [
              { "id": "i1", "name": "Cut the tag", "resolved": true, "orderindex": 0,
                "children": [ { "id": "i1a", "name": "Sign it", "resolved": true, "orderindex": 0 } ] },
              { "id": "i2", "name": "Draft notes", "resolved": false, "orderindex": 1 }
            ]
          }
        }
        """;

    [Fact]
    public async Task SetChecklistItemResolved_SendsPutWithResolvedTrue_AndMapsResponse()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var updated = await client.SetChecklistItemResolvedAsync("t1", "c1", "i2", resolved: true);

        // Request: PUT to the checklist-item endpoint with a { resolved: true } body.
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/checklist/c1/checklist_item/i2", handler.RequestUri);
        var body = handler.Body!.RootElement;
        Assert.Equal(JsonValueKind.True, body.GetProperty("resolved").ValueKind);
        Assert.True(body.GetProperty("resolved").GetBoolean());

        // Response mapped back to the domain TaskChecklist (container fields + items via ChecklistReader).
        Assert.Equal("c1", updated.Id);
        Assert.Equal("Release steps", updated.Name);
        Assert.Equal(2, updated.Items.Count);           // two top-level items
        var i1 = updated.Items[0];
        Assert.Equal("i1", i1.Id);
        Assert.True(i1.Resolved);
        var child = Assert.Single(i1.Children);          // nested child round-trips through the reader
        Assert.Equal("i1a", child.Id);
        Assert.True(child.Resolved);
        Assert.Equal("i2", updated.Items[1].Id);
        Assert.False(updated.Items[1].Resolved);
    }

    [Fact]
    public async Task SetChecklistItemResolved_SendsResolvedFalse()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.SetChecklistItemResolvedAsync("t9", "c9", "i9", resolved: false);

        Assert.Contains("/v2/checklist/c9/checklist_item/i9", handler.RequestUri);
        Assert.Equal(JsonValueKind.False, handler.Body!.RootElement.GetProperty("resolved").ValueKind);
    }

    [Fact]
    public async Task CreateChecklistItem_SendsPostWithName_AndMapsResponse()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var updated = await client.CreateChecklistItemAsync("c1", "Draft notes");

        // Request: POST to the checklist's item collection with a { name } body (no item id in the path).
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/checklist/c1/checklist_item", handler.RequestUri);
        Assert.DoesNotContain("/checklist_item/", handler.RequestUri); // collection endpoint, not an item
        Assert.Equal("Draft notes", handler.Body!.RootElement.GetProperty("name").GetString());

        // Response mapped back to the domain TaskChecklist (the reconciled group ClickUp echoes).
        Assert.Equal("c1", updated.Id);
        Assert.Equal(2, updated.Items.Count);
    }

    [Fact]
    public async Task RenameChecklistItem_SendsPutWithName_AndMapsResponse()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var updated = await client.RenameChecklistItemAsync("c1", "i2", "Draft the notes");

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/checklist/c1/checklist_item/i2", handler.RequestUri);
        Assert.Equal("Draft the notes", handler.Body!.RootElement.GetProperty("name").GetString());
        // A rename sends name only — no resolved flag in the body.
        Assert.False(handler.Body.RootElement.TryGetProperty("resolved", out _));
        Assert.Equal("Release steps", updated.Name);
    }

    [Fact]
    public async Task SetChecklistItemAssignee_SendsPutWithAssigneeId_AndMapsResponse()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var updated = await client.SetChecklistItemAssigneeAsync("t1", "c1", "i2", assigneeId: 42);

        // Request: PUT to the checklist-item endpoint with an { assignee: 42 } body — a JSON number, and
        // no name/resolved (only the changed field is sent, like the rename write).
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/checklist/c1/checklist_item/i2", handler.RequestUri);
        var body = handler.Body!.RootElement;
        Assert.Equal(JsonValueKind.Number, body.GetProperty("assignee").ValueKind);
        Assert.Equal(42, body.GetProperty("assignee").GetInt64());
        Assert.False(body.TryGetProperty("name", out _));
        Assert.False(body.TryGetProperty("resolved", out _));

        // Response mapped back to the domain TaskChecklist (container fields + items via ChecklistReader).
        Assert.Equal("c1", updated.Id);
        Assert.Equal(2, updated.Items.Count);
    }

    [Fact]
    public async Task SetChecklistItemAssignee_ClearSendsExplicitJsonNull()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        // A null assigneeId clears the assignee — ClickUp reads an explicit `"assignee": null` as "clear",
        // so the body must carry the key with a JSON null (not omit it, which would leave it untouched).
        await client.SetChecklistItemAssigneeAsync("t1", "c1", "i2", assigneeId: null);

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/checklist/c1/checklist_item/i2", handler.RequestUri);
        var body = handler.Body!.RootElement;
        Assert.True(body.TryGetProperty("assignee", out var assignee), "clear must send the assignee key");
        Assert.Equal(JsonValueKind.Null, assignee.ValueKind);
    }

    [Fact]
    public async Task DeleteChecklistItem_SendsDeleteToItemEndpoint_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.DeleteChecklistItemAsync("c1", "i2");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/checklist/c1/checklist_item/i2", handler.RequestUri);
        Assert.Null(handler.Body); // DELETE carries no request body
    }

    // ── Reorder / reparent (G, #569) ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveChecklistItem_UpDown_SendsOrderIndexOnly_NoParent()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var updated = await client.MoveChecklistItemAsync("t1", "c1", "i2", parentId: null, orderIndex: 0.5, clearParent: false);

        // A plain up/down move sends the new orderindex and leaves parent untouched (no key at all).
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/checklist/c1/checklist_item/i2", handler.RequestUri);
        var body = handler.Body!.RootElement;
        Assert.Equal(JsonValueKind.Number, body.GetProperty("orderindex").ValueKind);
        Assert.Equal(0.5, body.GetProperty("orderindex").GetDouble());
        Assert.False(body.TryGetProperty("parent", out _)); // parent unchanged ⇒ not sent
        // Response mapped back to the domain TaskChecklist.
        Assert.Equal("c1", updated.Id);
    }

    [Fact]
    public async Task MoveChecklistItem_Indent_SendsParentId_AndOrderIndex()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.MoveChecklistItemAsync("t1", "c1", "i2", parentId: "i1", orderIndex: 3, clearParent: false);

        var body = handler.Body!.RootElement;
        Assert.Equal("i1", body.GetProperty("parent").GetString());
        Assert.Equal(3, body.GetProperty("orderindex").GetDouble());
    }

    [Fact]
    public async Task MoveChecklistItem_OutdentToRoot_SendsExplicitNullParent()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.MoveChecklistItemAsync("t1", "c1", "i1a", parentId: null, orderIndex: 2, clearParent: true);

        // Outdent to top level forces an explicit "parent": null (ClickUp reads it as "no parent"),
        // mirroring the priority-clear pattern — not an omitted field.
        var body = handler.Body!.RootElement;
        Assert.True(body.TryGetProperty("parent", out var parent));
        Assert.Equal(JsonValueKind.Null, parent.ValueKind);
        Assert.Equal(2, body.GetProperty("orderindex").GetDouble());
    }

    // ── Group CRUD (F, #459) ───────────────────────────────────────────────────────────────────────

    private const string NewChecklistResponse =
        """
        {
          "checklist": {
            "id": "c2",
            "name": "QA signoff",
            "orderindex": 1,
            "resolved": 0,
            "unresolved": 0,
            "items": []
          }
        }
        """;

    [Fact]
    public async Task CreateChecklist_SendsPostToTaskChecklist_WithName_AndMapsResponse()
    {
        var handler = new CapturingHandler(NewChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateChecklistAsync("t1", "QA signoff");

        // Request: POST to the task's checklist collection with a { name } body (no checklist id in the path).
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/task/t1/checklist", handler.RequestUri);
        Assert.DoesNotContain("checklist_item", handler.RequestUri);
        Assert.Equal("QA signoff", handler.Body!.RootElement.GetProperty("name").GetString());

        // Response mapped back to the domain TaskChecklist (a new empty group).
        Assert.Equal("c2", created.Id);
        Assert.Equal("QA signoff", created.Name);
        Assert.Empty(created.Items);
    }

    [Fact]
    public async Task RenameChecklist_SendsPutToChecklist_WithName_AndMapsResponse()
    {
        var handler = new CapturingHandler(ChecklistResponse);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var renamed = await client.RenameChecklistAsync("c1", "Release checklist");

        // Request: PUT to the checklist endpoint (no item segment) with a { name } body.
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Contains("/v2/checklist/c1", handler.RequestUri);
        Assert.DoesNotContain("checklist_item", handler.RequestUri);
        Assert.Equal("Release checklist", handler.Body!.RootElement.GetProperty("name").GetString());
        Assert.Equal("Release steps", renamed.Name); // server-confirmed name from the echoed checklist
    }

    [Fact]
    public async Task DeleteChecklist_SendsDeleteToChecklistEndpoint_WithNoBody()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.DeleteChecklistAsync("c1");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("/v2/checklist/c1", handler.RequestUri);
        Assert.DoesNotContain("checklist_item", handler.RequestUri); // the group endpoint, not an item
        Assert.Null(handler.Body); // DELETE carries no request body
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
