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
    public void ParseRefreshSeconds_AcceptsWhitespacePaddedIntegers()
        => Assert.Equal(60, SettingsForm.ParseRefreshSeconds("  60  ", fallback: 99));

    // ── agent-dispatch extra args (#27) ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseExtraArgs_BlankYieldsEmpty(string? text)
        => Assert.Empty(SettingsForm.ParseExtraArgs(text));

    [Fact]
    public void ParseExtraArgs_SplitsOnWhitespaceAndDropsBlanks()
        => Assert.Equal(["--model", "opus", "--verbose"], SettingsForm.ParseExtraArgs("  --model   opus\t--verbose "));

    [Fact]
    public void FormatExtraArgs_JoinsWithSpaces()
        => Assert.Equal("--model opus", SettingsForm.FormatExtraArgs(["--model", "opus"]));

    [Fact]
    public void FormatExtraArgs_SkipsBlankEntries()
        => Assert.Equal("--model opus", SettingsForm.FormatExtraArgs(["--model", "  ", "opus"]));

    [Fact]
    public void ExtraArgs_RoundTripThroughFormatThenParse()
    {
        string[] args = ["--model", "opus", "--dangerously-skip-permissions"];
        Assert.Equal(args, SettingsForm.ParseExtraArgs(SettingsForm.FormatExtraArgs(args)));
    }

    // ── base working directory (#92) ────────────────────────────────────────────

    private const string Home = "/home/tester";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExpandHomePath_BlankYieldsEmpty(string? text)
        => Assert.Equal("", SettingsForm.ExpandHomePath(text, Home));

    [Fact]
    public void ExpandHomePath_BareTildeIsHome()
        => Assert.Equal(Home, SettingsForm.ExpandHomePath("~", Home));

    [Fact]
    public void ExpandHomePath_TildeSlashExpandsUnderHome()
        => Assert.Equal(Path.Combine(Home, "source", "repos"), SettingsForm.ExpandHomePath("~/source/repos", Home));

    [Fact]
    public void ExpandHomePath_TildeBackslashExpandsUnderHome()
        => Assert.Equal(Path.Combine(Home, "source\\repos"), SettingsForm.ExpandHomePath("~\\source\\repos", Home));

    [Fact]
    public void ExpandHomePath_AbsolutePathPassesThroughUnchanged()
        => Assert.Equal("/opt/work", SettingsForm.ExpandHomePath("/opt/work", Home));

    [Fact]
    public void ExpandHomePath_TrimsSurroundingWhitespace()
        => Assert.Equal("/opt/work", SettingsForm.ExpandHomePath("  /opt/work  ", Home));

    [Fact]
    public void ExpandHomePath_MidStringTildeIsNotExpanded()
        => Assert.Equal("/opt/~/work", SettingsForm.ExpandHomePath("/opt/~/work", Home));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDefaultWorkingDirectory_BlankFallsBackToClickUpTasks(string? stored)
        => Assert.Equal(
            Path.Combine(Home, SettingsForm.DefaultWorkingDirectoryFolderName),
            SettingsForm.ResolveDefaultWorkingDirectory(stored, Home));

    [Fact]
    public void ResolveDefaultWorkingDirectory_ExpandsTildeInStoredValue()
        => Assert.Equal(Path.Combine(Home, "repos"), SettingsForm.ResolveDefaultWorkingDirectory("~/repos", Home));

    [Fact]
    public void ResolveDefaultWorkingDirectory_PassesAbsoluteStoredValueThrough()
        => Assert.Equal("/opt/work", SettingsForm.ResolveDefaultWorkingDirectory("/opt/work", Home));
}
