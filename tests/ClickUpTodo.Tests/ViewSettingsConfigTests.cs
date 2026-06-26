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
    }

    [Fact]
    public void DefaultView_IsEmptyAndDefault()
    {
        var view = new AppConfig().View;

        Assert.True(view.IsDefault);
        Assert.Empty(view.Filters);
        Assert.Null(view.SortField);
        Assert.Null(view.GroupField);
    }

    [Fact]
    public void SavedConfig_PersistsEnumsAsStrings()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig
        {
            View = new ViewSettings { SortField = TaskField.LastActivity, GroupField = TaskField.List },
        });

        var json = File.ReadAllText(store.ConfigPath);
        using var doc = JsonDocument.Parse(json);
        var view = doc.RootElement.GetProperty("view");

        Assert.Equal("LastActivity", view.GetProperty("sortField").GetString());
        Assert.Equal("List", view.GetProperty("groupField").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
