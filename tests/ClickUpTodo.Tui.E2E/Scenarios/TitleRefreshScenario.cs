using System.Text.Json.Nodes;

namespace ClickUpTodo.Tui.E2E;

using Handler = FakeClickUp.RouteHandler;

/// <summary>#425 (E2E_TITLE_REFRESH=1): the launch task is renamed after its first (boot) detail fetch, so a
/// refresh (Ctrl+R / F5) must move the terminal tab title. The boot fetch keeps the original long name; every
/// detail GET after it returns a short renamed title the check can assert. Overrides the default
/// <c>GET task/{id}</c>, reusing the default detail and patching only the name after the first read (a PUT
/// echo goes through the default route, so a write can't inflate the counter). Off ⇒ the fixed launch-task
/// name every other scenario sees.</summary>
internal sealed class TitleRefreshScenario : IE2EScenario
{
    private int _fetches;

    public string Name => "title-refresh";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_TITLE_REFRESH") == "1";

    public IEnumerable<Route<Handler>> Routes(FakeClickUp backend) =>
    [
        new(HttpMethod.Get, "task/{id}", (_, path, query, _) =>
        {
            var id = FakeClickUp.LastSegment(path);
            if (FakeClickUp.TaskGetSentinel(id, query) is { } notFound)
                return Task.FromResult(notFound);
            var json = backend.DetailJson(id);
            if (System.Threading.Interlocked.Increment(ref _fetches) > 1)
            {
                var node = JsonNode.Parse(json)!;
                node["name"] = "Renamed on refresh";
                json = node.ToJsonString();
            }
            return FakeClickUp.OkAsync(json);
        }, 1),
    ];
}
