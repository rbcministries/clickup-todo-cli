using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the one-shot config migrations: seeding the default <c>Assignee IS me</c> rule (#68)
/// and migrating the legacy excluded-statuses setting to <c>Status IS NOT</c> filter rules (#69),
/// including the absent/empty/present-legacy mapping, de-dup, case-insensitivity, idempotency, and that
/// the legacy <c>excludedStatuses</c> key stops being persisted.
/// </summary>
public sealed class ConfigMigrationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    private static IReadOnlyList<FilterRule> StatusIsNotRules(ViewSettings view) =>
        [.. view.Filters.Where(r => r.Field == TaskField.Status && r.Op == FilterOp.IsNot)];

    [Fact]
    public void Apply_FreshConfig_SeedsAssigneeMeRule_AndDefaultStatusExclusions_AndStampsVersion()
    {
        var config = new AppConfig(); // SchemaVersion 0, empty view, no legacy exclusions (null)

        ConfigMigrations.Apply(config);

        Assert.Equal(ConfigMigrations.CurrentVersion, config.SchemaVersion);
        // Default view = Assignee IS me (#68) + one Status IS NOT rule per default excluded status (#69).
        var assignee = Assert.Single(config.View.Filters, r => r.Field == TaskField.Assignee);
        Assert.Equal(FilterOp.Is, assignee.Op);
        Assert.Equal(ViewSettings.CurrentUserToken, assignee.Value);
        Assert.Equal(
            ViewSettings.DefaultExcludedStatuses.OrderBy(s => s),
            StatusIsNotRules(config.View).Select(r => r.Value).OrderBy(s => s));
        Assert.True(config.View.IsDefault);
    }

    [Fact]
    public void Apply_NullAgentDispatch_CoalescedToDefaults()
    {
        // A hand-edited/corrupted config.json with "AgentDispatch": null deserializes to a null (the
        // property's `= new()` default only fills a missing key). Dispatch settings are read at startup
        // (#91), so Apply must normalize it back to defaults rather than leave a null to fault the launch.
        var config = new AppConfig { AgentDispatch = null! };

        ConfigMigrations.Apply(config);

        Assert.NotNull(config.AgentDispatch);
        Assert.True(config.AgentDispatch.IsDefault);
    }

    [Fact]
    public void Apply_NullTaskWorkingDirectories_CoalescedToEmptyMap()
    {
        // A hand-edited config.json with "taskWorkingDirectories": null deserializes to null (the
        // property's `= []` default only fills a missing key). The #96 pre-fill/update call sites
        // dereference it, so Apply must normalize it back to an empty map.
        var config = new AppConfig { TaskWorkingDirectories = null! };

        ConfigMigrations.Apply(config);

        Assert.NotNull(config.TaskWorkingDirectories);
        Assert.Empty(config.TaskWorkingDirectories);
    }

    [Fact]
    public void Apply_AbsentLegacyField_SeedsDefaultExclusions()
    {
        // A config that never carried excludedStatuses (null) is treated as a fresh install: seed the
        // defaults so today's "hide won't do / cancelled" behaviour is preserved under the filter model.
        var config = new AppConfig { LegacyExcludedStatuses = null };

        ConfigMigrations.Apply(config);

        Assert.Equal(["won't do", "cancelled"], StatusIsNotRules(config.View).Select(r => r.Value));
    }

    [Fact]
    public void Apply_EmptyLegacyList_SeedsNoExclusions()
    {
        // An empty array means the user deliberately cleared their exclusions — seed nothing, so only
        // the assignee rule remains. (This is why the shim is nullable: absent != empty.)
        var config = new AppConfig { LegacyExcludedStatuses = [] };

        ConfigMigrations.Apply(config);

        Assert.Empty(StatusIsNotRules(config.View));
        Assert.Equal(TaskField.Assignee, Assert.Single(config.View.Filters).Field);
    }

    [Fact]
    public void Apply_PresentLegacyList_MigratesEachEntry()
    {
        // The acceptance example: excludedStatuses: ["qa", "won't do"] → two Status IS NOT rules.
        var config = new AppConfig { LegacyExcludedStatuses = ["qa", "won't do"] };

        ConfigMigrations.Apply(config);

        Assert.Equal(["qa", "won't do"], StatusIsNotRules(config.View).Select(r => r.Value));
    }

    [Fact]
    public void Apply_TrimsMigratedValues_AndSkipsBlankEntries()
    {
        var config = new AppConfig { LegacyExcludedStatuses = ["  qa  ", "   ", ""] };

        ConfigMigrations.Apply(config);

        Assert.Equal(["qa"], StatusIsNotRules(config.View).Select(r => r.Value)); // trimmed; blanks dropped
    }

    [Fact]
    public void Apply_WhitespaceVariantOfTheSameStatus_IsNotDuplicated()
    {
        // "won't do" and "  won't do  " are the same status once trimmed — only one rule should result.
        var config = new AppConfig { LegacyExcludedStatuses = ["won't do", "  won't do  "] };

        ConfigMigrations.Apply(config);

        Assert.Equal(["won't do"], StatusIsNotRules(config.View).Select(r => r.Value));
    }

    [Fact]
    public void Apply_DoesNotDuplicateAStatusRuleAlreadyPresent_CaseInsensitively()
    {
        // The user already has a hand-added "Status IS NOT WON'T DO"; migrating the legacy "won't do"
        // must not add a second, case-insensitively-equal rule.
        var config = new AppConfig
        {
            View = new ViewSettings { Filters = [new FilterRule { Field = TaskField.Status, Op = FilterOp.IsNot, Value = "WON'T DO" }] },
            LegacyExcludedStatuses = ["won't do", "cancelled"],
        };

        ConfigMigrations.Apply(config);

        Assert.Equal(["WON'T DO", "cancelled"], StatusIsNotRules(config.View).Select(r => r.Value));
    }

    [Fact]
    public void Apply_PreservesUnrelatedExistingFilters()
    {
        var config = new AppConfig
        {
            View = new ViewSettings { Filters = [new FilterRule { Field = TaskField.Status, Op = FilterOp.IsNot, Value = "qa" }] },
            // null legacy → seed the defaults alongside the user's pre-existing "qa" exclusion.
        };

        ConfigMigrations.Apply(config);

        Assert.Equal(TaskField.Assignee, config.View.Filters[0].Field); // seeded assignee leads
        Assert.Equal(["qa", "won't do", "cancelled"], StatusIsNotRules(config.View).Select(r => r.Value));
    }

    [Fact]
    public void Apply_ExistingV1User_MigratesTheirSavedExclusions()
    {
        // A post-#68 config (schema 1) still carries excludedStatuses on disk (it was a live property);
        // the assignee rule is already seeded. v2 migrates the saved exclusions in place.
        var config = new AppConfig
        {
            SchemaVersion = 1,
            View = new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] },
            LegacyExcludedStatuses = ["won't do", "cancelled"],
        };

        ConfigMigrations.Apply(config);

        Assert.Equal(ConfigMigrations.CurrentVersion, config.SchemaVersion);
        Assert.Equal(["won't do", "cancelled"], StatusIsNotRules(config.View).Select(r => r.Value));
        Assert.True(config.View.IsDefault);
    }

    [Fact]
    public void Apply_NullsTheLegacyShim_SoItIsNotReMigrated()
    {
        var config = new AppConfig { LegacyExcludedStatuses = ["qa"] };

        ConfigMigrations.Apply(config);
        Assert.Null(config.LegacyExcludedStatuses); // dropped after one-shot migration

        // Re-running finds no legacy field and, being at the current version, adds nothing.
        var before = config.View.Filters.Count;
        ConfigMigrations.Apply(config);
        Assert.Equal(before, config.View.Filters.Count);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var config = new AppConfig();

        ConfigMigrations.Apply(config);
        var count = config.View.Filters.Count;
        ConfigMigrations.Apply(config);

        Assert.Equal(count, config.View.Filters.Count); // not duplicated
    }

    [Fact]
    public void Apply_DoesNotDuplicateAnExistingAssigneeRule()
    {
        var config = new AppConfig
        {
            View = new ViewSettings { Filters = [new FilterRule { Field = TaskField.Assignee, Op = FilterOp.Is, Value = "12345" }] },
            LegacyExcludedStatuses = [], // isolate the assignee behaviour from status seeding
        };

        ConfigMigrations.Apply(config);

        var rule = Assert.Single(config.View.Filters);
        Assert.Equal("12345", rule.Value); // the user's explicit assignee rule is kept as-is
    }

    [Fact]
    public void Apply_AlreadyCurrentVersion_LeavesEverythingAlone()
    {
        // A user who cleared both the assignee rule (→ "everyone") and all exclusions and is already at
        // the current schema must NOT have anything re-seeded on the next load.
        var config = new AppConfig { SchemaVersion = ConfigMigrations.CurrentVersion, View = new ViewSettings { Filters = [] } };

        ConfigMigrations.Apply(config);

        Assert.Empty(config.View.Filters);
    }

    [Fact]
    public void Load_LegacyConfigWithExcludedStatuses_MigratesOnDisk_AndDropsTheKey()
    {
        // A pre-migration config.json (no schemaVersion, with excludedStatuses) is migrated on load;
        // once saved, the excludedStatuses key is gone and the exclusions live as Status IS NOT rules.
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.ConfigPath,
            """{ "workspaceId": "1", "personalTasksListId": "2", "excludedStatuses": ["qa", "won't do"], "view": { "filters": [] } }""");

        var loaded = store.Load();

        Assert.Equal(ConfigMigrations.CurrentVersion, loaded.SchemaVersion);
        Assert.Null(loaded.LegacyExcludedStatuses);
        Assert.Equal(["qa", "won't do"], StatusIsNotRules(loaded.View).Select(r => r.Value));
        Assert.Equal(TaskField.Assignee, Assert.Single(loaded.View.Filters, r => r.Field == TaskField.Assignee).Field);

        store.Save(loaded);
        var json = File.ReadAllText(store.ConfigPath);
        Assert.DoesNotContain("excludedStatuses", json);
    }

    // ── v3 (#100): PromptPreamble → PromptTemplate ──────────────────────────────────

    [Fact]
    public void Apply_LegacyPromptPreamble_IsCarriedForwardIntoTemplate_AndKeyDropped()
    {
        // A saved (and, since #91, live) single-line preamble must not be silently lost: it migrates
        // into the equivalent full template (the default with its preamble line swapped).
        var config = new AppConfig
        {
            SchemaVersion = 2,
            AgentDispatch = new AgentDispatchSettings { LegacyPromptPreamble = "Only use the JSON." },
        };

        ConfigMigrations.Apply(config);

        Assert.Equal(ConfigMigrations.CurrentVersion, config.SchemaVersion);
        Assert.Equal(AgentPromptComposer.DefaultTemplateWithPreamble("Only use the JSON."), config.AgentDispatch.PromptTemplate);
        Assert.Null(config.AgentDispatch.LegacyPromptPreamble); // dropped after one-shot migration
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_BlankLegacyPreamble_SeedsNoTemplate_ButDropsTheKey(string? legacy)
    {
        var config = new AppConfig
        {
            SchemaVersion = 2,
            AgentDispatch = new AgentDispatchSettings { LegacyPromptPreamble = legacy },
        };

        ConfigMigrations.Apply(config);

        Assert.Equal("", config.AgentDispatch.PromptTemplate); // blank ⇒ still the default template
        Assert.True(config.AgentDispatch.IsDefault);
        Assert.Null(config.AgentDispatch.LegacyPromptPreamble);
    }

    [Fact]
    public void Apply_ExistingTemplate_IsNotOverwrittenByLegacyPreamble()
    {
        // If a user somehow has both, the explicit template wins; the legacy shim is still dropped.
        var config = new AppConfig
        {
            SchemaVersion = 2,
            AgentDispatch = new AgentDispatchSettings
            {
                PromptTemplate = "MY TEMPLATE {userPrompt}",
                LegacyPromptPreamble = "Only use the JSON.",
            },
        };

        ConfigMigrations.Apply(config);

        Assert.Equal("MY TEMPLATE {userPrompt}", config.AgentDispatch.PromptTemplate);
        Assert.Null(config.AgentDispatch.LegacyPromptPreamble);
    }

    [Fact]
    public void Apply_AlreadyV3_WithStrayPreambleKey_DropsIt_WithoutMigrating()
    {
        // A hand-added promptPreamble on an already-v3 config isn't migrated (version-gated), but the
        // deserialize-only shim is still nulled so it stops being persisted.
        var config = new AppConfig
        {
            SchemaVersion = 3,
            AgentDispatch = new AgentDispatchSettings { LegacyPromptPreamble = "stray" },
        };

        ConfigMigrations.Apply(config);

        Assert.Equal("", config.AgentDispatch.PromptTemplate); // not migrated (already current)
        Assert.Null(config.AgentDispatch.LegacyPromptPreamble); // but dropped
    }

    [Fact]
    public void Load_LegacyConfigWithPromptPreamble_MigratesOnDisk_AndDropsTheKey()
    {
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.ConfigPath,
            """{ "schemaVersion": 2, "workspaceId": "1", "personalTasksListId": "2", "agentDispatch": { "promptPreamble": "Only use the JSON." }, "view": { "filters": [] } }""");

        var loaded = store.Load();

        Assert.Equal(ConfigMigrations.CurrentVersion, loaded.SchemaVersion);
        Assert.Null(loaded.AgentDispatch.LegacyPromptPreamble);
        Assert.Equal(AgentPromptComposer.DefaultTemplateWithPreamble("Only use the JSON."), loaded.AgentDispatch.PromptTemplate);

        store.Save(loaded);
        var json = File.ReadAllText(store.ConfigPath);
        Assert.DoesNotContain("promptPreamble", json);
        Assert.Contains("promptTemplate", json);
    }

    // ── v4 (#179): ShowSubtasks/ShowAllSubtasksOfAssignedParents booleans → SubtaskView enum ──────────

    [Theory]
    [InlineData(null, null, SubtaskView.Hidden)]      // fresh install / never carried the keys
    [InlineData(false, null, SubtaskView.Hidden)]     // explicitly off
    [InlineData(false, true, SubtaskView.Hidden)]     // off wins even if the #70 flag lingered
    [InlineData(true, null, SubtaskView.MineAndUnassigned)] // on, #70 absent → new default on-state
    [InlineData(true, false, SubtaskView.MineAndUnassigned)] // on, #70 off
    [InlineData(true, true, SubtaskView.All)]         // on + #70 → all
    public void Apply_MapsLegacySubtaskBooleans_OntoEnum(bool? legacyShow, bool? legacyAll, SubtaskView expected)
    {
        var config = new AppConfig
        {
            View = new ViewSettings { LegacyShowSubtasks = legacyShow, LegacyShowAllSubtasks = legacyAll },
        };

        ConfigMigrations.Apply(config);

        Assert.Equal(expected, config.View.Subtasks);
        Assert.Null(config.View.LegacyShowSubtasks);   // shims dropped after the one-shot migration
        Assert.Null(config.View.LegacyShowAllSubtasks);
    }

    [Fact]
    public void Apply_AlreadyV4_WithStraySubtaskBooleans_DropsThem_WithoutMigrating()
    {
        // A hand-added showSubtasks on an already-v4 config isn't migrated (version-gated), but the
        // deserialize-only shims are still nulled so they stop being persisted.
        var config = new AppConfig
        {
            SchemaVersion = 4,
            View = new ViewSettings { Subtasks = SubtaskView.Hidden, LegacyShowSubtasks = true, LegacyShowAllSubtasks = true },
        };

        ConfigMigrations.Apply(config);

        Assert.Equal(SubtaskView.Hidden, config.View.Subtasks); // not migrated (already current)
        Assert.Null(config.View.LegacyShowSubtasks);
        Assert.Null(config.View.LegacyShowAllSubtasks);
    }

    [Fact]
    public void Load_LegacyConfigWithSubtaskBooleans_MigratesOnDisk_AndDropsTheKeys()
    {
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.ConfigPath,
            """{ "schemaVersion": 3, "workspaceId": "1", "personalTasksListId": "2", "view": { "filters": [], "showSubtasks": true, "showAllSubtasksOfAssignedParents": true } }""");

        var loaded = store.Load();

        Assert.Equal(ConfigMigrations.CurrentVersion, loaded.SchemaVersion);
        Assert.Equal(SubtaskView.All, loaded.View.Subtasks);
        Assert.Null(loaded.View.LegacyShowSubtasks);
        Assert.Null(loaded.View.LegacyShowAllSubtasks);

        store.Save(loaded);
        var json = File.ReadAllText(store.ConfigPath);
        Assert.DoesNotContain("showSubtasks", json);
        Assert.DoesNotContain("showAllSubtasksOfAssignedParents", json);
        Assert.Contains("\"All\"", json); // the enum persists by name
    }

    [Fact]
    public void Load_FreshConfig_SeedsDefaultsAndIsDefault()
    {
        // No config file at all → a fresh install is migrated on load to the full default view.
        var loaded = new ConfigStore(_dir).Load();

        Assert.True(loaded.View.IsDefault);
        Assert.Equal(["won't do", "cancelled"], StatusIsNotRules(loaded.View).Select(r => r.Value));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
