using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// The <see cref="AuthMode"/> persisted in config (#52): it must default to
/// <see cref="AuthMode.PersonalToken"/> for fresh and pre-#52 configs, round-trip OAuth, and
/// serialize as a readable string.
/// </summary>
public sealed class AuthModeConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Default_IsPersonalToken()
    {
        Assert.Equal(AuthMode.PersonalToken, new AppConfig().AuthMode);
    }

    [Fact]
    public void Load_WhenFileMissingAuthMode_DefaultsToPersonalToken()
    {
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        // A pre-#52 config.json without an authMode key.
        File.WriteAllText(store.ConfigPath, "{\"workspaceId\":\"1\",\"personalTasksListId\":\"2\"}");

        var loaded = store.Load();

        Assert.Equal(AuthMode.PersonalToken, loaded.AuthMode);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsOAuthMode()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2", AuthMode = AuthMode.OAuth });

        Assert.Equal(AuthMode.OAuth, store.Load().AuthMode);
    }

    [Fact]
    public void Save_PersistsAuthModeAsReadableString()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { AuthMode = AuthMode.OAuth });

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("OAuth", json);
        Assert.DoesNotContain("\"authMode\":1", json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
