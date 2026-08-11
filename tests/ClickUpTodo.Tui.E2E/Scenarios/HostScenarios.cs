using ClickUpTodo.Services;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tui.E2E;

/// <summary>Single-task launch mode (#296): E2E_SINGLE_TASK=&lt;id&gt; boots <see cref="SingleTaskApp"/>
/// straight into that task's detail view — the harness equivalent of <c>clickup-todo --task &lt;id&gt;</c> —
/// instead of the dashboard. It shares the same #304 browser launcher so a Ctrl+B host rewrite is
/// observable in single-task mode too, and gets the assignee-frequency pool so the Ctrl+N composer's
/// @-mention picker has candidates (#473).</summary>
internal sealed class SingleTaskScenario : IE2EScenario, IAppHost
{
    public string Name => "single-task";
    public bool IsActive => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("E2E_SINGLE_TASK"));
    public IAppHost? Host => this;

    public async Task RunAsync(HarnessServices s)
    {
        var id = Environment.GetEnvironmentVariable("E2E_SINGLE_TASK")!;
        var launchTask = await s.Tasks.GetTaskDetailAsync(id);
        var launchComments = await s.Tasks.GetTaskCommentsAsync(id);
        new SingleTaskApp(s.Tasks, s.Config, s.ConfigStore, launchTask, launchComments, s.Browser, assignees: s.Assignees)
            .Run("ansi");
    }
}

/// <summary>Standalone feed host (#509): E2E_FEED=1 boots <see cref="FeedApp"/> straight into the mentions &amp;
/// comments feed — the harness equivalent of <c>clickup-todo --feed</c> — instead of the dashboard. Seeded
/// empty (a cold launch), so the check exercises FeedApp's on-show live-load path just like the real host;
/// the recording tab launcher (E2E_TAB_LOG) is passed so an Enter-to-open-a-task-tab leg is observable.</summary>
internal sealed class FeedScenario : IE2EScenario, IAppHost
{
    public string Name => "feed";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_FEED") == "1";
    public IAppHost? Host => this;

    public Task RunAsync(HarnessServices s)
    {
        new FeedApp(s.Feed, s.FeedCache, s.Config, s.ConfigStore, FeedResult.Empty,
            changeMarkers: s.ChangeMarkers, tabLauncher: s.TabLauncher).Run("ansi");
        return Task.CompletedTask;
    }
}
