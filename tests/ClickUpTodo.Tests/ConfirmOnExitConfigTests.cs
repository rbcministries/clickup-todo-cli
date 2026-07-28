using System.Text.Json;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the optional skip-exit-confirmation preference (issue #407): the persisted
/// <see cref="AppConfig.ConfirmOnExit"/> flag its default, its round-trip through
/// <see cref="ConfigStore"/>, and — the crux of the issue — that an older <c>config.json</c> with no
/// <c>confirmOnExit</c> key loads with the confirmation still <b>on</b>, so existing users keep the
/// #299 guard with no migration. The F2 Settings glue and the hosts' <c>RequestExit</c> branch are
/// Terminal.Gui code, verified by build + reasoning per the repo's TUI convention.
/// </summary>
public sealed class ConfirmOnExitConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Default_IsOn()
        => Assert.True(new AppConfig().ConfirmOnExit);

    [Fact]
    public void SaveThenLoad_RoundTripsAnExplicitOptOut()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2", ConfirmOnExit = false });

        Assert.False(store.Load().ConfirmOnExit);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheOnValue()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2", ConfirmOnExit = true });

        Assert.True(store.Load().ConfirmOnExit);
    }

    // The backward-compat contract: a pre-#407 config.json has no confirmOnExit key, and it must load
    // with the confirmation ON — a new bool defaulting to false would silently disable the #299 guard.
    [Fact]
    public void Load_WhenFileMissingConfirmOnExitKey_DefaultsToOn()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        // Rewrite without a confirmOnExit key (simulates a pre-#407 config.json).
        File.WriteAllText(store.ConfigPath, "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\"}");

        Assert.True(store.Load().ConfirmOnExit);
    }

    // Persisted as a JSON bool, never an ordinal/number — the value the F2 toggle writes.
    [Fact]
    public void Save_PersistsConfirmOnExitAsABool()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { ConfirmOnExit = false });

        var json = File.ReadAllText(store.ConfigPath);
        using var doc = JsonDocument.Parse(json);
        var prop = doc.RootElement.GetProperty("confirmOnExit");
        Assert.Equal(JsonValueKind.False, prop.ValueKind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
