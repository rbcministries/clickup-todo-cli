using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Covers the "how long ago" formatter backing the cache-staleness markers (#124): the unit boundaries
/// (minute / hour / day), the sub-minute "just now" floor, and negative ages from minor clock skew.
/// </summary>
public sealed class RelativeTimeTests
{
    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "just now")]        // 1 second
    [InlineData(59, "just now")]       // still under a minute
    [InlineData(60, "1m ago")]         // exactly one minute
    [InlineData(90, "1m ago")]         // truncates to whole minutes
    [InlineData(59 * 60, "59m ago")]   // just under an hour
    [InlineData(60 * 60, "1h ago")]    // exactly one hour
    [InlineData(90 * 60, "1h ago")]    // truncates to whole hours
    [InlineData(23 * 3600, "23h ago")] // just under a day
    [InlineData(24 * 3600, "1d ago")]  // exactly one day
    [InlineData(50 * 3600, "2d ago")]  // truncates to whole days
    public void Format_RendersTheLargestWholeUnit(int seconds, string expected)
        => Assert.Equal(expected, RelativeTime.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Format_TreatsNegativeAge_AsJustNow()
    {
        // A cache captured a moment "in the future" (system clock nudged backwards) must not render a
        // nonsensical negative label — it floors to "just now".
        Assert.Equal("just now", RelativeTime.Format(TimeSpan.FromSeconds(-30)));
    }
}
