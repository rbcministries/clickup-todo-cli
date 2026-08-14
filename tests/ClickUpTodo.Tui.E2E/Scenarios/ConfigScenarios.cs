using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tui.E2E;

/// <summary>Rich power-view scenario (E2E_VIEW=rich): grouped by list, subtasks nested (all assignees,
/// #179), a few pins — a realistic dashboard for the screen/latency checks.</summary>
internal sealed class RichViewScenario : IE2EScenario
{
    public string Name => "rich-view";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_VIEW") == "rich";

    public void Configure(AppConfig config)
    {
        config.View.GroupField = TaskField.List;
        config.View.Subtasks = SubtaskView.All;
        config.PinnedTaskIds = ["t1", "t5", "t9"];
    }
}

/// <summary>#320: E2E_LINK_CTRL_DEST=tab sets the persisted task-link Ctrl+Click destination to a new
/// terminal tab, so the link-destination check can drive Ctrl+/Ctrl+Shift+click and observe the new-tab
/// launch. Default (unset) keeps the Browser destination, matching every other check's #318 behaviour.</summary>
internal sealed class LinkCtrlDestScenario : IE2EScenario
{
    public string Name => "link-ctrl-dest-tab";
    public bool IsActive => Environment.GetEnvironmentVariable("E2E_LINK_CTRL_DEST") == "tab";

    public void Configure(AppConfig config)
        => config.DetailView.TaskLinkCtrlClick = TaskLinkCtrlClickDestination.NewTerminalTab;
}

/// <summary>#506: seed launch-chord overrides (E2E_LAUNCH_NEWTAB / E2E_LAUNCH_SPLIT) so the configurable
/// launch-chord check can boot with a rebound Ctrl+Enter/Ctrl+Alt+Enter and observe the main-list footer
/// advertise it (and the list dispatcher fire it). Absent ⇒ both null ⇒ the shipped defaults, so every
/// other check sees the stock chords and their footers byte-for-byte.</summary>
internal sealed class LaunchChordScenario : IE2EScenario
{
    public string Name => "launch-chords";

    public bool IsActive =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_LAUNCH_NEWTAB"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_LAUNCH_SPLIT"));

    public void Configure(AppConfig config)
    {
        config.LaunchChords.NewTab = Environment.GetEnvironmentVariable("E2E_LAUNCH_NEWTAB");
        config.LaunchChords.SplitPane = Environment.GetEnvironmentVariable("E2E_LAUNCH_SPLIT");
    }
}

/// <summary>#304: seed a workspace subdomain (E2E_SUBDOMAIN) so a Ctrl+B launch rewrites the fake backend's
/// app.clickup.com task URLs onto {subdomain}.clickup.com. Absent ⇒ blank ⇒ no rewrite (the default).</summary>
internal sealed class SubdomainScenario : IE2EScenario
{
    public string Name => "subdomain";
    public bool IsActive => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_SUBDOMAIN"));

    public void Configure(AppConfig config)
        => config.WorkspaceSubdomain = Environment.GetEnvironmentVariable("E2E_SUBDOMAIN") ?? "";
}
