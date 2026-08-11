namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>
/// The Task Tree tab scenario (#291, E2E_TREE=1): a fixed, bounded ancestry/child tree hung off the opened
/// task <c>t0</c>. A plain GET returns each node's own <c>parent</c> (the tab's ancestry walk climbs it one
/// node at a time); an <c>?include_subtasks=true</c> GET appends that node's direct children (the descendant
/// BFS). The chain terminates — <c>tanc</c> has no parent and the leaves have no children — so the walk and
/// BFS both stop naturally. Overrides the default <c>GET task/{id}</c> so no other check's detail changes.
/// </summary>
internal sealed class TreeScenario : IE2EScenario
{
    public string Name => "tree";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_TREE") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "task/{id}", (_, path, query, _) =>
        {
            var id = FakeClickUp.LastSegment(path);
            var includeSubtasks = query.Contains("include_subtasks=true", StringComparison.OrdinalIgnoreCase);
            return FakeClickUp.OkAsync(TaskJson(id, includeSubtasks));
        }, 1),
    ];

    private static string TaskJson(string id, bool includeSubtasks)
    {
        // id -> (display name, parent id, direct child ids). Distinctive UPPER tokens so the check can
        // assert each row is present and correctly indented.
        var (name, parent, children) = id switch
        {
            "tanc" => ("Ancestor epic ANCESTOR", (string?)null, Array.Empty<string>()),
            "t0" => ("Release task ROOT", "tanc", new[] { "t0c1", "t0c2" }),
            "t0c1" => ("Subtask one CHILDONE", "t0", new[] { "t0c1a" }),
            "t0c1a" => ("Nested subtask GRANDKID", "t0c1", Array.Empty<string>()),
            "t0c2" => ("Subtask two CHILDTWO", "t0", Array.Empty<string>()),
            _ => ($"Task {id}", (string?)null, Array.Empty<string>()),
        };
        var parentField = parent is null ? "" : $",\"parent\":\"{parent}\"";
        var subtasksField = includeSubtasks && children.Length > 0
            ? $",\"subtasks\":[{string.Join(",", children.Select(c => TaskJson(c, includeSubtasks: false)))}]"
            : "";
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"status":"in progress","color":"#4194f6"},"list":{"id":"plist","name":"Personal Tasks"},"assignees":[],"date_updated":"1700000000000","url":"https://app.clickup.com/t/{{id}}"{{parentField}}{{subtasksField}}}
        """;
    }
}
