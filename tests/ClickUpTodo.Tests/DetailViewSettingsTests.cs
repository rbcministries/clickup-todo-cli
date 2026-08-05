using System.Text.Json;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the detail-view settings (issue #108): the enum cycle helpers used by the F2 cycle
/// buttons, the pure <see cref="DetailTab.ToTabIndex"/> mapping the detail screen relies on, the
/// defaults, and persistence (round-trip, persisted-as-string, backward-compatible load of an older
/// config with no <c>detailView</c> block). The Terminal.Gui F2 glue is verified by build + reasoning.
/// </summary>
public sealed class DetailViewSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    // ── enum cycle helpers ──────────────────────────────────────────────────

    [Fact]
    public void DetailTab_Next_CyclesAllFour_AndLoopsBack()
    {
        Assert.Equal(DetailTab.Description, DetailTab.Stream.Next());
        Assert.Equal(DetailTab.Comments, DetailTab.Description.Next());
        Assert.Equal(DetailTab.Other, DetailTab.Comments.Next());
        Assert.Equal(DetailTab.Stream, DetailTab.Other.Next());
    }

    [Fact]
    public void DetailTab_Next_FourPresses_ReturnToStart()
        => Assert.Equal(DetailTab.Stream, DetailTab.Stream.Next().Next().Next().Next());

    [Theory]
    [InlineData(DetailTab.Stream, 0)]
    [InlineData(DetailTab.Description, 1)]
    [InlineData(DetailTab.Comments, 2)]
    [InlineData(DetailTab.Other, 3)]
    public void DetailTab_ToTabIndex_MapsToScreenOrder(DetailTab tab, int expected)
        => Assert.Equal(expected, tab.ToTabIndex());

    [Fact]
    public void StreamSort_Next_TogglesAndBack()
    {
        Assert.Equal(StreamSort.Descending, StreamSort.Ascending.Next());
        Assert.Equal(StreamSort.Ascending, StreamSort.Descending.Next());
    }

    [Fact]
    public void StreamAutoScroll_Next_TogglesAndBack()
    {
        Assert.Equal(StreamAutoScroll.Oldest, StreamAutoScroll.Newest.Next());
        Assert.Equal(StreamAutoScroll.Newest, StreamAutoScroll.Oldest.Next());
    }

    [Fact]
    public void TaskLinkCtrlClickDestination_Next_TogglesAndBack()
    {
        Assert.Equal(TaskLinkCtrlClickDestination.NewTerminalTab, TaskLinkCtrlClickDestination.Browser.Next());
        Assert.Equal(TaskLinkCtrlClickDestination.Browser, TaskLinkCtrlClickDestination.NewTerminalTab.Next());
    }

    // ── defaults ────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AreStreamAscendingNewest()
    {
        var d = new DetailViewSettings();
        Assert.Equal(DetailTab.Stream, d.DefaultTab);
        Assert.Equal(StreamSort.Ascending, d.StreamSort);
        Assert.Equal(StreamAutoScroll.Newest, d.AutoScroll);
        // #320: the Ctrl+Click destination defaults to Browser (byte-identical to #318).
        Assert.Equal(TaskLinkCtrlClickDestination.Browser, d.TaskLinkCtrlClick);
    }

    [Fact]
    public void AppConfig_DetailView_DefaultsToNonNullAllDefaults()
    {
        var d = new AppConfig().DetailView;
        Assert.NotNull(d);
        Assert.Equal(DetailTab.Stream, d.DefaultTab);
        Assert.Equal(StreamSort.Ascending, d.StreamSort);
        Assert.Equal(StreamAutoScroll.Newest, d.AutoScroll);
        Assert.Equal(TaskLinkCtrlClickDestination.Browser, d.TaskLinkCtrlClick);
    }

    // ── persistence ─────────────────────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsDetailView()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            DetailView = new DetailViewSettings
            {
                DefaultTab = DetailTab.Comments,
                StreamSort = StreamSort.Descending,
                AutoScroll = StreamAutoScroll.Oldest,
                TaskLinkCtrlClick = TaskLinkCtrlClickDestination.NewTerminalTab,
            },
        });

        var d = store.Load().DetailView;
        Assert.Equal(DetailTab.Comments, d.DefaultTab);
        Assert.Equal(StreamSort.Descending, d.StreamSort);
        Assert.Equal(StreamAutoScroll.Oldest, d.AutoScroll);
        Assert.Equal(TaskLinkCtrlClickDestination.NewTerminalTab, d.TaskLinkCtrlClick);
    }

    [Fact]
    public void Save_PersistsDetailViewEnumsAsReadableStrings()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            DetailView = new DetailViewSettings
            {
                DefaultTab = DetailTab.Other,
                StreamSort = StreamSort.Descending,
                AutoScroll = StreamAutoScroll.Oldest,
                TaskLinkCtrlClick = TaskLinkCtrlClickDestination.NewTerminalTab,
            },
        });

        var json = File.ReadAllText(store.ConfigPath);
        using var doc = JsonDocument.Parse(json);
        var detail = doc.RootElement.GetProperty("detailView");
        Assert.Equal("Other", detail.GetProperty("defaultTab").GetString());
        Assert.Equal("Descending", detail.GetProperty("streamSort").GetString());
        Assert.Equal("Oldest", detail.GetProperty("autoScroll").GetString());
        Assert.Equal("NewTerminalTab", detail.GetProperty("taskLinkCtrlClick").GetString());
        // Never ordinals.
        Assert.DoesNotContain("\"defaultTab\":3", json);
    }

    [Fact]
    public void Load_WhenFileMissingDetailViewBlock_DefaultsToStreamAscendingNewest()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        // Rewrite without a detailView key (simulates a pre-#108 config.json).
        File.WriteAllText(store.ConfigPath, "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\"}");

        var d = store.Load().DetailView;
        Assert.Equal(DetailTab.Stream, d.DefaultTab);
        Assert.Equal(StreamSort.Ascending, d.StreamSort);
        Assert.Equal(StreamAutoScroll.Newest, d.AutoScroll);
        Assert.Equal(TaskLinkCtrlClickDestination.Browser, d.TaskLinkCtrlClick);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
