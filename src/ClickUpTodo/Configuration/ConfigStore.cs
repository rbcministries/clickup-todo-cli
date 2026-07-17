namespace ClickUpTodo.Configuration;

/// <summary>
/// Typed accessor for the app's <see cref="AppConfig"/> settings document. It no longer touches the
/// disk directly — all persistence flows through the <see cref="IStateStore"/> seam (file-backed by
/// default via <see cref="JsonFileStateStore"/>; the #119 verdict LiteDB drops in later). This class
/// adds the config-specific concerns on top of the raw store: the <see cref="StateKeys.Config"/> key,
/// the unconfigured-default fallback, and schema migration on load.
/// </summary>
public sealed class ConfigStore
{
    private readonly IStateStore _store;

    /// <summary>Primary constructor — inject the persistence backend (composition root / tests).</summary>
    public ConfigStore(IStateStore store) => _store = store;

    /// <summary>
    /// Convenience constructor preserving the original file-backed behaviour: builds a
    /// <see cref="JsonFileStateStore"/> over <paramref name="directoryPath"/> (or the default dir).
    /// </summary>
    public ConfigStore(string? directoryPath = null) : this(new JsonFileStateStore(directoryPath)) { }

    /// <summary>The data directory, when the backend is file-based; empty otherwise.</summary>
    public string DirectoryPath => (_store as JsonFileStateStore)?.DirectoryPath ?? string.Empty;

    /// <summary>The <c>config.json</c> path, when the backend is file-based; empty otherwise.</summary>
    public string ConfigPath => (_store as JsonFileStateStore)?.PathFor(StateKeys.Config) ?? string.Empty;

    /// <summary>The shared data directory, used for both config and the encrypted token.</summary>
    public static string DefaultDirectory() => JsonFileStateStore.DefaultDirectory();

    public bool Exists() => _store.Exists(StateKeys.Config);

    public AppConfig Load()
    {
        var config = _store.Load<AppConfig>(StateKeys.Config) ?? new AppConfig();
        // Bring older (or freshly-created) configs up to the current schema — e.g. seed the default
        // Assignee rule (#68). Runs on the in-memory config; it's persisted on the next Save.
        ConfigMigrations.Apply(config);
        return config;
    }

    public void Save(AppConfig config)
    {
        try
        {
            _store.Save(StateKeys.Config, config);
        }
        catch
        {
            // A failed settings write (read-only / full disk, or a LiteDB contention error when a
            // second tab is writing #293) must never crash the UI action that triggered it (a pin
            // toggle, an F3 view change). The in-memory config lives on; the next save retries.
        }
    }

    /// <summary>Forget the persisted settings (used by <c>--reset</c>). Backend-agnostic.</summary>
    public void Delete() => _store.Delete(StateKeys.Config);
}
