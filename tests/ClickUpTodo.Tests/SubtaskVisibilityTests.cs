using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="SubtaskVisibility"/> — the pure rule that classifies a pulled-in
/// ("foreign") subtask under the F4 three-state view (#179): unassigned vs assigned-to-others, and
/// which of those render in each state.
/// </summary>
public sealed class SubtaskVisibilityTests
{
    private static TaskItem Unassigned() => new() { Id = "u", Name = "u", Assignees = [] };
    private static TaskItem AssignedToOthers() =>
        new() { Id = "o", Name = "o", Assignees = [new TaskAssignee(200, "Teammate")] };

    [Fact]
    public void IsUnassigned_TrueOnlyWhenNoAssignees()
    {
        Assert.True(SubtaskVisibility.IsUnassigned(Unassigned()));
        Assert.False(SubtaskVisibility.IsUnassigned(AssignedToOthers()));
    }

    [Fact]
    public void All_ShowsEveryForeignSubtask()
    {
        Assert.True(SubtaskVisibility.IsVisibleForeign(Unassigned(), SubtaskView.All));
        Assert.True(SubtaskVisibility.IsVisibleForeign(AssignedToOthers(), SubtaskView.All));
    }

    [Fact]
    public void MineAndUnassigned_ShowsOnlyUnassigned()
    {
        Assert.True(SubtaskVisibility.IsVisibleForeign(Unassigned(), SubtaskView.MineAndUnassigned));
        Assert.False(SubtaskVisibility.IsVisibleForeign(AssignedToOthers(), SubtaskView.MineAndUnassigned));
    }

    [Fact]
    public void Hidden_ShowsNothing()
    {
        Assert.False(SubtaskVisibility.IsVisibleForeign(Unassigned(), SubtaskView.Hidden));
        Assert.False(SubtaskVisibility.IsVisibleForeign(AssignedToOthers(), SubtaskView.Hidden));
    }
}

/// <summary>Unit tests for the F4 cycle order and display text (<see cref="SubtaskViewExtensions"/>).</summary>
public sealed class SubtaskViewExtensionsTests
{
    [Fact]
    public void Next_WrapsMineAndUnassigned_All_Hidden_FromHidden()
    {
        // Pressing F4 from Hidden lands on the default on-state, then cycles 1 -> 2 -> 3 -> 1.
        Assert.Equal(SubtaskView.MineAndUnassigned, SubtaskView.Hidden.Next());
        Assert.Equal(SubtaskView.All, SubtaskView.MineAndUnassigned.Next());
        Assert.Equal(SubtaskView.Hidden, SubtaskView.All.Next());
    }

    [Fact]
    public void Next_ThreePresses_ReturnToStart()
    {
        var s = SubtaskView.MineAndUnassigned;
        Assert.Equal(SubtaskView.MineAndUnassigned, s.Next().Next().Next());
    }

    [Theory]
    [InlineData(SubtaskView.MineAndUnassigned)]
    [InlineData(SubtaskView.All)]
    [InlineData(SubtaskView.Hidden)]
    public void Describe_IsNonEmpty_ForEveryState(SubtaskView state)
        => Assert.False(string.IsNullOrWhiteSpace(state.Describe()));

    [Fact]
    public void TitleFlag_NullOnlyWhenHidden()
    {
        Assert.Null(SubtaskView.Hidden.TitleFlag());
        Assert.NotNull(SubtaskView.MineAndUnassigned.TitleFlag());
        Assert.NotNull(SubtaskView.All.TitleFlag());
    }
}
