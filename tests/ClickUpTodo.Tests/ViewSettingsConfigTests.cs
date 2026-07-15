using System.Text.Json;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for persisting the F3 view (issue #19): the active filter/sort/group survives a
/// config round-trip, and enums are written as readable strings rather than ordinals.
/// </summary>
public sealed class ViewSettingsConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveThenLoad_RoundTripsView()
    {
        var store = new ConfigStore(_dir);
        var original = new AppConfig
        {
            // Already at the current schema, so Load() doesn't seed the default assignee rule — this
            // test isolates the view's save/load round-trip (migration is covered in ConfigMigrationsTests).
            SchemaVersion = ConfigMigrations.CurrentVersion,
            WorkspaceId = "1",
            PersonalTasksListId = "2",
            View = new ViewSettings
            {
                Filters =
                [
                    new FilterRule { Field = TaskField.Status, Op = FilterOp.Is, Value = "to do" },
                    new FilterRule { Field = TaskField.Due, Op = FilterOp.LessOrEqual, Value = "2026-07-01" },
                ],
                SortField = TaskField.LastActivity,
                SortDirection = SortDirection.Descending,
                GroupField = TaskField.List,
                Subtasks = SubtaskView.All,
            },
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(2, loaded.View.Filters.Count);
        Assert.Equal(TaskField.Status, loaded.View.Filters[0].Field);
        Assert.Equal(FilterOp.Is, loaded.View.Filters[0].Op);
        Assert.Equal("to do", loaded.View.Filters[0].Value);
        Assert.Equal(FilterOp.LessOrEqual, loaded.View.Filters[1].Op);
        Assert.Equal(TaskField.LastActivity, loaded.View.SortField);
        Assert.Equal(SortDirection.Descending, loaded.View.SortDirection);
        Assert.Equal(TaskField.List, loaded.View.GroupField);
        Assert.Equal(SubtaskView.All, loaded.View.Subtasks);
        // The read-only convenience getters follow from the enum.
        Assert.True(loaded.View.ShowSubtasks);
        Assert.True(loaded.View.ShowAllSubtasksOfAssignedParents);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSubtaskView_AsAString()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            SchemaVersion = ConfigMigrations.CurrentVersion, // isolate round-trip from the boolean migration
            View = new ViewSettings { Subtasks = SubtaskView.MineAndUnassigned },
        });

        var loaded = store.Load();
        Assert.Equal(SubtaskView.MineAndUnassigned, loaded.View.Subtasks);
        Assert.True(loaded.View.ShowSubtasks);
        Assert.False(loaded.View.ShowAllSubtasksOfAssignedParents);

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"MineAndUnassigned\"", json); // persisted by name, not ordinal
        // The legacy boolean shims are never re-persisted.
        Assert.DoesNotContain("showSubtasks", json);
        Assert.DoesNotContain("showAllSubtasksOfAssignedParents", json);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsCreatedField()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            SchemaVersion = ConfigMigrations.CurrentVersion, // isolate round-trip from the default-rule seed
            View = new ViewSettings
            {
                Filters = [new FilterRule { Field = TaskField.Created, Op = FilterOp.GreaterOrEqual, Value = "2026-06-01" }],
                SortField = TaskField.Created,
                GroupField = TaskField.Created,
            },
        });

        var loaded = store.Load();

        Assert.Equal(TaskField.Created, loaded.View.Filters[0].Field);
        Assert.Equal(TaskField.Created, loaded.View.SortField);
        Assert.Equal(TaskField.Created, loaded.View.GroupField);

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"Created\"", json); // persisted by name, not ordinal
    }

    /// <summary>The full seeded default view: Assignee IS me (#68) + a Status IS NOT rule per default
    /// excluded status (#69).</summary>
    private static List<FilterRule> DefaultFilters() =>
    [
        ViewSettings.DefaultAssigneeRule(),
        .. ViewSettings.DefaultExcludedStatuses.Select(ViewSettings.StatusIsNotRule),
    ];

    [Fact]
    public void FreshlyLoadedDefaultView_IsTheSeededDefault_AndIsDefault()
    {
        // A fresh config (no file) is migrated on load: the default view is Assignee IS me plus the
        // default Status IS NOT exclusions (#68 + #69), and that counts as the default view.
        var view = new ConfigStore(_dir).Load().View;

        Assert.True(view.IsDefault);
        var assignee = Assert.Single(view.Filters, r => r.Field == TaskField.Assignee);
        Assert.Equal(FilterOp.Is, assignee.Op);
        Assert.Equal(ViewSettings.CurrentUserToken, assignee.Value);
        Assert.Equal(
            ViewSettings.DefaultExcludedStatuses.OrderBy(s => s),
            view.Filters.Where(r => r.Field == TaskField.Status && r.Op == FilterOp.IsNot).Select(r => r.Value).OrderBy(s => s));
        Assert.Null(view.SortField);
        Assert.Null(view.GroupField);
        Assert.False(view.ShowSubtasks);
    }

    [Fact]
    public void SeededDefaultView_IsDefault_RegardlessOfFilterOrder()
    {
        Assert.True(new ViewSettings { Filters = DefaultFilters() }.IsDefault);
        // Order-independent: reversing the filter list is still the default.
        Assert.True(new ViewSettings { Filters = [.. Enumerable.Reverse(DefaultFilters())] }.IsDefault);
    }

    [Fact]
    public void ViewsThatDivergeFromTheSeededDefault_AreNotDefault()
    {
        Assert.False(new ViewSettings().IsDefault); // zero filters
        Assert.False(new ViewSettings { Filters = [ViewSettings.DefaultAssigneeRule()] }.IsDefault); // assignee alone (missing exclusions)
        Assert.False(new ViewSettings { Filters = DefaultFilters(), Subtasks = SubtaskView.MineAndUnassigned }.IsDefault);
        Assert.False(new ViewSettings { Filters = DefaultFilters(), Subtasks = SubtaskView.All }.IsDefault); // #179 F4 states
        Assert.False(new ViewSettings { Filters = DefaultFilters(), Completed = CompletedView.WithDone }.IsDefault); // #191 F12 states
        Assert.False(new ViewSettings { Filters = DefaultFilters(), Completed = CompletedView.All }.IsDefault);
        Assert.False(new ViewSettings { Filters = DefaultFilters(), GroupField = TaskField.List }.IsDefault);
        // An extra rule beyond the default set.
        Assert.False(new ViewSettings
        {
            Filters = [.. DefaultFilters(), new FilterRule { Field = TaskField.Status, Op = FilterOp.IsNot, Value = "done" }],
        }.IsDefault);
        // Right count, but a default exclusion swapped for a different status.
        Assert.False(new ViewSettings
        {
            Filters = [ViewSettings.DefaultAssigneeRule(), ViewSettings.StatusIsNotRule("won't do"), ViewSettings.StatusIsNotRule("done")],
        }.IsDefault);
    }

    [Fact]
    public void SavedConfig_PersistsEnumsAsStrings()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            View = new ViewSettings
            {
                SortField = TaskField.LastActivity,
                GroupField = TaskField.List,
                Completed = CompletedView.WithDone,
            },
        });

        var json = File.ReadAllText(store.ConfigPath);
        using var doc = JsonDocument.Parse(json);
        var view = doc.RootElement.GetProperty("view");

        Assert.Equal("LastActivity", view.GetProperty("sortField").GetString());
        Assert.Equal("List", view.GetProperty("groupField").GetString());
        Assert.Equal("WithDone", view.GetProperty("completed").GetString());
        // The legacy showCompleted shim is deserialize-only — never written back.
        Assert.False(view.TryGetProperty("showCompleted", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
