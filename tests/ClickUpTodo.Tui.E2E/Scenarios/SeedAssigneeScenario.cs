using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>#234 repro seam (E2E_QU_SEED_ASSIGNEE=1): tasks open Quick Updates already assigned to a member,
/// so the Assignees pane's empty-state row 0 is a removable ✓ row — the state where a stray Enter in the
/// <em>empty</em> search box used to silently remove them. Seeds the shared assignee set (so the detail read
/// and the add/remove round-trip reflect it) and overrides the team-tasks rows to carry the same assignee, so
/// whichever task the cursor opens Quick Updates on has the seeded ✓ row. Off ⇒ the original empty set.</summary>
internal sealed class SeedAssigneeScenario : IE2EScenario
{
    private const long SeededAssigneeId = 101; // Ada Lovelace (Members[0])

    public string Name => "qu-seed-assignee";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_QU_SEED_ASSIGNEE") == "1";

    public void SeedBackend(FakeClickUp backend) => backend.Assignees.Add(SeededAssigneeId);

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "team/{id}/task", (_, _, query, _) =>
        {
            var json = backend.TasksJson(FakeClickUp.PageOf(query), backend.TaskCount, FakeClickUp.IncludeClosed(query));
            var node = JsonNode.Parse(json)!;
            foreach (var t in node["tasks"]!.AsArray())
            {
                // Every seeded task carries the assignee; the completed tclosed row is left as-is (it never
                // opens Quick Updates), matching the original per-row seed.
                if (t!["id"]?.GetValue<string>() == "tclosed")
                    continue;
                t["assignees"] = new JsonArray(new JsonObject
                {
                    ["id"] = SeededAssigneeId,
                    ["username"] = FakeClickUp.Members[0].Name,
                });
            }
            return FakeClickUp.OkAsync(node.ToJsonString());
        }, 1),
    ];
}
