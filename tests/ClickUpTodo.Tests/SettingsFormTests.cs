using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

public sealed class SettingsFormTests
{
    [Theory]
    [InlineData("60", 60)]
    [InlineData("10", 10)]
    [InlineData("3600", 3600)]
    public void ParseRefreshSeconds_KeepsValidInRangeValues(string text, int expected)
        => Assert.Equal(expected, SettingsForm.ParseRefreshSeconds(text, fallback: 99));

    [Theory]
    [InlineData("5", 10)]      // below min → clamped up
    [InlineData("0", 10)]
    [InlineData("-30", 10)]
    [InlineData("100000", 3600)] // above max → clamped down
    public void ParseRefreshSeconds_ClampsOutOfRangeValues(string text, int expected)
        => Assert.Equal(expected, SettingsForm.ParseRefreshSeconds(text, fallback: 99));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(null)]
    public void ParseRefreshSeconds_FallsBackWhenNotAnInteger(string? text)
        => Assert.Equal(42, SettingsForm.ParseRefreshSeconds(text, fallback: 42));

    [Fact]
    public void CanAdd_AllowsANewNonBlankStatus()
        => Assert.True(SettingsForm.CanAdd(["cancelled"], "won't do"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanAdd_RejectsBlank(string? candidate)
        => Assert.False(SettingsForm.CanAdd([], candidate));

    [Fact]
    public void CanAdd_RejectsCaseInsensitiveDuplicate()
        => Assert.False(SettingsForm.CanAdd(["Cancelled"], "cancelled"));

    [Fact]
    public void CanAdd_ComparesAgainstTheTrimmedCandidate()
        => Assert.False(SettingsForm.CanAdd(["cancelled"], "  cancelled  "));
}
