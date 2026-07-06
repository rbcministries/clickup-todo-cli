using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;
using ClickUpTodo.Services;
using ClickUpTodo.Tui;

// Boots the REAL TodoApp against a canned in-process ClickUp backend so the TUI can be
// driven under a PTY and its keypress latency measured end-to-end. No network.

var taskCount = int.TryParse(Environment.GetEnvironmentVariable("E2E_TASKS"), out var n) ? n : 200;

var config = new AppConfig
{
    WorkspaceId = "ws1",
    WorkspaceName = "Bench",
    PersonalTasksListId = "plist",
    PersonalTasksListName = "Personal Tasks",
    RefreshSeconds = int.TryParse(Environment.GetEnvironmentVariable("E2E_REFRESH"), out var r) ? r : 600,
};

if (Environment.GetEnvironmentVariable("E2E_VIEW") == "rich")
{
    // A realistic power view: grouped by list, subtasks nested, a few pins.
    config.View.GroupField = TaskField.List;
    config.View.ShowSubtasks = true;
    config.PinnedTaskIds = ["t1", "t5", "t9"];
}

var client = new ClickUpClient("fake-token", new HttpClient(new FakeClickUp(taskCount)));
var configStore = new ConfigStore();
var tasks = new TaskService(client, config, 1);
var focus = new LocalFocusStore(config, configStore);
new TodoApp(tasks, config, configStore, focus).Run("ansi");
return;

sealed class FakeClickUp(int taskCount) : HttpMessageHandler
{
    private static readonly string[] Statuses = ["to do", "in progress", "blocked", "in review"];
    private static readonly string[] StatusColors = ["#d3d3d3", "#4194f6", "#e50000", "#a875ff"];
    private static readonly string[] Lists = ["plist", "list2", "list3"];
    private static readonly string[] ListNames = ["Personal Tasks", "Q3 Website Refresh", "Ministry Ops"];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        string body;

        if (path.EndsWith("/user"))
            body = """{"user":{"id":1,"username":"bench","email":"bench@example.com"}}""";
        else if (path.Contains("/team/") && path.EndsWith("/task"))
            body = TasksJson(page: PageOf(query), taskCount);
        else if (path.Contains("/list/") && path.EndsWith("/task"))
            body = """{"tasks":[],"last_page":true}""";
        else if (path.Contains("/list/"))
            body = ListJson(path);
        else if (path.EndsWith("/team"))
            body = """{"teams":[{"id":"ws1","name":"Bench","members":[]}]}""";
        else
            body = "{}";

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    private static int PageOf(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
            if (part.StartsWith("page=") && int.TryParse(part[5..], out var p))
                return p;
        return 0;
    }

    private static string TasksJson(int page, int total)
    {
        const int pageSize = 100;
        var start = page * pageSize;
        var count = Math.Clamp(total - start, 0, pageSize);
        var sb = new StringBuilder();
        sb.Append("{\"tasks\":[");
        for (var i = 0; i < count; i++)
        {
            var k = start + i;
            var li = k % 3;
            if (i > 0) sb.Append(',');
            // Every 4th task is a subtask of the task 3 before it (same list), so the F4
            // nested view has real parents to nest under.
            var parent = k % 4 == 3 ? $",\"parent\":\"t{k - 3}\"" : "";
            sb.Append($$"""
            {"id":"t{{k}}","name":"Task {{k}} — follow up on the {{ListNames[li]}} item with a realistic title 📌","status":{"status":"{{Statuses[k % 4]}}","color":"{{StatusColors[k % 4]}}"},"list":{"id":"{{Lists[li]}}","name":"{{ListNames[li]}}"},"due_date":"{{DateTimeOffset.UtcNow.AddDays(k % 14).ToUnixTimeMilliseconds()}}","date_updated":"1700000000000","url":"https://app.clickup.com/t/t{{k}}"{{parent}}{{(k % 3 == 0 ? ",\"priority\":{\"priority\":\"high\",\"color\":\"#f50000\"}" : "")}}}
            """);
        }
        sb.Append($"],\"last_page\":{(start + count >= total ? "true" : "false")}}}");
        return sb.ToString();
    }

    private static string ListJson(string path)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        var idx = Array.IndexOf(Lists, id);
        var name = idx >= 0 ? ListNames[idx] : id;
        return $$"""
        {"id":"{{id}}","name":"{{name}}","status":{"color":"#e16b16"},"statuses":[{"status":"to do","color":"#d3d3d3","orderindex":0},{"status":"in progress","color":"#4194f6","orderindex":1},{"status":"blocked","color":"#e50000","orderindex":2},{"status":"in review","color":"#a875ff","orderindex":3},{"status":"complete","color":"#6bc950","orderindex":4}]}
        """;
    }
}
