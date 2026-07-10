using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the persistence seam (#120): the file-backed <see cref="JsonFileStateStore"/> and the
/// backend-agnostic contract that lets <see cref="ConfigStore"/> run over any <see cref="IStateStore"/>.
/// </summary>
public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private sealed record Sample(string Name, int Count);

    // --- JsonFileStateStore ---------------------------------------------------------------------

    [Fact]
    public void Save_ThenLoad_RoundTripsValue()
    {
        var store = new JsonFileStateStore(_dir);

        store.Save("thing", new Sample("hello", 7));
        var loaded = store.Load<Sample>("thing");

        Assert.Equal(new Sample("hello", 7), loaded);
    }

    [Fact]
    public void Load_WhenKeyAbsent_ReturnsNull()
    {
        var store = new JsonFileStateStore(_dir);

        Assert.Null(store.Load<Sample>("missing"));
    }

    [Fact]
    public void Exists_TracksSaveAndDelete()
    {
        var store = new JsonFileStateStore(_dir);
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
        var store = new JsonFileStateStore(_dir);

        // Must not throw when nothing is stored under the key.
        store.Delete("never-written");

        Assert.False(store.Exists("never-written"));
    }

    [Fact]
    public void ConfigKey_MapsToConfigJson()
    {
        var store = new JsonFileStateStore(_dir);

        Assert.Equal(Path.Combine(_dir, "config.json"), store.PathFor(StateKeys.Config));
    }

    [Fact]
    public void Save_CreatesDirectory_AndWritesTheFile()
    {
        var store = new JsonFileStateStore(_dir);
        Assert.False(Directory.Exists(_dir));

        store.Save(StateKeys.Config, new AppConfig { WorkspaceId = "1" });

        Assert.True(File.Exists(store.PathFor(StateKeys.Config)));
    }

    [Fact]
    public void Save_SerializesCamelCaseIndentedEnumsAsStrings()
    {
        var store = new JsonFileStateStore(_dir);

        store.Save(StateKeys.Config, new AppConfig
        {
            DefaultWorkingDirectory = "/work",
            AgentDispatch = new AgentDispatchSettings { PreferredTerminal = PreferredTerminal.WindowsTerminal },
        });

        var json = File.ReadAllText(store.PathFor(StateKeys.Config));
        // camelCase + indented (space after the colon), matching the original ConfigStore guarantees.
        Assert.Contains("\"defaultWorkingDirectory\": \"/work\"", json);
        // Enums persist as readable strings, not ordinals.
        Assert.Contains("WindowsTerminal", json);
        Assert.DoesNotContain("\"preferredTerminal\":1", json);
    }

    [Fact]
    public void IndependentKeys_CoexistWithoutClobbering()
    {
        // Proves cache payloads (#122/#123/#125) can sit beside the config document under their own
        // keys without stepping on each other.
        var store = new JsonFileStateStore(_dir);

        store.Save(StateKeys.Config, new AppConfig { WorkspaceId = "ws" });
        store.Save("task-cache", new Sample("cached", 42));

        Assert.Equal("ws", store.Load<AppConfig>(StateKeys.Config)!.WorkspaceId);
        Assert.Equal(new Sample("cached", 42), store.Load<Sample>("task-cache"));
    }

    [Fact]
    public void DefaultDirectory_IsUnderClickUpTodo()
    {
        Assert.EndsWith("clickup-todo", JsonFileStateStore.DefaultDirectory());
    }

    // --- Backend-agnosticism: ConfigStore over an in-memory IStateStore -------------------------

    [Fact]
    public void ConfigStore_OverInMemoryBackend_RoundTripsWithoutDisk()
    {
        // The drop-in criterion, concretely: ConfigStore is decoupled from the file format — a
        // non-file IStateStore (as LiteDB will be) works with no call-site change and no disk I/O.
        var backend = new InMemoryStateStore();
        var store = new ConfigStore(backend);

        Assert.False(store.Exists());

        store.Save(new AppConfig
        {
            WorkspaceId = "123",
            PersonalTasksListId = "456",
            PinnedTaskIds = ["a", "b"],
            AgentDispatch = new AgentDispatchSettings { ClaudeExecutable = "/opt/claude" },
        });

        Assert.True(store.Exists());
        // No file backend, so file-specific accessors are empty rather than pointing at a real path.
        Assert.Equal(string.Empty, store.ConfigPath);
        Assert.Equal(string.Empty, store.DirectoryPath);

        var loaded = store.Load();
        Assert.True(loaded.IsConfigured);
        Assert.Equal(["a", "b"], loaded.PinnedTaskIds);
        Assert.Equal("/opt/claude", loaded.AgentDispatch.ClaudeExecutable);
    }

    [Fact]
    public void ConfigStore_Delete_ForgetsSettings()
    {
        var backend = new InMemoryStateStore();
        var store = new ConfigStore(backend);
        store.Save(new AppConfig { WorkspaceId = "1", PersonalTasksListId = "2" });
        Assert.True(store.Exists());

        store.Delete();

        Assert.False(store.Exists());
        Assert.False(store.Load().IsConfigured);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A minimal non-file <see cref="IStateStore"/> — stands in for a future backend (LiteDB).</summary>
    private sealed class InMemoryStateStore : IStateStore
    {
        private readonly Dictionary<string, object> _values = [];

        public bool Exists(string key) => _values.ContainsKey(key);

        public T? Load<T>(string key) where T : class
            => _values.TryGetValue(key, out var v) ? (T)v : null;

        public void Save<T>(string key, T value) where T : class => _values[key] = value;

        public void Delete(string key) => _values.Remove(key);
    }
}
