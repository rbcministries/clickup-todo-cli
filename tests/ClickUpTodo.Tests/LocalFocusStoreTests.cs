using ClickUpTodo.Configuration;
using ClickUpTodo.Focus;

namespace ClickUpTodo.Tests;

public sealed class LocalFocusStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SeedsFromExistingConfig()
    {
        var config = new AppConfig { PinnedTaskIds = ["a", "b"] };
        var store = new LocalFocusStore(config, new ConfigStore(_dir));

        Assert.True(store.IsPinned("a"));
        Assert.True(store.IsPinned("b"));
        Assert.False(store.IsPinned("c"));
    }

    [Fact]
    public async Task GetPinnedAsync_ReturnsCurrentSet()
    {
        var config = new AppConfig { PinnedTaskIds = ["a", "b"] };
        var store = new LocalFocusStore(config, new ConfigStore(_dir));

        var pinned = await store.GetPinnedAsync();

        Assert.Equal(new HashSet<string> { "a", "b" }, pinned);
    }

    [Fact]
    public async Task ToggleAsync_PinsAnUnpinnedTask()
    {
        var config = new AppConfig();
        var store = new LocalFocusStore(config, new ConfigStore(_dir));

        var nowPinned = await store.ToggleAsync("x");

        Assert.True(nowPinned);
        Assert.True(store.IsPinned("x"));
    }

    [Fact]
    public async Task ToggleAsync_UnpinsAPinnedTask()
    {
        var config = new AppConfig { PinnedTaskIds = ["x"] };
        var store = new LocalFocusStore(config, new ConfigStore(_dir));

        var nowPinned = await store.ToggleAsync("x");

        Assert.False(nowPinned);
        Assert.False(store.IsPinned("x"));
    }

    [Fact]
    public async Task ToggleAsync_PersistsToConfigInMemory()
    {
        var config = new AppConfig();
        var store = new LocalFocusStore(config, new ConfigStore(_dir));

        await store.ToggleAsync("x");

        Assert.Contains("x", config.PinnedTaskIds);
    }

    [Fact]
    public async Task ToggleAsync_PersistsToDisk()
    {
        var config = new AppConfig();
        var configStore = new ConfigStore(_dir);
        var store = new LocalFocusStore(config, configStore);

        await store.ToggleAsync("x");

        // A fresh load from disk must see the pinned id — i.e. the toggle was saved, not just held
        // in memory. This is the behaviour the TUI relied on before the seam (pins survive restart).
        var reloaded = configStore.Load();
        Assert.Contains("x", reloaded.PinnedTaskIds);
    }

    [Fact]
    public async Task ToggleAsync_RoundTrip_LeavesNoResidue()
    {
        var config = new AppConfig();
        var store = new LocalFocusStore(config, new ConfigStore(_dir));

        Assert.True(await store.ToggleAsync("x"));
        Assert.False(await store.ToggleAsync("x"));

        Assert.False(store.IsPinned("x"));
        Assert.Empty(config.PinnedTaskIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
