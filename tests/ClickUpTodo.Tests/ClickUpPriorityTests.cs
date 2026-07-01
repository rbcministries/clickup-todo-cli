using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the canonical priority mapping (issue #48): name↔level conversion and deriving the
/// importance level from a ClickUp priority object's id/name (1=Urgent … 4=Low, lower = more urgent).
/// </summary>
public sealed class ClickUpPriorityTests
{
    [Theory]
    [InlineData("Urgent", 1)]
    [InlineData("high", 2)]
    [InlineData("  Normal  ", 3)]
    [InlineData("LOW", 4)]
    public void LevelFromName_MapsCanonicalNames_CaseAndTrimInsensitive(string name, int expected)
        => Assert.Equal(expected, ClickUpPriority.LevelFromName(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("whatever")]
    public void LevelFromName_UnrecognisedIsNull(string? name)
        => Assert.Null(ClickUpPriority.LevelFromName(name));

    [Theory]
    [InlineData(1, "Urgent")]
    [InlineData(2, "High")]
    [InlineData(3, "Normal")]
    [InlineData(4, "Low")]
    public void NameFromLevel_MapsEachLevel(int level, string expected)
        => Assert.Equal(expected, ClickUpPriority.NameFromLevel(level));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5)]
    public void NameFromLevel_OutOfRangeIsNull(int? level)
        => Assert.Null(ClickUpPriority.NameFromLevel(level));

    [Fact]
    public void Level_PrefersIdOverName()
    {
        // ClickUp's id is the canonical level string; trust it even if a name is also present.
        Assert.Equal(2, ClickUpPriority.Level("2", "high"));
        Assert.Equal(1, ClickUpPriority.Level(" 1 ", null));
    }

    [Fact]
    public void Level_FallsBackToName_WhenIdMissingOrOutOfRange()
    {
        Assert.Equal(3, ClickUpPriority.Level(null, "normal"));
        Assert.Equal(4, ClickUpPriority.Level("99", "low")); // out-of-range id → name wins
        Assert.Equal(1, ClickUpPriority.Level("not-a-number", "urgent"));
    }

    [Fact]
    public void Level_NoPriority_IsNull()
    {
        Assert.Null(ClickUpPriority.Level(null, null));
        Assert.Null(ClickUpPriority.Level("", ""));
        Assert.Null(ClickUpPriority.Level("7", "mystery"));
    }

    [Fact]
    public void Names_AreMostUrgentFirst()
        => Assert.Equal(["Urgent", "High", "Normal", "Low"], ClickUpPriority.Names);
}
