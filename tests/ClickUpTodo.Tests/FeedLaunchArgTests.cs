using ClickUpTodo;

namespace ClickUpTodo.Tests;

public sealed class FeedLaunchArgTests
{
    [Fact]
    public void Absent_WhenFlagNotPresent()
    {
        Assert.False(FeedLaunchArg.Parse(["--driver", "ansi"]).Present);
    }

    [Fact]
    public void NoArgs_IsAbsent()
    {
        Assert.False(FeedLaunchArg.Parse([]).Present);
    }

    [Fact]
    public void Present_WhenFlagGiven()
    {
        Assert.True(FeedLaunchArg.Parse(["--feed"]).Present);
    }

    [Fact]
    public void FindsFlag_AmongOtherArgs()
    {
        Assert.True(FeedLaunchArg.Parse(["--driver", "ansi", "--feed"]).Present);
    }

    [Fact]
    public void EqualsForm_IsNotAccepted_FlagTakesNoValue()
    {
        // --feed carries no value; an =-form is not the flag.
        Assert.False(FeedLaunchArg.Parse(["--feed=1"]).Present);
    }

    [Fact]
    public void DifferentFlagWithSamePrefix_DoesNotMatch()
    {
        // A hypothetical future "--feedback" must not be read as the launch flag.
        Assert.False(FeedLaunchArg.Parse(["--feedback"]).Present);
    }
}
