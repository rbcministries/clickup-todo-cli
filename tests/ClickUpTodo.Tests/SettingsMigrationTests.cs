using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the one-time legacy-settings import (#121): upgrading users keep their <c>config.json</c>
/// settings in the new LiteDB backend, the import is idempotent, and the old file is left in place so
/// a downgrade is still possible. The target uses an in-memory <see cref="IStateStore"/> double so
/// the migration logic is exercised independently of the LiteDB file backend.
/// </summary>
public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshInstall_NoLegacyFile_IsNoOp()
    {
        var legacy = new JsonFileStateStore(_dir); // nothing written
        var target = new InMemoryStateStore();

        var imported = SettingsMigration.ImportLegacyConfig(target, legacy);

        Assert.False(imported);
        Assert.False(target.Exists(StateKeys.Config));
        Assert.False(new ConfigStore(target).Load().IsConfigured);
    }

    [Fact]
    public void Upgrade_WithLegacyConfig_ImportsIdenticalEffectiveSettings()
    {
        // Arrange: a real config.json written through the file backend, as an existing install has.
        var legacy = new JsonFileStateStore(_dir);
        new ConfigStore(legacy).Save(new AppConfig
        {
            WorkspaceId = "ws-1",
            WorkspaceName = "Acme",
            PersonalTasksListId = "list-1",
            RefreshSeconds = 45,
            PinnedTaskIds = ["p1", "p2"],
        });

        var target = new InMemoryStateStore();

        // Act
        var imported = SettingsMigration.ImportLegacyConfig(target, legacy);

        // Assert: imported, and the effective settings loaded from the target match the legacy ones.
        Assert.True(imported);
        var migrated = new ConfigStore(target).Load();
        var original = new ConfigStore(legacy).Load();
        Assert.True(migrated.IsConfigured);
        Assert.Equal(original.WorkspaceId, migrated.WorkspaceId);
        Assert.Equal(original.WorkspaceName, migrated.WorkspaceName);
        Assert.Equal(original.PersonalTasksListId, migrated.PersonalTasksListId);
        Assert.Equal(original.RefreshSeconds, migrated.RefreshSeconds);
        Assert.Equal(original.PinnedTaskIds, migrated.PinnedTaskIds);
        Assert.Equal(original.SchemaVersion, migrated.SchemaVersion);
    }

    [Fact]
    public void Import_WithCorruptLegacyConfig_IsNoOpAndDoesNotThrow()
    {
        // A truncated/garbled config.json (e.g. a crash mid non-atomic write) must not abort startup:
        // the import treats it as nothing importable so the app falls through to first-time setup.
        var legacy = new JsonFileStateStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(legacy.PathFor(StateKeys.Config), "{ \"workspaceId\": \"ws\", ");  // malformed

        var target = new InMemoryStateStore();

        var imported = SettingsMigration.ImportLegacyConfig(target, legacy);

        Assert.False(imported);
        Assert.False(target.Exists(StateKeys.Config));
        Assert.False(new ConfigStore(target).Load().IsConfigured);
    }

    [Fact]
    public void Import_WithLiteralNullLegacyConfig_IsNoOp()
    {
        // A file whose content is the literal JSON `null` deserializes to null, not an error — import
        // is a no-op rather than writing a null document.
        var legacy = new JsonFileStateStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(legacy.PathFor(StateKeys.Config), "null");

        var target = new InMemoryStateStore();

        Assert.False(SettingsMigration.ImportLegacyConfig(target, legacy));
        Assert.False(target.Exists(StateKeys.Config));
    }

    [Fact]
    public void Import_LeavesLegacyFileInPlace_ForDowngrade()
    {
        var legacy = new JsonFileStateStore(_dir);
        new ConfigStore(legacy).Save(new AppConfig { WorkspaceId = "ws", PersonalTasksListId = "list" });
        var configPath = legacy.PathFor(StateKeys.Config);
        Assert.True(File.Exists(configPath));

        SettingsMigration.ImportLegacyConfig(new InMemoryStateStore(), legacy);

        // The old file must survive so a downgrade to the JSON backend still finds its settings.
        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public void Import_IsIdempotent_AndNeverClobbersTheTarget()
    {
        var legacy = new JsonFileStateStore(_dir);
        new ConfigStore(legacy).Save(new AppConfig { WorkspaceId = "old", PersonalTasksListId = "list" });

        var target = new InMemoryStateStore();

        // First import brings the legacy settings across.
        Assert.True(SettingsMigration.ImportLegacyConfig(target, legacy));

        // The user then changes a setting in the new backend.
        var targetStore = new ConfigStore(target);
        var updated = targetStore.Load();
        updated.WorkspaceId = "new";
        targetStore.Save(updated);

        // A second import (e.g. next launch) must be a no-op and must not resurrect the stale value.
        Assert.False(SettingsMigration.ImportLegacyConfig(target, legacy));
        Assert.Equal("new", targetStore.Load().WorkspaceId);
    }

    [Fact]
    public void Import_IntoRealLiteDbStore_PersistsAcrossReopen()
    {
        // End-to-end over the production pairing: a legacy config.json imported into a real
        // LiteDbStateStore lands in state.db and survives a subsequent app launch (fresh store,
        // same file). Uses a distinct data directory so it doesn't collide with a legacy config.json.
        var legacyDir = Path.Combine(_dir, "legacy");
        var legacy = new JsonFileStateStore(legacyDir);
        new ConfigStore(legacy).Save(new AppConfig { WorkspaceId = "ws-e2e", PersonalTasksListId = "list-e2e" });

        var dbPath = Path.Combine(_dir, "state.db");
        using (var target = new LiteDbStateStore(dbPath))
        {
            Assert.True(SettingsMigration.ImportLegacyConfig(target, legacy));
            // A second import over the same live store is a no-op (Config now present).
            Assert.False(SettingsMigration.ImportLegacyConfig(target, legacy));
        }

        using var reopened = new LiteDbStateStore(dbPath);
        var loaded = new ConfigStore(reopened).Load();
        Assert.Equal("ws-e2e", loaded.WorkspaceId);
        Assert.Equal("list-e2e", loaded.PersonalTasksListId);
        // Legacy file left in place for downgrade.
        Assert.True(File.Exists(legacy.PathFor(StateKeys.Config)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A minimal non-file <see cref="IStateStore"/> — stands in for the LiteDB target.</summary>
    private sealed class InMemoryStateStore : IStateStore
    {
        private readonly Dictionary<string, object> _values = [];

        public bool Exists(string key) => _values.ContainsKey(key);

        public T? Load<T>(string key) where T : class
            => _values.TryGetValue(key, out var v) ? v as T : null;

        public void Save<T>(string key, T value) where T : class => _values[key] = value;

        public void Delete(string key) => _values.Remove(key);
    }
}
