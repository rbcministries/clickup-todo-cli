using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

public sealed class StatusBadgeColorTests
{
    [Theory]
    [InlineData("#87909e", 0x87, 0x90, 0x9e)]
    [InlineData("87909e", 0x87, 0x90, 0x9e)]
    [InlineData("  #FF0000  ", 255, 0, 0)]
    [InlineData("#abc", 0xaa, 0xbb, 0xcc)] // 3-digit shorthand expands
    public void TryParseHex_ParsesValidColors(string hex, int r, int g, int b)
    {
        Assert.True(StatusBadgeColor.TryParseHex(hex, out var pr, out var pg, out var pb));
        Assert.Equal((r, g, b), (pr, pg, pb));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12")]      // wrong length
    [InlineData("#12345")]   // wrong length
    [InlineData("#xyzxyz")]  // non-hex
    [InlineData("nope")]
    public void TryParseHex_RejectsInvalidColors(string? hex)
    {
        Assert.False(StatusBadgeColor.TryParseHex(hex, out var r, out var g, out var b));
        Assert.Equal((0, 0, 0), (r, g, b));
    }

    [Fact]
    public void RelativeLuminance_IsZeroForBlack_AndOneForWhite()
    {
        Assert.Equal(0.0, StatusBadgeColor.RelativeLuminance(0, 0, 0), 3);
        Assert.Equal(1.0, StatusBadgeColor.RelativeLuminance(255, 255, 255), 3);
    }

    [Fact]
    public void RelativeLuminance_GreenContributesMoreThanRedOrBlue()
    {
        var green = StatusBadgeColor.RelativeLuminance(0, 255, 0);
        var red = StatusBadgeColor.RelativeLuminance(255, 0, 0);
        var blue = StatusBadgeColor.RelativeLuminance(0, 0, 255);
        Assert.True(green > red);
        Assert.True(red > blue);
    }

    [Fact]
    public void ContrastRatio_BlackOnWhite_IsMaximal()
    {
        var ratio = StatusBadgeColor.ContrastRatio(
            StatusBadgeColor.RelativeLuminance(0, 0, 0),
            StatusBadgeColor.RelativeLuminance(255, 255, 255));
        Assert.Equal(21.0, ratio, 1);
    }

    [Theory]
    [InlineData(255, 255, 255, true)]  // white bg -> dark text
    [InlineData(255, 255, 0, true)]    // yellow bg -> dark text
    [InlineData(0, 0, 0, false)]       // black bg -> light text
    [InlineData(0, 0, 128, false)]     // navy bg -> light text
    [InlineData(0x87, 0x90, 0x9e, true)]  // ClickUp default grey (L≈0.28) -> dark text reads better
    public void PreferDarkText_PicksHigherContrastForeground(int r, int g, int b, bool expectDark)
    {
        Assert.Equal(expectDark, StatusBadgeColor.PreferDarkText(r, g, b));
    }

    [Fact]
    public void PreferDarkText_AgreesWithContrastComparison()
    {
        // Cross-check the decision against the raw WCAG contrast ratios for a mid-tone color.
        const int r = 100, g = 150, b = 200;
        var bg = StatusBadgeColor.RelativeLuminance(r, g, b);
        var withBlack = StatusBadgeColor.ContrastRatio(bg, 0.0);
        var withWhite = StatusBadgeColor.ContrastRatio(bg, 1.0);
        Assert.Equal(withBlack >= withWhite, StatusBadgeColor.PreferDarkText(r, g, b));
    }

    // The same pure helper backs the priority badge (#55): a ClickUp priority hex color maps to a
    // readable black/white foreground exactly as status colors do. These pin the canonical palette.
    [Theory]
    [InlineData("#f50000", true)]  // Urgent bright red (L≈0.19) — black reads (marginally) better
    [InlineData("#ffcc00", true)]  // High amber — dark text
    [InlineData("#6fddff", true)]  // Normal light blue — dark text
    [InlineData("#d8d8d8", true)]  // Low light grey — dark text
    [InlineData("#e50000", false)] // a darker urgent red flips to light text (helper adapts)
    public void PreferDarkText_HandlesPriorityPalette(string hex, bool expectDark)
    {
        Assert.True(StatusBadgeColor.TryParseHex(hex, out var r, out var g, out var b));
        Assert.Equal(expectDark, StatusBadgeColor.PreferDarkText(r, g, b));
    }
}
