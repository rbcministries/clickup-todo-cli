using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

public sealed class DispatchWorkingDirectoryCacheTests
{
    [Fact]
    public void PreFill_ReturnsStoredPath_WhenPresent()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "/work/repo" };

        Assert.Equal("/work/repo", DispatchWorkingDirectoryCache.PreFill(cache, "task-1"));
    }

    [Fact]
    public void PreFill_ReturnsBlank_WhenMissing()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "/work/repo" };

        Assert.Equal("", DispatchWorkingDirectoryCache.PreFill(cache, "task-2"));
    }

    [Fact]
    public void PreFill_ReturnsBlank_WhenStoredValueIsBlank()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "   " };

        Assert.Equal("", DispatchWorkingDirectoryCache.PreFill(cache, "task-1"));
    }

    [Fact]
    public void Update_StoresExplicitNonDefaultPick_AndReportsChange()
    {
        var cache = new Dictionary<string, string>();

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "/work/repo", resolvedDefault: "/base");

        Assert.True(changed);
        Assert.Equal("/work/repo", cache["task-1"]);
    }

    [Fact]
    public void Update_TrimsStoredPick()
    {
        var cache = new Dictionary<string, string>();

        DispatchWorkingDirectoryCache.Update(cache, "task-1", "  /work/repo  ", resolvedDefault: "/base");

        Assert.Equal("/work/repo", cache["task-1"]);
    }

    [Fact]
    public void Update_NoOps_WhenPickMatchesExistingEntry()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "/work/repo" };

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "/work/repo", resolvedDefault: "/base");

        Assert.False(changed);
        Assert.Equal("/work/repo", cache["task-1"]);
    }

    [Fact]
    public void Update_OverwritesExistingEntry_WhenPickChanges()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "/work/old" };

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "/work/new", resolvedDefault: "/base");

        Assert.True(changed);
        Assert.Equal("/work/new", cache["task-1"]);
    }

    [Fact]
    public void Update_RemovesEntry_WhenPickIsBlank()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "/work/repo" };

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "   ", resolvedDefault: "/base");

        Assert.True(changed);
        Assert.False(cache.ContainsKey("task-1"));
    }

    [Fact]
    public void Update_RemovesEntry_WhenPickEqualsResolvedDefault()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "/work/repo" };

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "/base", resolvedDefault: "/base");

        Assert.True(changed);
        Assert.False(cache.ContainsKey("task-1"));
    }

    [Fact]
    public void Update_RemovingAbsentEntry_ReportsNoChange()
    {
        var cache = new Dictionary<string, string>();

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "", resolvedDefault: "/base");

        Assert.False(changed);
        Assert.Empty(cache);
    }

    [Fact]
    public void Update_DoesNotPersistPickEqualToDefault_ForFreshTask()
    {
        var cache = new Dictionary<string, string>();

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "/base", resolvedDefault: "/base");

        Assert.False(changed);
        Assert.Empty(cache);
    }

    [Fact]
    public void Update_LeavesOtherTasksUntouched()
    {
        var cache = new Dictionary<string, string> { ["task-1"] = "/work/one", ["task-2"] = "/work/two" };

        DispatchWorkingDirectoryCache.Update(cache, "task-1", "", resolvedDefault: "/base");

        Assert.False(cache.ContainsKey("task-1"));
        Assert.Equal("/work/two", cache["task-2"]);
    }

    [Fact]
    public void Update_StoresPick_WhenResolvedDefaultIsNull()
    {
        var cache = new Dictionary<string, string>();

        var changed = DispatchWorkingDirectoryCache.Update(cache, "task-1", "/work/repo", resolvedDefault: null);

        Assert.True(changed);
        Assert.Equal("/work/repo", cache["task-1"]);
    }
}
