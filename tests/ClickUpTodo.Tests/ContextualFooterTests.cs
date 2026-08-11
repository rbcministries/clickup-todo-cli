using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// The status-line precedence <see cref="ContextualFooter"/> owns for the #408 hover hint: a hover hint
/// shows over the steady status and is restored on clear, a <see cref="ContextualFooter.Flash"/> outranks
/// and drops a hint, and <see cref="ContextualFooter.CommitStatus"/> keeps a live hint on top of a
/// recomposed steady status. Views construct headless (no driver), so only the label text is asserted.
/// </summary>
public sealed class ContextualFooterTests
{
    private static ContextualFooter Footer(string status = "ready") => new(status);

    [Fact]
    public void SeedStatus_ShowsOnTheStatusLine()
    {
        Assert.Equal("ready", Footer("ready").StatusText);
    }

    [Fact]
    public void SetHoverHint_ShowsTheHint_ThenRestoresTheSteadyStatusOnClear()
    {
        var footer = Footer("ready");

        footer.SetHoverHint("Link: https://example.com");
        Assert.Equal("Link: https://example.com", footer.StatusText);

        footer.SetHoverHint(null);
        Assert.Equal("ready", footer.StatusText);
    }

    [Fact]
    public void Flash_OutranksAHoverHint_AndTheHintDoesNotRestoreOverTheFlash()
    {
        var footer = Footer("ready");
        footer.SetHoverHint("Link: https://example.com");

        footer.Flash("Refreshing…");
        Assert.Equal("Refreshing…", footer.StatusText);

        // The hint was dropped by the flash, so clearing hover restores the flash's status, not the hint.
        footer.SetHoverHint(null);
        Assert.Equal("Refreshing…", footer.StatusText);
    }

    [Fact]
    public void CommitStatus_KeepsALiveHoverHintOnTop_ThenRevealsTheNewStatusOnClear()
    {
        var footer = Footer("ready");
        footer.SetHoverHint("Link: https://example.com");

        // The host recomposes the steady status while the pointer still rests on the link.
        footer.Status = "12 tasks · updated 14:02";
        footer.CommitStatus();
        Assert.Equal("Link: https://example.com", footer.StatusText);   // hint stays on top

        footer.SetHoverHint(null);
        Assert.Equal("12 tasks · updated 14:02", footer.StatusText);     // the recomposed status shows
    }

    [Fact]
    public void SetHoverHint_RepeatedClear_IsIdempotent()
    {
        var footer = Footer("ready");

        footer.SetHoverHint(null);
        footer.SetHoverHint(null);
        Assert.Equal("ready", footer.StatusText);
    }
}
