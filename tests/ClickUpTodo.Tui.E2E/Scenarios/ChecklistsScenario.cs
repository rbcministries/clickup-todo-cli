using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// Task checklists in the Task Detail tab (#456–#459). Serves the opened task with a seeded, <em>mutable</em>
/// <c>checklists</c> DOM so the Checklists tab renders real groups/items and edits round-trip across a refresh:
/// <list type="bullet">
/// <item><b>E2E_CHECKLISTS=1</b> — seed two groups (a nested item, mixed resolved state, one assigned item).</item>
/// <item><b>E2E_CHECKLISTS_EMPTY=1</b> — serve the mutable DOM seeded <em>empty</em>, so the group-CRUD leg
/// (#459) starts on the empty-state row and create/delete round-trips back to it.</item>
/// </list>
/// Owns the DOM (mutated in place by the item toggle/rename/create/delete and group create/rename/delete
/// writes, guarded by its own gate) and injects it into the detail read. Off ⇒ the default empty
/// <c>checklists</c>, so no other check's detail changes.
/// </summary>
internal sealed class ChecklistsScenario : IE2EScenario
{
    private readonly object _gate = new();
    private readonly JsonArray _dom;
    private int _itemSeq;
    private int _groupSeq;

    public ChecklistsScenario()
        => _dom = Empty ? [] : (JsonArray)JsonNode.Parse(FakeClickUp.Fixture("checklists"))!;

    private static bool Empty => Environment.GetEnvironmentVariable("E2E_CHECKLISTS_EMPTY") == "1";

    public string Name => "checklists";
    public bool IsActive =>
        Environment.GetEnvironmentVariable("E2E_CHECKLISTS") == "1"
        || Environment.GetEnvironmentVariable("E2E_CHECKLISTS_EMPTY") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        // Detail read: the default detail with the current checklist DOM spliced in (from the mutable DOM, so
        // a toggle/create/delete persists across a later detail GET).
        new(HttpMethod.Get, "task/{id}", (_, path, _, _) =>
        {
            var node = JsonNode.Parse(backend.DetailJson(FakeClickUp.LastSegment(path)))!;
            lock (_gate)
                node["checklists"] = _dom.DeepClone();
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),

        // PUT /checklist/{id}/checklist_item/{id} (D #457 toggle-resolved, E #458 rename).
        new(HttpMethod.Put, "checklist/{checklistId}/checklist_item/{itemId}", ChecklistItemPut, 1),
        // POST /checklist/{id}/checklist_item (E #458 create); DELETE .../{item} (delete).
        new(HttpMethod.Post, "checklist/{checklistId}/checklist_item", CreateChecklistItem, 1),
        new(HttpMethod.Delete, "checklist/{checklistId}/checklist_item/{itemId}", DeleteChecklistItem, 1),
        // Group CRUD (F #459): POST /task/{id}/checklist create; PUT /checklist/{id} rename; DELETE delete.
        new(HttpMethod.Post, "task/{taskId}/checklist", CreateChecklist, 1),
        new(HttpMethod.Put, "checklist/{checklistId}", ChecklistPut, 1),
        new(HttpMethod.Delete, "checklist/{checklistId}", DeleteChecklist, 1),
    ];

    /// <summary>PUT: the toggle-resolved (D #457) and rename (E #458) write. Parses whichever of
    /// <c>{"resolved":bool}</c> / <c>{"name":string}</c> the body carries, mutates that item in the DOM, and
    /// echoes <c>{ "checklist": … }</c> exactly as ClickUp does.</summary>
    private async Task<HttpResponseMessage> ChecklistItemPut(HttpRequestMessage request, string path, string query, CancellationToken ct)
    {
        var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
        var (checklistId, itemId) = ChecklistItemIds(path);
        string body;
        lock (_gate)
        {
            foreach (var node in _dom)
            {
                if (node is JsonObject checklist && checklist["id"]?.GetValue<string>() == checklistId)
                {
                    if (TryParseResolved(reqBody, out var resolved))
                        SetItemResolved(checklist["items"] as JsonArray, itemId, resolved);
                    if (FakeClickUp.ParseName(reqBody) is { } name)
                        SetItemName(checklist["items"] as JsonArray, itemId, name);
                    RecomputeCounts(checklist);
                    break;
                }
            }
            body = Echo(checklistId);
        }
        return FakeClickUp.Ok(body);
    }

    /// <summary>POST /checklist/{id}/checklist_item (E #458): append a new item (server-assigned id,
    /// unresolved, ordered last) to the checklist's top level and echo the reconciled envelope.</summary>
    private async Task<HttpResponseMessage> CreateChecklistItem(HttpRequestMessage request, string path, string query, CancellationToken ct)
    {
        var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
        var name = FakeClickUp.ParseName(reqBody) ?? "";
        var checklistId = ChecklistIdFromCollectionPath(path);
        string body;
        lock (_gate)
        {
            foreach (var node in _dom)
            {
                if (node is JsonObject checklist && checklist["id"]?.GetValue<string>() == checklistId)
                {
                    var items = checklist["items"] as JsonArray ?? [];
                    checklist["items"] = items;
                    items.Add(new JsonObject
                    {
                        ["id"] = $"inew{++_itemSeq}",
                        ["name"] = name,
                        ["resolved"] = false,
                        ["orderindex"] = items.Count,
                    });
                    RecomputeCounts(checklist);
                    break;
                }
            }
            body = Echo(checklistId);
        }
        return FakeClickUp.Ok(body);
    }

    /// <summary>DELETE /checklist/{id}/checklist_item/{id} (E #458): remove the item (searching nested
    /// children), recompute counts, return an empty object.</summary>
    private async Task<HttpResponseMessage> DeleteChecklistItem(HttpRequestMessage request, string path, string query, CancellationToken ct)
    {
        _ = request;
        await Task.CompletedTask;
        var (checklistId, itemId) = ChecklistItemIds(path);
        lock (_gate)
        {
            foreach (var node in _dom)
            {
                if (node is JsonObject checklist && checklist["id"]?.GetValue<string>() == checklistId)
                {
                    RemoveItem(checklist["items"] as JsonArray, itemId);
                    RecomputeCounts(checklist);
                    break;
                }
            }
        }
        return FakeClickUp.Ok("{}");
    }

    /// <summary>POST /task/{id}/checklist (F #459): append a new (empty) group with a server-assigned id and
    /// echo the reconciled envelope.</summary>
    private async Task<HttpResponseMessage> CreateChecklist(HttpRequestMessage request, string path, string query, CancellationToken ct)
    {
        var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
        var name = FakeClickUp.ParseName(reqBody) ?? "";
        string body;
        lock (_gate)
        {
            var id = $"cnew{++_groupSeq}";
            _dom.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["orderindex"] = _dom.Count,
                ["resolved"] = 0,
                ["unresolved"] = 0,
                ["items"] = new JsonArray(),
            });
            body = Echo(id);
        }
        return FakeClickUp.Ok(body);
    }

    /// <summary>PUT /checklist/{id} (F #459): rename the group and echo the reconciled envelope.</summary>
    private async Task<HttpResponseMessage> ChecklistPut(HttpRequestMessage request, string path, string query, CancellationToken ct)
    {
        var reqBody = request.Content is { } content ? await content.ReadAsStringAsync(ct) : "";
        var checklistId = FakeClickUp.LastSegment(path);
        string body;
        lock (_gate)
        {
            foreach (var node in _dom)
                if (node is JsonObject checklist && checklist["id"]?.GetValue<string>() == checklistId)
                {
                    if (FakeClickUp.ParseName(reqBody) is { } name)
                        checklist["name"] = name;
                    break;
                }
            body = Echo(checklistId);
        }
        return FakeClickUp.Ok(body);
    }

    /// <summary>DELETE /checklist/{id} (F #459): remove the whole group (its items go with it), return {}.</summary>
    private async Task<HttpResponseMessage> DeleteChecklist(HttpRequestMessage request, string path, string query, CancellationToken ct)
    {
        _ = request;
        await Task.CompletedTask;
        var checklistId = FakeClickUp.LastSegment(path);
        lock (_gate)
        {
            for (var i = 0; i < _dom.Count; i++)
                if (_dom[i] is JsonObject checklist && checklist["id"]?.GetValue<string>() == checklistId)
                {
                    _dom.RemoveAt(i);
                    break;
                }
        }
        return FakeClickUp.Ok("{}");
    }

    /// <summary>Echoes the whole parent checklist from the DOM under a <c>checklist</c> key (ClickUp's shape)
    /// as a deep clone, so the returned node isn't parented in the DOM. Called under <c>_gate</c>.</summary>
    private string Echo(string checklistId)
    {
        JsonObject? target = null;
        foreach (var node in _dom)
            if (node is JsonObject checklist && checklist["id"]?.GetValue<string>() == checklistId)
            {
                target = checklist;
                break;
            }
        var echoed = target is null ? new JsonObject() : (JsonObject)target.DeepClone();
        return new JsonObject { ["checklist"] = echoed }.ToJsonString();
    }

    // ── Body / path parsing and DOM mutation helpers ────────────────────────────────────────────────────

    private static bool TryParseResolved(string requestBody, out bool resolved)
    {
        resolved = false;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (!doc.RootElement.TryGetProperty("resolved", out var r)
                || r.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            resolved = r.ValueKind == JsonValueKind.True;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The checklist id from a create path <c>/v2/checklist/{id}/checklist_item</c> (no item id).</summary>
    private static string ChecklistIdFromCollectionPath(string path)
    {
        const string listSeg = "/checklist/";
        const string itemSeg = "/checklist_item";
        var start = path.IndexOf(listSeg, StringComparison.Ordinal) + listSeg.Length;
        var end = path.IndexOf(itemSeg, StringComparison.Ordinal);
        return end > start ? path[start..end] : "";
    }

    /// <summary>Splits <c>/v2/checklist/{id}/checklist_item/{item_id}</c> into its two ids.</summary>
    private static (string ChecklistId, string ItemId) ChecklistItemIds(string path)
    {
        var itemId = FakeClickUp.LastSegment(path);
        const string listSeg = "/checklist/";
        const string itemSeg = "/checklist_item/";
        var start = path.IndexOf(listSeg, StringComparison.Ordinal) + listSeg.Length;
        var end = path.IndexOf(itemSeg, StringComparison.Ordinal);
        return (end > start ? path[start..end] : "", itemId);
    }

    private static bool SetItemResolved(JsonArray? items, string itemId, bool resolved)
    {
        if (items is null)
            return false;
        foreach (var node in items)
        {
            if (node is not JsonObject item)
                continue;
            if (item["id"]?.GetValue<string>() == itemId)
            {
                item["resolved"] = resolved;
                return true;
            }
            if (SetItemResolved(item["children"] as JsonArray, itemId, resolved))
                return true;
        }
        return false;
    }

    private static bool SetItemName(JsonArray? items, string itemId, string name)
    {
        if (items is null)
            return false;
        foreach (var node in items)
        {
            if (node is not JsonObject item)
                continue;
            if (item["id"]?.GetValue<string>() == itemId)
            {
                item["name"] = name;
                return true;
            }
            if (SetItemName(item["children"] as JsonArray, itemId, name))
                return true;
        }
        return false;
    }

    private static bool RemoveItem(JsonArray? items, string itemId)
    {
        if (items is null)
            return false;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject item)
                continue;
            if (item["id"]?.GetValue<string>() == itemId)
            {
                items.RemoveAt(i);
                return true;
            }
            if (RemoveItem(item["children"] as JsonArray, itemId))
                return true;
        }
        return false;
    }

    private static void RecomputeCounts(JsonObject checklist)
    {
        var (resolved, total) = CountItems(checklist["items"] as JsonArray);
        checklist["resolved"] = resolved;
        checklist["unresolved"] = total - resolved;
    }

    private static (int Resolved, int Total) CountItems(JsonArray? items)
    {
        var resolved = 0;
        var total = 0;
        if (items is not null)
            foreach (var node in items)
                if (node is JsonObject item)
                {
                    total++;
                    if (item["resolved"]?.GetValue<bool>() == true)
                        resolved++;
                    var (childResolved, childTotal) = CountItems(item["children"] as JsonArray);
                    resolved += childResolved;
                    total += childTotal;
                }
        return (resolved, total);
    }
}
