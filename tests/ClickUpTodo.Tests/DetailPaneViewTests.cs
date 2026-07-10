using ClickUpTodo.Tui;
using Terminal.Gui.Drawing;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for the pure separator-line classification in <see cref="DetailPaneView.BuildCells"/> — the
/// logic that tags the inter-block rule so the pane draws it on the terminal-default background. The
/// draw override itself is Terminal.Gui glue and isn't unit-testable in CI.
/// </summary>
public sealed class DetailPaneViewTests
{
    private const string Sep = TaskDetailFormatter.CommentSeparator;

    private static bool IsSeparatorTagged(IReadOnlyList<Cell> line)
        => line.Count > 0 && line.All(c => c.Attribute is { } a && a.Background == Color.None);

    private static bool IsUntagged(IReadOnlyList<Cell> line)
        => line.All(c => c.Attribute is null);

    [Fact]
    public void BuildCells_OneLinePerBodyLine()
    {
        var cells = DetailPaneView.BuildCells("a\nb\nc", Sep);
        Assert.Equal(3, cells.Count);
    }

    [Fact]
    public void BuildCells_TagsSeparatorLineCellsWithTerminalDefaultBackground()
    {
        var body = string.Join('\n', "Author  ·  today", "A comment.", "", Sep, "", "Author2", "Another.");
        var cells = DetailPaneView.BuildCells(body, Sep);

        var separatorRow = cells.Single(IsSeparatorTagged);
        Assert.Equal(Sep.Length, separatorRow.Count);
        // Color.None (alpha 0) is what the driver renders as the terminal's own default background.
        Assert.All(separatorRow, c => Assert.Equal(0, c.Attribute!.Value.Background.A));
    }

    [Fact]
    public void BuildCells_LeavesContentLinesUncoloured()
    {
        var body = string.Join('\n', "Author  ·  today", "A comment.", "", Sep, "", "Another.");
        var cells = DetailPaneView.BuildCells(body, Sep);

        // Every line except the rule is left with null attributes → drawn in the pane's normal colour.
        var tagged = cells.Count(IsSeparatorTagged);
        Assert.Equal(1, tagged);
        foreach (var line in cells.Where(l => !IsSeparatorTagged(l)))
            Assert.True(IsUntagged(line));
    }

    [Fact]
    public void BuildCells_TagsEverySeparatorInAMultiBlockBody()
    {
        // Three blocks → two separators.
        var body = string.Join("\n\n" + Sep + "\n\n", "block one", "block two", "block three");
        var cells = DetailPaneView.BuildCells(body, Sep);
        Assert.Equal(2, cells.Count(IsSeparatorTagged));
    }

    [Fact]
    public void BuildCells_DoesNotTagLinesThatMerelyContainTheRule()
    {
        // A body line that is longer than the bare rule must not be treated as a separator.
        var cells = DetailPaneView.BuildCells(Sep + " trailing", Sep);
        Assert.DoesNotContain(cells, IsSeparatorTagged);
    }
}
