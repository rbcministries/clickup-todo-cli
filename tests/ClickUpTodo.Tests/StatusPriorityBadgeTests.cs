using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the shared <see cref="StatusPriorityBadge"/> label — the single source of the
/// "{icon} {name}" text both the main-list text badges and the detail title line render (#162).
/// </summary>
public sealed class StatusPriorityBadgeTests
{
    [Fact]
    public void Glyphs_AreTheHollowCircleAndFlag()
    {
        Assert.Equal("○", StatusPriorityBadge.StatusGlyph);
        Assert.Equal("⚑", StatusPriorityBadge.PriorityGlyph);
    }

    [Fact]
    public void Status_IsGlyphSpaceName()
    {
        Assert.Equal("○ In Progress", StatusPriorityBadge.Status("In Progress"));
    }

    [Fact]
    public void Priority_IsGlyphSpaceName()
    {
        Assert.Equal("⚑ Urgent", StatusPriorityBadge.Priority("Urgent"));
    }

    [Fact]
    public void PriorityIconChip_SharesTheGlyph()
    {
        // The icon-mode Priority chip is the glyph flanked by a space — one source of truth. (The
        // icon-mode Status chip is now a letter abbreviation, not the glyph, so it no longer shares it — #181.)
        Assert.Equal($" {StatusPriorityBadge.PriorityGlyph} ", TaskRowFormatter.PriorityIcon);
    }

    [Fact]
    public void StatusGlyph_StillBacksTheTextBadge_NotTheIconChip()
    {
        // The ○ glyph moved out of the icon-mode Status chip (now "(XX)") but still leads the Text-mode
        // Status badge and the detail title line, so the glyph remains a single shared source there.
        Assert.StartsWith($"{StatusPriorityBadge.StatusGlyph} ", StatusPriorityBadge.Status("Blocked"));
    }
}
