namespace ClickUpTodo.Configuration;

/// <summary>
/// One-shot, forward-only migrations applied to <see cref="AppConfig"/> as it's loaded, gated by
/// <see cref="AppConfig.SchemaVersion"/> so each runs exactly once. Pure (no I/O) so it's unit-testable
/// and callable from <see cref="ConfigStore.Load"/>.
/// </summary>
public static class ConfigMigrations
{
    /// <summary>The version an up-to-date config carries once all migrations have run.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Applies any migrations the config hasn't seen yet, then stamps it current.</summary>
    public static void Apply(AppConfig config)
    {
        // v1 (#68): assignee became a first-class filter field. Seed the default "Assignee IS me" rule
        // so an existing/blank view keeps reproducing the original "my tasks" fetch. Version-gated (not
        // "seed whenever absent") so a user who deliberately clears the assignee rule to see everyone
        // isn't re-seeded on the next load.
        if (config.SchemaVersion < 1)
            SeedDefaultAssigneeRule(config.View);

        config.SchemaVersion = CurrentVersion;
    }

    private static void SeedDefaultAssigneeRule(ViewSettings view)
    {
        if (!view.Filters.Any(r => r.Field == TaskField.Assignee))
            view.Filters.Insert(0, ViewSettings.DefaultAssigneeRule());
    }
}
