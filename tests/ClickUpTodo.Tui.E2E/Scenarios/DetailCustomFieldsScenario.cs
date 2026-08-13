using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// Seeds the opened task's detail read with a <c>custom_fields</c> array so the Task Detail <b>Other</b>
/// tab's navigable custom-field row model (#587 §2) has real field rows to render and move a selection
/// over. Gated on <c>E2E_TASK_CUSTOM_FIELDS=1</c>; off ⇒ the default detail carries no <c>custom_fields</c>
/// (the <c>(none)</c> empty state), so no other check's Other tab changes. Read-only — the §3 per-field
/// write path is a separate slice, so this scenario models no <c>POST/DELETE /task/{id}/field/{fid}</c>.
/// </summary>
internal sealed class DetailCustomFieldsScenario : IE2EScenario
{
    // A small mixed set: fillable checkbox / short_text / drop_down rows plus a non-fillable computed
    // formula, so the Other tab shows both selectable field rows and inert scenery. Field order is
    // preserved as returned (the arranger keeps it), so the rows render top-to-bottom in this order.
    private const string Fields = """
    [
      { "id": "cf1", "name": "Reviewed", "type": "checkbox", "value": "false" },
      { "id": "cf2", "name": "Ticket ref", "type": "short_text", "value": "OPS-4271" },
      { "id": "cf3", "name": "Severity", "type": "drop_down", "value": 1,
        "type_config": { "options": [ { "id": "o0", "name": "Low", "orderindex": 0 },
                                      { "id": "o1", "name": "High", "orderindex": 1 } ] } },
      { "id": "cf4", "name": "Computed total", "type": "formula", "value": "128" }
    ]
    """;

    public string Name => "detail-custom-fields";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_TASK_CUSTOM_FIELDS") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        // Detail read: the default detail with the seeded custom_fields spliced in (mirrors how the
        // Checklists scenario injects its checklists DOM into the same GET).
        new(HttpMethod.Get, "task/{id}", (_, path, query, _) =>
        {
            var id = FakeClickUp.LastSegment(path);
            if (FakeClickUp.TaskGetSentinel(id, query) is { } notFound)
                return Task.FromResult(notFound);
            var node = JsonNode.Parse(backend.DetailJson(id))!;
            node["custom_fields"] = JsonNode.Parse(Fields);
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),
    ];
}
