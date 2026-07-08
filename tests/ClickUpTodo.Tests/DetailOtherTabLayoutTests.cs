using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="DetailOtherTabLayout.Compute"/> — how the detail Other tab splits its
/// rows between the fixed coloured header and the scrollable body on short terminals (issue #81):
/// everything fits at a normal height; on a short window the header caps, the body keeps a minimum
/// scrollable region, and the clipped trailing header lines are reported as spillover so the caller
/// can keep them reachable in the body.
/// </summary>
public sealed class DetailOtherTabLayoutTests
{
    private const int Gap = DetailOtherTabLayout.GapRows;      // 1
    private const int MinBody = DetailOtherTabLayout.MinBodyRows; // 3
    private const int MinHeader = DetailOtherTabLayout.MinHeaderRows; // 1

    [Fact]
    public void RoomyWindow_ShowsFullHeaderWithGap_NoSpill()
    {
        var l = DetailOtherTabLayout.Compute(headerLineCount: 7, availableHeight: 20);

        Assert.Equal(7, l.HeaderHeight);
        Assert.Equal(0, l.SpilledHeaderLines);
        Assert.Equal(7 + Gap, l.BodyY);
        Assert.Equal(20 - (7 + Gap), l.BodyHeight);
    }

    [Fact]
    public void ExactlyFits_KeepsFullHeaderGapAndMinBody()
    {
        // 7 header + 1 gap + 3 body = 11 is the smallest height that still "fits".
        var l = DetailOtherTabLayout.Compute(7, 11);

        Assert.Equal(7, l.HeaderHeight);
        Assert.Equal(0, l.SpilledHeaderLines);
        Assert.Equal(8, l.BodyY);
        Assert.Equal(MinBody, l.BodyHeight);
    }

    [Fact]
    public void OneRowShort_DropsGap_FullHeaderStillFits_NoSpill()
    {
        // 10 < 11 so it's constrained, but the header still fits by giving up the blank gap row.
        var l = DetailOtherTabLayout.Compute(7, 10);

        Assert.Equal(7, l.HeaderHeight);
        Assert.Equal(0, l.SpilledHeaderLines);
        Assert.Equal(7, l.BodyY);               // body starts right after the header (no gap)
        Assert.Equal(MinBody, l.BodyHeight);
    }

    [Fact]
    public void Constrained_CapsHeaderAndSpillsOverflowKeepingMinBody()
    {
        var l = DetailOtherTabLayout.Compute(7, 8);

        Assert.Equal(5, l.HeaderHeight);        // 8 - MinBody
        Assert.Equal(2, l.SpilledHeaderLines);  // 7 - 5 lines pushed into the body
        Assert.Equal(5, l.BodyY);
        Assert.Equal(MinBody, l.BodyHeight);
    }

    [Fact]
    public void VeryShort_KeepsMinHeaderAndSpillsTheRest()
    {
        var l = DetailOtherTabLayout.Compute(7, 4);

        Assert.Equal(MinHeader, l.HeaderHeight);
        Assert.Equal(7 - MinHeader, l.SpilledHeaderLines);
        Assert.Equal(MinHeader, l.BodyY);
        Assert.Equal(MinBody, l.BodyHeight);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TinyWindow_BodyStaysReachable_NoNegativeSizes(int availableHeight)
    {
        var l = DetailOtherTabLayout.Compute(7, availableHeight);

        Assert.Equal(MinHeader, l.HeaderHeight);
        Assert.True(l.BodyHeight >= 1, "custom-fields body must keep at least one reachable row");
        Assert.True(l.BodyHeight >= 0);
    }

    [Fact]
    public void OneRowArea_DegradesToHeaderFloorNoBody_WithoutNegatives()
    {
        // Below MinHeaderRows + MinBodyRows the body can't keep its minimum; at a 1-row area only the
        // header floor fits. Documented degeneracy, far below the ≲9-row target — must stay non-negative.
        var l = DetailOtherTabLayout.Compute(7, 1);

        Assert.Equal(MinHeader, l.HeaderHeight);
        Assert.Equal(0, l.BodyHeight);
        Assert.True(l.BodyY >= 0);
        Assert.Equal(7 - MinHeader, l.SpilledHeaderLines);
    }

    [Fact]
    public void ZeroHeight_ProducesNoNegativeSizes()
    {
        var l = DetailOtherTabLayout.Compute(7, 0);

        Assert.Equal(0, l.HeaderHeight);   // header never exceeds the (zero) available height
        Assert.Equal(0, l.BodyHeight);
        Assert.True(l.BodyY >= 0);
        Assert.Equal(7, l.SpilledHeaderLines);
    }

    [Fact]
    public void NoHeaderLines_BodyTakesEverything()
    {
        var l = DetailOtherTabLayout.Compute(0, 15);

        Assert.Equal(0, l.HeaderHeight);
        Assert.Equal(0, l.SpilledHeaderLines);
        Assert.Equal(0, l.BodyY);
        Assert.Equal(15, l.BodyHeight);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Invariants_HoldAcrossShrinkingHeights(int headerLineCount)
    {
        var prevHeaderHeight = 0;
        for (var h = 0; h <= 30; h++)
        {
            var l = DetailOtherTabLayout.Compute(headerLineCount, h);

            Assert.InRange(l.HeaderHeight, 0, headerLineCount);
            Assert.True(l.HeaderHeight <= h, $"header {l.HeaderHeight} must fit in {h}");
            Assert.True(l.BodyHeight >= 0, "body height never negative");
            Assert.True(l.BodyY >= 0);
            Assert.Equal(headerLineCount - l.HeaderHeight, l.SpilledHeaderLines);

            // Growing the window never shrinks the coloured header (so it never spills *more* as space
            // grows) — the monotonicity the fix relies on to converge as a terminal is resized.
            Assert.True(l.HeaderHeight >= prevHeaderHeight, $"h={h}: header shrank as height grew");
            prevHeaderHeight = l.HeaderHeight;

            // Whenever there is room for at least the minimum header + minimum body, the body keeps its
            // minimum scrollable region so "Custom fields:" is always reachable — the #81 regression.
            if (h >= MinHeader + MinBody)
                Assert.True(l.BodyHeight >= MinBody, $"h={h}: body {l.BodyHeight} < {MinBody}");
        }
    }
}
