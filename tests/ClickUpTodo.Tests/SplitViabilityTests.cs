using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the split-pane viability floor (#505, slice C) — <see cref="SplitViability.Evaluate"/>.
/// Pure decision logic: given a terminal width, direction and size, decide whether a split is worth making
/// or should degrade to a tab. The <see cref="SplitViability.Decision.Location"/> a degraded check returns
/// is fed to <see cref="TerminalCommandPlanner"/>, so a companion test pins that a degraded
/// <c>NewTab</c> yields exactly today's tab specs (the "degraded spec").
/// </summary>
public sealed class SplitViabilityTests
{
    [Fact]
    public void WideTerminal_EvenSplit_StaysASplit()
    {
        // 200 cols, even → two 100-col panes, well above the 60 floor.
        var d = SplitViability.Evaluate(200, SplitDirection.Beside);

        Assert.Equal(LaunchLocation.SplitPane, d.Location);
        Assert.False(d.Degraded);
        Assert.Equal(100, d.ResultingColumns);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void NarrowTerminal_EvenSplit_DegradesToTab_WithHostAgnosticReason()
    {
        // 100 cols, even → two 50-col panes, below the 60 floor → degrade.
        var d = SplitViability.Evaluate(100, SplitDirection.Beside);

        Assert.Equal(LaunchLocation.NewTab, d.Location);
        Assert.True(d.Degraded);
        Assert.Equal(50, d.ResultingColumns);
        Assert.NotNull(d.Reason);
        Assert.Contains("50", d.Reason);
        Assert.Contains("60", d.Reason);
        // The reason names the split problem but must NOT promise a "tab": the NewTab fallback isn't a tab
        // on every host (e.g. Zellij opens an in-session pane, #589), so the message stays host-agnostic.
        Assert.Contains("narrow", d.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tab", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactlyAtFloor_StaysASplit()
    {
        // 120 cols, even → two 60-col panes == the floor → not degraded (>= floor).
        var d = SplitViability.Evaluate(120, SplitDirection.Beside);

        Assert.Equal(LaunchLocation.SplitPane, d.Location);
        Assert.False(d.Degraded);
        Assert.Equal(60, d.ResultingColumns);
    }

    [Fact]
    public void Auto_IsTreatedAsSideBySide_ForTheFloor()
    {
        // Auto divides the columns like Beside (WT aspect-auto still halves a side-by-side), so the floor
        // applies identically.
        Assert.Equal(
            SplitViability.Evaluate(100, SplitDirection.Beside),
            SplitViability.Evaluate(100, SplitDirection.Auto));
    }

    [Fact]
    public void Below_NeverTripsTheFloor_EvenOnANarrowTerminal()
    {
        // A stacked split keeps the full width — only rows shrink — so a column floor can't trip it.
        var d = SplitViability.Evaluate(40, SplitDirection.Below);

        Assert.Equal(LaunchLocation.SplitPane, d.Location);
        Assert.False(d.Degraded);
        Assert.Equal(40, d.ResultingColumns);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void UnevenSize_BindsOnTheNarrowerPane()
    {
        // 150 cols, new pane 30% → new=45, ours=105; the binding width is the narrower 45 < 60 → degrade,
        // even though our own pane would be comfortable.
        var d = SplitViability.Evaluate(150, SplitDirection.Beside, sizePercent: 30);

        Assert.True(d.Degraded);
        Assert.Equal(45, d.ResultingColumns);
    }

    [Fact]
    public void UnevenSize_ViableWhenBothPanesClearTheFloor()
    {
        // 150 cols, new pane 45% → new=68, ours=82; both clear 60 → stays a split, bound by the 68.
        var d = SplitViability.Evaluate(150, SplitDirection.Beside, sizePercent: 45);

        Assert.False(d.Degraded);
        Assert.Equal(LaunchLocation.SplitPane, d.Location);
        Assert.Equal(68, d.ResultingColumns);
    }

    [Fact]
    public void CustomFloor_IsHonoured()
    {
        // A caller-supplied floor overrides the default: 200 cols → 100-col panes, which fail a 120 floor.
        var d = SplitViability.Evaluate(200, SplitDirection.Beside, minPaneColumns: 120);

        Assert.True(d.Degraded);
        Assert.Equal(100, d.ResultingColumns);
        Assert.Contains("120", d.Reason);
    }

    [Fact]
    public void DefaultFloor_IsSixtyColumns()
    {
        // Pin the derived default so a change is deliberate (and matches the doc/plan derivation).
        Assert.Equal(60, SplitViability.DefaultMinPaneColumns);
    }

    [Fact]
    public void DegradedDecision_FeedsThePlanner_ExactlyTodaysTabSpecs()
    {
        // The whole point of degrading to NewTab: feeding that Location to the planner must produce the
        // same specs as an outright NewTab request — the "degraded spec" the issue asks for.
        var decision = SplitViability.Evaluate(80, SplitDirection.Beside); // 40-col panes → degrade
        Assert.Equal(LaunchLocation.NewTab, decision.Location);

        Func<string, bool> exists = new HashSet<string>(["gnome-terminal"], StringComparer.OrdinalIgnoreCase).Contains;
        Func<string, string?> env = k => k == "VTE_VERSION" ? "6003" : null;

        var degraded = TerminalCommandPlanner.Plan(
            OSPlatformKind.Linux, exists, env, "/tmp/p.txt", null,
            new TerminalLauncherOptions { LaunchLocation = decision.Location });
        var tab = TerminalCommandPlanner.Plan(
            OSPlatformKind.Linux, exists, env, "/tmp/p.txt", null,
            new TerminalLauncherOptions { LaunchLocation = LaunchLocation.NewTab });

        Assert.Equal(tab.Select(s => s.DisplayName), degraded.Select(s => s.DisplayName));
        Assert.Equal(tab.Select(s => s.Arguments), degraded.Select(s => s.Arguments));
    }
}
