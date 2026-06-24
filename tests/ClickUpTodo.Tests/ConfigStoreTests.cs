using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_WhenNoFile_ReturnsUnconfiguredDefault()
    {
        var store = new ConfigStore(_dir);

        var config = store.Load();

        Assert.False(store.Exists());
        Assert.False(config.IsConfigured);
        Assert.Equal(60, config.RefreshSeconds);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = new ConfigStore(_dir);
        var original = new AppConfig
        {
            WorkspaceId = "123",
            WorkspaceName = "Acme",
            PersonalTasksListId = "456",
            PersonalTasksListName = "Personal Tasks",
            RefreshSeconds = 30,
            PinnedTaskIds = ["abc", "def"],
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.True(store.Exists());
        Assert.True(loaded.IsConfigured);
        Assert.Equal("123", loaded.WorkspaceId);
        Assert.Equal("Personal Tasks", loaded.PersonalTasksListName);
        Assert.Equal(30, loaded.RefreshSeconds);
        Assert.Equal(["abc", "def"], loaded.PinnedTaskIds);
    }

    [Fact]
    public void IsConfigured_RequiresWorkspaceAndList()
    {
        Assert.False(new AppConfig { WorkspaceId = "1" }.IsConfigured);
        Assert.False(new AppConfig { PersonalTasksListId = "2" }.IsConfigured);
        Assert.True(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" }.IsConfigured);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
