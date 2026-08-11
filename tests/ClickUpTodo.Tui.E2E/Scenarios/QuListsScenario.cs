using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// #365 Quick Updates List-pane field-strand scenario (E2E_QU_LISTS=1). The task is pre-seeded into one
/// additional list ("list2" / Q3 Website Refresh) alongside its home list; that additional list defines a
/// <em>local</em> "Sprint Points" Custom Field the home list does not; and the task detail carries values for
/// both "Sprint Points" (list-local) and "Notes" (shared). Removing the additional list therefore strands the
/// Sprint Points value (arms a confirmation) but not Notes. Seeds the additional membership and overrides the
/// detail (adding the set custom-field values) and the per-list field definitions.
/// </summary>
internal sealed class QuListsScenario : IE2EScenario
{
    private const string SeededLocation = "list2"; // Q3 Website Refresh (Lists[1]/ListNames[1])

    // The task's SET Custom Field values: Sprint Points (only list2 defines it → strands on a list2 remove)
    // and Notes (the home list defines it too → never strands).
    private const string CustomFieldValuesJson =
        """[{"id":"cf_sprint","name":"Sprint Points","type":"number","value":8},{"id":"cf_notes","name":"Notes","type":"text","value":"triage"}]""";

    public string Name => "qu-lists";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_QU_LISTS") == "1";

    public void SeedBackend(FakeClickUp backend) => backend.Locations.Add(SeededLocation);

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        // Detail: default detail + the task's set custom-field values, so the List-pane remove preflight has
        // values to strand-check.
        new(HttpMethod.Get, "task/{id}", (_, path, query, _) =>
        {
            var id = FakeClickUp.LastSegment(path);
            if (FakeClickUp.TaskGetSentinel(id, query) is { } notFound)
                return Task.FromResult(notFound);
            var node = JsonNode.Parse(backend.DetailJson(id))!;
            node["custom_fields"] = JsonNode.Parse(CustomFieldValuesJson);
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),

        // Per-list Custom Field DEFINITIONS keyed by list id: the additional "list2" defines the shared
        // "Notes" plus a local "Sprint Points"; every other list (incl. the home "plist") defines only the
        // shared "Notes". So a Sprint Points value strands on a list2 remove while a Notes value never does.
        new(HttpMethod.Get, "list/{id}/field", (_, p, _, _) =>
            FakeClickUp.OkAsync(FieldsJson(FakeClickUp.ListIdOfField(p))), 1),
    ];

    private static string FieldsJson(string listId) => listId == "list2"
        ? """{"fields":[{"id":"cf_notes","name":"Notes","type":"text","required":false},{"id":"cf_sprint","name":"Sprint Points","type":"number","required":false}]}"""
        : """{"fields":[{"id":"cf_notes","name":"Notes","type":"text","required":false}]}""";
}
