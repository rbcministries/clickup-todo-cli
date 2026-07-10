namespace ClickUpTodo.Configuration;

/// <summary>
/// One-time import of the legacy file-backed settings (<c>config.json</c>) into the chosen backend
/// (LiteDB, #121) so upgrading users keep their workspace/list/view/agent settings without
/// re-running setup. The legacy file is left untouched so a downgrade to the JSON backend still
/// finds its settings.
/// </summary>
public static class SettingsMigration
{
    /// <summary>
    /// Copy the legacy settings document into <paramref name="target"/> when — and only when — the
    /// target has no settings yet and the legacy store has some. Idempotent: safe to call on every
    /// launch. Once the target holds a settings document (imported or written by the app), this is a
    /// no-op, so a later edit in the new backend is never clobbered by a stale <c>config.json</c>.
    /// </summary>
    /// <param name="target">The live backend (LiteDB) settings should end up in.</param>
    /// <param name="legacy">The file backend to import any existing <c>config.json</c> from.</param>
    /// <returns><see langword="true"/> if a settings document was imported; otherwise <see langword="false"/>.</returns>
    public static bool ImportLegacyConfig(IStateStore target, JsonFileStateStore legacy)
    {
        // Already migrated (or the app has since written its own settings) — never overwrite.
        if (target.Exists(StateKeys.Config))
            return false;

        // Fresh install — nothing to bring across.
        var legacyConfig = legacy.Load<AppConfig>(StateKeys.Config);
        if (legacyConfig is null)
            return false;

        // Copy the settings document as-stored; its SchemaVersion is preserved and ConfigMigrations
        // still runs later on ConfigStore.Load.
        target.Save(StateKeys.Config, legacyConfig);
        return true;
    }
}
