using System.Text.Json;

namespace ClickUpTodo.Configuration;

/// <summary>
/// Typed accessor for the app's <see cref="AppConfig"/> settings document. It no longer touches the
/// disk directly — all persistence flows through the <see cref="IStateStore"/> seam (file-backed by
/// default via <see cref="JsonFileStateStore"/>; the #119 verdict LiteDB drops in later). This class
/// adds the config-specific concerns on top of the raw store: the <see cref="StateKeys.Config"/> key,
/// the unconfigured-default fallback, and schema migration on load.
/// <para>
/// It also holds the last-synced <b>baseline</b> so a save can three-way merge against a fresh disk
/// read (#293): with multiple tabs writing the shared store, a whole-document rewrite would otherwise
/// clobber a field another tab changed after this one loaded. <see cref="Save"/> re-reads, merges
/// per-field (last-writer-wins on a fresh read — see <see cref="ConfigMerge"/>), and swallows a failed
/// write so it never crashes the triggering UI action.
/// </para>
/// </summary>
public sealed class ConfigStore
{
    private readonly IStateStore _store;

    // A clone of the config as this process last synced it (set on Load, refreshed after each Save).
    // The merge uses it to tell which top-level fields this process changed vs. left untouched. Null
    // until the first Load/Save, in which case Save writes the config as-is (nothing to merge against).
    private AppConfig? _baseline;

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
        _baseline = Clone(config);
        return config;
    }

    public void Save(AppConfig config)
    {
        var toWrite = MergeWithDisk(config);
        try
        {
            _store.Save(StateKeys.Config, toWrite);
        }
        catch
        {
            // A failed settings write (read-only / full disk, or a LiteDB contention error when a
            // second tab is writing #293) must never crash the UI action that triggered it (a pin
            // toggle, an F3 view change). The in-memory config lives on and the baseline is NOT advanced
            // — so the un-persisted change is still seen as a local change and retried on the next save.
            return;
        }
        // Only after a successful write: baseline mirrors THIS process's in-memory config (not the
        // merged doc), so only a genuine future local edit reads as a change; a field another tab owns
        // stays deferred to disk.
        _baseline = Clone(config);
    }

    /// <summary>
    /// Three-way merges <paramref name="config"/> over the freshly re-read on-disk config so a
    /// concurrent tab's field change isn't clobbered (#293). Returns <paramref name="config"/> unchanged
    /// when there's nothing to merge against — no baseline yet, or no on-disk document — or when the
    /// re-read/merge fails for any reason (a torn write, a LiteDB contention error, a corrupt document):
    /// this is called before the write, outside its try/catch, so it must never throw out of
    /// <see cref="Save"/> and crash the caller's UI action. Falling back to writing our own config is
    /// the safe best-effort degradation.
    /// </summary>
    private AppConfig MergeWithDisk(AppConfig config)
    {
        if (_baseline is null)
            return config;

        try
        {
            var onDisk = _store.Load<AppConfig>(StateKeys.Config);
            if (onDisk is null)
                return config;

            // Normalise the on-disk copy to the current schema before merging so a legacy shim (e.g. the
            // pre-#69 excludedStatuses array, dropped by migration) can't resurrect through the merge.
            ConfigMigrations.Apply(onDisk);

            var mergedJson = ConfigMerge.ThreeWay(Serialize(_baseline), Serialize(config), Serialize(onDisk));
            return JsonSerializer.Deserialize<AppConfig>(mergedJson, StateJson.Options) ?? config;
        }
        catch
        {
            return config;
        }
    }

    private static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, StateJson.Options);

    private static AppConfig Clone(AppConfig config)
        => JsonSerializer.Deserialize<AppConfig>(Serialize(config), StateJson.Options)!;

    /// <summary>Forget the persisted settings (used by <c>--reset</c>). Backend-agnostic.</summary>
    public void Delete()
    {
        _store.Delete(StateKeys.Config);
        _baseline = null; // nothing on disk to merge against after a reset.
    }
}
