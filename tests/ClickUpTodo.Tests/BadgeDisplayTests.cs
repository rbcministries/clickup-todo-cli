using System.Text.Json;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

public sealed class BadgeDisplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Next_CyclesIconsTextHidden_AndLoopsBack()
    {
        Assert.Equal(BadgeDisplay.Text, BadgeDisplay.Icons.Next());
        Assert.Equal(BadgeDisplay.Hidden, BadgeDisplay.Text.Next());
        Assert.Equal(BadgeDisplay.Icons, BadgeDisplay.Hidden.Next());
    }

    [Fact]
    public void Next_ThreePresses_ReturnToStart()
    {
        Assert.Equal(BadgeDisplay.Icons, BadgeDisplay.Icons.Next().Next().Next());
    }

    [Theory]
    [InlineData(BadgeDisplay.Icons)]
    [InlineData(BadgeDisplay.Text)]
    [InlineData(BadgeDisplay.Hidden)]
    public void Describe_MentionsF6(BadgeDisplay mode) => Assert.Contains("F6", mode.Describe());

    [Fact]
    public void DefaultConfig_UsesIcons()
        => Assert.Equal(BadgeDisplay.Icons, new AppConfig().BadgeDisplay);

    [Fact]
    public void SaveThenLoad_RoundTripsBadgeDisplay_AsString()
    {
        var store = new ConfigStore(_dir);
        store.Save(new AppConfig { SchemaVersion = ConfigMigrations.CurrentVersion, BadgeDisplay = BadgeDisplay.Hidden });

        Assert.Equal(BadgeDisplay.Hidden, store.Load().BadgeDisplay);

        // Persisted by name, not ordinal.
        var json = File.ReadAllText(store.ConfigPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Hidden", doc.RootElement.GetProperty("badgeDisplay").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
