using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the logout cache wipe (#124): <see cref="CacheReset.ClearAll"/> must forget every cache
/// payload — the task working set (#122), feed (#123), status/color metadata (#125), and the
/// assignee-frequency pool (#155) — so a <c>--reset</c> into a different account/workspace leaves
/// nothing behind. Guards against a future edit silently dropping a key from the cleared set (the
/// assignee-cache clear in particular is the behaviour #124 adds).
/// </summary>
public sealed class CacheResetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private sealed record Dummy(string Value);

    [Fact]
    public void ClearAll_RemovesEveryCachePayload()
    {
        var store = new JsonFileStateStore(_dir);
        foreach (var key in CacheReset.CacheKeys)
            store.Save(key, new Dummy(key));
        Assert.All(CacheReset.CacheKeys, key => Assert.True(store.Exists(key)));

        CacheReset.ClearAll(store);

        Assert.All(CacheReset.CacheKeys, key => Assert.False(store.Exists(key)));
    }

    [Fact]
    public void CacheKeys_IncludesEveryKnownCache_IncludingTheAssigneePool()
    {
        // The assignee-frequency pool clear is the reset gap #124 closes — pin it explicitly, alongside
        // the other caches, so it can't regress unnoticed.
        Assert.Contains(StateKeys.Tasks, CacheReset.CacheKeys);
        Assert.Contains(StateKeys.Feed, CacheReset.CacheKeys);
        Assert.Contains(StateKeys.Statuses, CacheReset.CacheKeys);
        Assert.Contains(StateKeys.ListColors, CacheReset.CacheKeys);
        Assert.Contains(StateKeys.Assignees, CacheReset.CacheKeys);
        // The warm closed-task set (#280) persists across restarts, so a logout must forget it too.
        Assert.Contains(StateKeys.Closed, CacheReset.CacheKeys);
        // The Super Agent directory (#494) is workspace-scoped and persisted, so a logout into a
        // different account/workspace must forget it too.
        Assert.Contains(StateKeys.AgentDirectories, CacheReset.CacheKeys);
    }

    [Fact]
    public void ClearAll_WhenNothingStored_IsANoOp()
        => CacheReset.ClearAll(new JsonFileStateStore(_dir)); // must not throw

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
