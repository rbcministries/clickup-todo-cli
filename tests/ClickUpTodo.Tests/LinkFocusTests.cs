using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>Tests for the pure <see cref="LinkFocus.Step"/> focus-index math behind Tab/Shift+Tab link
/// traversal (#319). The pane draw/scroll glue that consumes it isn't unit-testable in CI.</summary>
public sealed class LinkFocusTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Step_NoLinks_IsAlwaysNone(int count)
    {
        Assert.Equal(LinkFocus.None, LinkFocus.Step(LinkFocus.None, count, forward: true));
        Assert.Equal(LinkFocus.None, LinkFocus.Step(LinkFocus.None, count, forward: false));
    }

    [Fact]
    public void Step_FromNone_ForwardLandsOnFirst_BackwardOnLast()
    {
        Assert.Equal(0, LinkFocus.Step(LinkFocus.None, 3, forward: true));
        Assert.Equal(2, LinkFocus.Step(LinkFocus.None, 3, forward: false));
    }

    [Fact]
    public void Step_ForwardAdvancesAndWrapsPastTheLast()
    {
        Assert.Equal(1, LinkFocus.Step(0, 3, forward: true));
        Assert.Equal(2, LinkFocus.Step(1, 3, forward: true));
        Assert.Equal(0, LinkFocus.Step(2, 3, forward: true)); // wrap
    }

    [Fact]
    public void Step_BackwardRetreatsAndWrapsPastTheFirst()
    {
        Assert.Equal(1, LinkFocus.Step(2, 3, forward: false));
        Assert.Equal(0, LinkFocus.Step(1, 3, forward: false));
        Assert.Equal(2, LinkFocus.Step(0, 3, forward: false)); // wrap
    }

    [Fact]
    public void Step_SingleLink_StaysOnTheOnlyLinkEitherWay()
    {
        Assert.Equal(0, LinkFocus.Step(0, 1, forward: true));
        Assert.Equal(0, LinkFocus.Step(0, 1, forward: false));
        Assert.Equal(0, LinkFocus.Step(LinkFocus.None, 1, forward: true));
        Assert.Equal(0, LinkFocus.Step(LinkFocus.None, 1, forward: false));
    }

    [Fact]
    public void Step_ForwardThenBackward_ReturnsToStart()
    {
        var next = LinkFocus.Step(1, 4, forward: true);   // 2
        Assert.Equal(1, LinkFocus.Step(next, 4, forward: false));
    }
}
