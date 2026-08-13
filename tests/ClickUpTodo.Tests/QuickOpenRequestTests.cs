using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure quick-open result factory (launch modes B, #615): the trim + blank guard the
/// <see cref="QuickOpenScreen"/>'s three submit gestures funnel through, tested off Terminal.Gui so the
/// per-intent collection is provable in CI.
/// </summary>
public sealed class QuickOpenRequestTests
{
    [Theory]
    [InlineData(QuickOpenIntent.OpenHere)]
    [InlineData(QuickOpenIntent.NewTab)]
    [InlineData(QuickOpenIntent.SplitPane)]
    public void From_NonBlank_CarriesTrimmedTextAndIntent(QuickOpenIntent intent)
    {
        var request = QuickOpenRequest.From("  86abc123  ", intent);
        Assert.Equal(new QuickOpenRequest("86abc123", intent), request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void From_Blank_IsNull_ForEveryIntent(string? raw)
    {
        // A blank field never dismisses the surface on any gesture — the screen flashes and stays open.
        Assert.Null(QuickOpenRequest.From(raw, QuickOpenIntent.OpenHere));
        Assert.Null(QuickOpenRequest.From(raw, QuickOpenIntent.NewTab));
        Assert.Null(QuickOpenRequest.From(raw, QuickOpenIntent.SplitPane));
    }

    [Fact]
    public void From_PreservesInnerWhitespace_TrimsOnlyEnds()
    {
        // A task URL never contains spaces, but the factory must not mangle a token it is handed — it
        // trims the ends and leaves the interior alone.
        var request = QuickOpenRequest.From(" a b ", QuickOpenIntent.NewTab);
        Assert.Equal("a b", request?.Text);
    }
}
