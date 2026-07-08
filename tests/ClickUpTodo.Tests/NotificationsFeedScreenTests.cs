using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pins the pure, CI-testable surface of the feed screen scaffold (#110). The Terminal.Gui view is
/// not instantiated (the suite never calls <c>Application.Init</c>), matching the repo's pattern of
/// asserting only the framework-free logic of a screen.
/// </summary>
public sealed class NotificationsFeedScreenTests
{
    [Fact]
    public void EmptyStatePlaceholder_IsNonEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(NotificationsFeedScreen.EmptyStatePlaceholder));

    [Fact]
    public void EmptyStatePlaceholder_DescribesTheFeedAndTheWayBack()
    {
        var text = NotificationsFeedScreen.EmptyStatePlaceholder;

        Assert.Contains("mention", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comment", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Esc", text, StringComparison.Ordinal);
    }
}
