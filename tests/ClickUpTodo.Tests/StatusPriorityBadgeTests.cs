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
    public void IconChips_ShareTheGlyphs()
    {
        // The icon-mode chips are the same glyphs, flanked by a space — one source of truth.
        Assert.Equal($" {StatusPriorityBadge.StatusGlyph} ", TaskRowFormatter.StatusIcon);
        Assert.Equal($" {StatusPriorityBadge.PriorityGlyph} ", TaskRowFormatter.PriorityIcon);
    }
}
