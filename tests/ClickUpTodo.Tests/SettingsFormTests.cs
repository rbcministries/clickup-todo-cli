using ClickUpTodo.Configuration;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

public sealed class SettingsFormTests
{
    // ── DescribeProviders (#547): the F10 read-only provider summary ──────────────────────────────

    [Fact]
    public void DescribeProviders_EmptyList_ReadsAsTheSingleBuiltInDefault()
        => Assert.Equal(
            $"1 provider · {AgentDispatchSettings.DefaultProviderDisplayName}",
            SettingsForm.DescribeProviders([], ""));

    [Fact]
    public void DescribeProviders_SingleProvider_NamesIt()
        => Assert.Equal(
            "1 provider · Claude",
            SettingsForm.DescribeProviders([new DispatchProvider { Name = "Claude", Executable = "claude" }], "Claude"));

    [Fact]
    public void DescribeProviders_MultipleProviders_CountsThemAndNamesTheDefault()
    {
        List<DispatchProvider> providers =
        [
            new() { Name = "Claude", Executable = "claude" },
            new() { Name = "Codex", Executable = "codex" },
        ];

        Assert.Equal("2 providers · default Codex", SettingsForm.DescribeProviders(providers, "Codex"));
    }

    [Fact]
    public void DescribeProviders_UnmatchedDefaultName_FallsBackToTheFirst()
    {
        List<DispatchProvider> providers =
        [
            new() { Name = "Claude", Executable = "claude" },
            new() { Name = "Codex", Executable = "codex" },
        ];

        Assert.Equal("2 providers · default Claude", SettingsForm.DescribeProviders(providers, "missing"));
    }

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

    // ── feed look-back window (#244) ────────────────────────────────────────────

    [Theory]
    [InlineData("0", 0)]        // 0 = disabled (fetch the full set)
    [InlineData("7", 7)]
    [InlineData("30", 30)]
    [InlineData("3650", 3650)]  // at max
    public void ParseLookbackDays_KeepsValidInRangeValues(string text, int expected)
        => Assert.Equal(expected, SettingsForm.ParseLookbackDays(text, fallback: 99));

    [Theory]
    [InlineData("-1", 0)]        // below min → clamped to 0 (off)
    [InlineData("-365", 0)]
    [InlineData("100000", 3650)] // above max → clamped down
    public void ParseLookbackDays_ClampsOutOfRangeValues(string text, int expected)
        => Assert.Equal(expected, SettingsForm.ParseLookbackDays(text, fallback: 99));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(null)]
    public void ParseLookbackDays_FallsBackWhenNotAnInteger(string? text)
        => Assert.Equal(14, SettingsForm.ParseLookbackDays(text, fallback: 14));

    [Fact]
    public void ParseLookbackDays_AcceptsWhitespacePaddedIntegers()
        => Assert.Equal(30, SettingsForm.ParseLookbackDays("  30  ", fallback: 99));

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
        // The tail is combined as a single segment (Path.Combine(home, "source/repos")), so the
        // expectation must mirror that exactly to stay correct on Windows too (where the '/' inside
        // the tail is left as-is rather than being treated as a segment separator).
        => Assert.Equal(Path.Combine(Home, "source/repos"), SettingsForm.ExpandHomePath("~/source/repos", Home));

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
