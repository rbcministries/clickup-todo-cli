using ClickUpTodo;

namespace ClickUpTodo.Tests;

public sealed class AppBrandingTests
{
    [Fact]
    public void DisplayName_IsClickUpSimpleCli()
    {
        Assert.Equal("ClickUp Simple CLI", AppBranding.DisplayName);
    }

    [Fact]
    public void DisplayName_DropsTheToDoNameThatClashedWithTheStatus()
    {
        // #20: "To-Do" clashed with the "to do" task status. The display name must not reintroduce it.
        Assert.DoesNotContain("To-Do", AppBranding.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("to do", AppBranding.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TasksSectionLabel_IsNeutral_NotTheToDoStatusWord()
    {
        Assert.Equal("TASKS", AppBranding.TasksSectionLabel);
        Assert.DoesNotContain("TO-DO", AppBranding.TasksSectionLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("to do", AppBranding.TasksSectionLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Acme Workspace", "ClickUp Simple CLI — Acme Workspace")]
    [InlineData("", "ClickUp Simple CLI — ")]
    public void WindowTitle_ComposesDisplayNameAndWorkspace(string workspace, string expected)
    {
        Assert.Equal(expected, AppBranding.WindowTitle(workspace));
    }

    [Fact]
    public void SetupHeading_DerivesFromDisplayName()
    {
        Assert.Equal("ClickUp Simple CLI — first-time setup", AppBranding.SetupHeading);
        Assert.StartsWith(AppBranding.DisplayName, AppBranding.SetupHeading);
    }
}
