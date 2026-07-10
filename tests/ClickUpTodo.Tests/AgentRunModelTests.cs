using ClickUpTodo.Agent;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pins the pure, CI-testable surface of the background one-off run screen (#99): the spinner frame
/// advance/wrap, the per-phase header text, and the phase transitions. The Terminal.Gui screen is not
/// instantiated (the suite never calls <c>Application.Init</c>), matching the repo's pattern of
/// asserting only the framework-free logic of a screen.
/// </summary>
public sealed class AgentRunModelTests
{
    [Fact]
    public void StartsRunning_WithFirstSpinnerFrame_AndTaskNameInHeader()
    {
        var model = new AgentRunModel("Ship the Q3 report");

        Assert.Equal(AgentRunPhase.Running, model.Phase);
        Assert.True(model.IsActive);
        Assert.Equal(AgentRunModel.SpinnerFrames[0], model.CurrentFrame);
        Assert.Contains("Ship the Q3 report", model.Header);
        Assert.StartsWith(AgentRunModel.SpinnerFrames[0], model.Header);
    }

    [Fact]
    public void Advance_CyclesThroughFramesAndWraps()
    {
        var model = new AgentRunModel("t");
        var count = AgentRunModel.SpinnerFrames.Count;

        // The first Advance moves to frame 1; after `count` advances we are back at frame 0.
        for (var i = 1; i <= count; i++)
        {
            model.Advance();
            Assert.Equal(AgentRunModel.SpinnerFrames[i % count], model.CurrentFrame);
        }
    }

    [Fact]
    public void Advance_ReturnsTheFreshHeader()
    {
        var model = new AgentRunModel("t");
        var header = model.Advance();
        Assert.Equal(model.Header, header);
        Assert.StartsWith(model.CurrentFrame, header);
    }

    [Fact]
    public void MarkCancelling_OnlyFromRunning_AndStaysActive()
    {
        var model = new AgentRunModel("t");
        model.MarkCancelling();

        Assert.Equal(AgentRunPhase.Cancelling, model.Phase);
        Assert.True(model.IsActive);
        Assert.Contains("Cancelling", model.Header);

        // Once finished, MarkCancelling is a no-op (a completed run can't slip back to cancelling).
        model.MarkFinished(success: true);
        model.MarkCancelling();
        Assert.Equal(AgentRunPhase.Succeeded, model.Phase);
    }

    [Fact]
    public void MarkFinished_Success_ShowsSucceededHeader_AndBecomesInactive()
    {
        var model = new AgentRunModel("Ship it");
        model.MarkFinished(success: true);

        Assert.Equal(AgentRunPhase.Succeeded, model.Phase);
        Assert.False(model.IsActive);
        Assert.StartsWith("✓", model.Header);
        Assert.Contains("Ship it", model.Header);
    }

    [Fact]
    public void MarkFinished_Failure_ShowsFailedHeader()
    {
        var model = new AgentRunModel("Ship it");
        model.MarkFinished(success: false);

        Assert.Equal(AgentRunPhase.Failed, model.Phase);
        Assert.False(model.IsActive);
        Assert.StartsWith("✗", model.Header);
    }

    [Fact]
    public void MarkCancelled_ShowsCancelledHeader()
    {
        var model = new AgentRunModel("Ship it");
        model.MarkCancelling();
        model.MarkCancelled();

        Assert.Equal(AgentRunPhase.Cancelled, model.Phase);
        Assert.False(model.IsActive);
        Assert.StartsWith("■", model.Header);
        Assert.Contains("Ship it", model.Header);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTaskName_FallsBackToAPlaceholder(string? name)
    {
        var model = new AgentRunModel(name!);
        Assert.Contains("task", model.Header, StringComparison.OrdinalIgnoreCase);
    }

    // ── FormatOutput (the finished-run body text) ────────────────────────────────────

    [Fact]
    public void FormatOutput_Success_IsJustTheStdout()
    {
        var run = BackgroundRunResult.Exited(0, "the summary", null);
        Assert.Equal("the summary", AgentRunModel.FormatOutput(run));
    }

    [Fact]
    public void FormatOutput_NonZeroExit_AppendsStderrAndExitCode()
    {
        var run = BackgroundRunResult.Exited(2, "partial output", "boom");
        var text = AgentRunModel.FormatOutput(run);

        Assert.Contains("partial output", text);
        Assert.Contains("boom", text);
        Assert.Contains("exited with code 2", text);
    }

    [Fact]
    public void FormatOutput_NonZeroExit_NoStdout_StillShowsExitCode()
    {
        var run = BackgroundRunResult.Exited(1, "", null);
        var text = AgentRunModel.FormatOutput(run);

        Assert.Contains("exited with code 1", text);
        Assert.DoesNotContain("\n\n", text); // no leading blank block when there was no stdout/stderr
    }

    [Fact]
    public void FormatOutput_NeverStarted_ShowsTheStartFailureMessage()
    {
        var run = BackgroundRunResult.NotStarted("Could not start 'claude': not found");
        Assert.Equal("Could not start 'claude': not found", AgentRunModel.FormatOutput(run));
    }
}
