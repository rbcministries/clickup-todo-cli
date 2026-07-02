using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the one-shot config migrations (#68): seeding the default <c>Assignee IS me</c> rule,
/// idempotency, and that a deliberately-cleared assignee rule isn't re-seeded once the config is at the
/// current schema version.
/// </summary>
public sealed class ConfigMigrationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Apply_FreshConfig_SeedsAssigneeMeRule_AndStampsVersion()
    {
        var config = new AppConfig(); // SchemaVersion 0, empty view

        ConfigMigrations.Apply(config);

        Assert.Equal(ConfigMigrations.CurrentVersion, config.SchemaVersion);
        var rule = Assert.Single(config.View.Filters);
        Assert.Equal(TaskField.Assignee, rule.Field);
        Assert.Equal(FilterOp.Is, rule.Op);
        Assert.Equal(ViewSettings.CurrentUserToken, rule.Value);
        Assert.True(config.View.IsDefault);
    }

    [Fact]
    public void Apply_PreservesExistingFilters_AndPrependsTheAssigneeRule()
    {
        var config = new AppConfig
        {
            View = new ViewSettings
            {
                Filters = [new FilterRule { Field = TaskField.Status, Op = FilterOp.IsNot, Value = "won't do" }],
            },
        };

        ConfigMigrations.Apply(config);

        Assert.Equal(2, config.View.Filters.Count);
        Assert.Equal(TaskField.Assignee, config.View.Filters[0].Field); // seeded rule leads
        Assert.Equal(TaskField.Status, config.View.Filters[1].Field);   // original preserved
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var config = new AppConfig();

        ConfigMigrations.Apply(config);
        ConfigMigrations.Apply(config);

        Assert.Single(config.View.Filters); // not duplicated
    }

    [Fact]
    public void Apply_DoesNotDuplicateAnExistingAssigneeRule()
    {
        var config = new AppConfig
        {
            View = new ViewSettings { Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "12345" }] },
        };

        ConfigMigrations.Apply(config);

        var rule = Assert.Single(config.View.Filters);
        Assert.Equal("12345", rule.Value); // the user's explicit assignee rule is kept as-is
    }

    [Fact]
    public void Apply_AlreadyCurrentVersionWithNoAssigneeRule_LeavesEveryoneAlone()
    {
        // A user who cleared the assignee rule (→ "everyone") and is already at the current schema must
        // NOT have the me-rule re-seeded on the next load.
        var config = new AppConfig { SchemaVersion = ConfigMigrations.CurrentVersion, View = new ViewSettings { Filters = [] } };

        ConfigMigrations.Apply(config);

        Assert.Empty(config.View.Filters);
    }

    [Fact]
    public void Load_LegacyConfigWithoutSchemaVersion_MigratesOnDisk()
    {
        // A pre-migration config.json (no schemaVersion, empty view) → loaded config is migrated.
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.ConfigPath, """{ "workspaceId": "1", "personalTasksListId": "2", "view": { "filters": [] } }""");

        var loaded = store.Load();

        Assert.Equal(ConfigMigrations.CurrentVersion, loaded.SchemaVersion);
        Assert.True(loaded.View.IsDefault);
        Assert.Equal(TaskField.Assignee, Assert.Single(loaded.View.Filters).Field);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
