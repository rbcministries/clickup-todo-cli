using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the LiteDB backend adopted in #121: the <see cref="LiteDbStateStore"/> honours the same
/// <see cref="IStateStore"/> contract as the file backend (round-trip, absence, delete, coexisting
/// keys, cross-instance persistence) and stores payloads with the shared serializer contract, so
/// <see cref="ConfigStore"/> runs over it unchanged.
/// </summary>
public sealed class LiteDbStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "state.db");

    private sealed record Sample(string Name, int Count);

    [Fact]
    public void Save_ThenLoad_RoundTripsValue()
    {
        using var store = new LiteDbStateStore(DbPath);

        store.Save("thing", new Sample("hello", 7));

        Assert.Equal(new Sample("hello", 7), store.Load<Sample>("thing"));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAppConfig()
    {
        using var store = new LiteDbStateStore(DbPath);

        store.Save(StateKeys.Config, new AppConfig
        {
            WorkspaceId = "ws",
            PersonalTasksListId = "list",
            PinnedTaskIds = ["a", "b"],
            AgentDispatch = new AgentDispatchSettings
            {
                Providers = [new DispatchProvider { Name = "Claude", Executable = "/opt/claude" }],
                DefaultProviderName = "Claude",
            },
        });

        var loaded = store.Load<AppConfig>(StateKeys.Config)!;
        Assert.Equal("ws", loaded.WorkspaceId);
        Assert.Equal("list", loaded.PersonalTasksListId);
        Assert.Equal(["a", "b"], loaded.PinnedTaskIds);
        Assert.Equal("/opt/claude", loaded.AgentDispatch.ResolveDefaultProvider().Executable);
    }

    [Fact]
    public void Load_WhenKeyAbsent_ReturnsNull()
    {
        using var store = new LiteDbStateStore(DbPath);

        Assert.Null(store.Load<Sample>("missing"));
    }

    [Fact]
    public void Exists_TracksSaveAndDelete()
    {
        using var store = new LiteDbStateStore(DbPath);
        Assert.False(store.Exists("thing"));

        store.Save("thing", new Sample("x", 1));
        Assert.True(store.Exists("thing"));

        store.Delete("thing");
        Assert.False(store.Exists("thing"));
        Assert.Null(store.Load<Sample>("thing"));
    }

    [Fact]
    public void Delete_WhenKeyAbsent_IsNoOp()
    {
        using var store = new LiteDbStateStore(DbPath);

        // Must not throw when nothing is stored under the key.
        store.Delete("never-written");

        Assert.False(store.Exists("never-written"));
    }

    [Fact]
    public void Save_ReplacesPriorValueForKey()
    {
        using var store = new LiteDbStateStore(DbPath);

        store.Save("thing", new Sample("first", 1));
        store.Save("thing", new Sample("second", 2));

        Assert.Equal(new Sample("second", 2), store.Load<Sample>("thing"));
    }

    [Fact]
    public void IndependentKeys_CoexistWithoutClobbering()
    {
        // Proves cache payloads (#122/#123/#125) can sit beside the config document under their own
        // keys without stepping on each other.
        using var store = new LiteDbStateStore(DbPath);

        store.Save(StateKeys.Config, new AppConfig { WorkspaceId = "ws" });
        store.Save("task-cache", new Sample("cached", 42));

        Assert.Equal("ws", store.Load<AppConfig>(StateKeys.Config)!.WorkspaceId);
        Assert.Equal(new Sample("cached", 42), store.Load<Sample>("task-cache"));
    }

    [Fact]
    public void Values_PersistAcrossStoreInstances_OverTheSameFile()
    {
        using (var store = new LiteDbStateStore(DbPath))
            store.Save(StateKeys.Config, new AppConfig { WorkspaceId = "persisted" });

        // A fresh store over the same file (a subsequent app launch) sees the earlier write.
        using var reopened = new LiteDbStateStore(DbPath);
        Assert.Equal("persisted", reopened.Load<AppConfig>(StateKeys.Config)!.WorkspaceId);
    }

    [Fact]
    public void PayloadUsesSharedSerializerContract_CamelCaseEnumsAsStrings()
    {
        // The value stored round-trips through the same System.Text.Json contract as the file backend,
        // so enums survive as readable strings (not ordinals) across a store instance boundary.
        using (var store = new LiteDbStateStore(DbPath))
        {
            store.Save(StateKeys.Config, new AppConfig
            {
                AgentDispatch = new AgentDispatchSettings { PreferredTerminal = PreferredTerminal.WindowsTerminal },
            });
        }

        using var reopened = new LiteDbStateStore(DbPath);
        Assert.Equal(PreferredTerminal.WindowsTerminal, reopened.Load<AppConfig>(StateKeys.Config)!.AgentDispatch.PreferredTerminal);
    }

    [Fact]
    public void CreatesDatabaseDirectory_WhenMissing()
    {
        Assert.False(Directory.Exists(_dir));

        using var store = new LiteDbStateStore(DbPath);
        store.Save("thing", new Sample("x", 1));

        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void DefaultDatabasePath_IsStateDbUnderClickUpTodo()
    {
        var path = LiteDbStateStore.DefaultDatabasePath();
        Assert.EndsWith(Path.Combine("clickup-todo", "state.db"), path);
    }

    [Fact]
    public void ConfigStore_OverLiteDbBackend_RoundTrips()
    {
        // The drop-in criterion, concretely: ConfigStore is decoupled from the file format — the
        // LiteDB backend works with no call-site change.
        using var backend = new LiteDbStateStore(DbPath);
        var store = new ConfigStore(backend);

        Assert.False(store.Exists());

        store.Save(new AppConfig
        {
            WorkspaceId = "123",
            PersonalTasksListId = "456",
            PinnedTaskIds = ["a", "b"],
            AgentDispatch = new AgentDispatchSettings
            {
                Providers = [new DispatchProvider { Name = "Claude", Executable = "/opt/claude" }],
                DefaultProviderName = "Claude",
            },
        });

        Assert.True(store.Exists());
        // Not a file backend, so the file-specific accessors are empty.
        Assert.Equal(string.Empty, store.ConfigPath);
        Assert.Equal(string.Empty, store.DirectoryPath);

        var loaded = store.Load();
        Assert.True(loaded.IsConfigured);
        Assert.Equal(["a", "b"], loaded.PinnedTaskIds);
        Assert.Equal("/opt/claude", loaded.AgentDispatch.ResolveDefaultProvider().Executable);

        store.Delete();
        Assert.False(store.Exists());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
