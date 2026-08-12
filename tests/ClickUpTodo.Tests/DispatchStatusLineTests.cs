using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure dispatch status-line composer (#517, split-pane epic slice L) — the single
/// rule that folds the #505/#515 split→tab degradation reason, the launcher core message (with its own
/// fall-back <see cref="LaunchResult.Note"/>), and the #462 Windows Terminal profile note into one
/// coherent line. Exhaustive over each fact present/absent, mirroring <see cref="AppHostLaunchTests"/>'s
/// exact-string style. Every branch runs without a terminal or a UI host.
/// </summary>
public sealed class DispatchStatusLineTests
{
    // The launcher's core message on success (AgentDispatcher.FormatStatus shape), and its failure form.
    private const string CoreOk = "Launched Claude (Windows Terminal) for 'My Task'.";
    private const string CoreOkNonWt = "Launched Claude (gnome-terminal) for 'My Task'.";
    private const string CoreFail = "Could not launch Claude: no terminal found.";

    // A viability-floor reason of the same shape SplitViability.Evaluate emits (host-agnostic, #590).
    private const string DegradeReason =
        "Terminal too narrow to split (50-column panes; need 60) — opening elsewhere instead.";
    private const string Profile = "My Repo";

    private static string ProfileClause(string profile) => $" (Windows Terminal profile '{profile}'.)";

    // ── All-defaults: the shortest honest message is just the core ────────────────────────────────

    [Fact]
    public void AllDefaults_IsJustTheCoreMessage()
        => Assert.Equal(
            CoreOk,
            DispatchStatusLine.Compose(CoreOk, launched: true, "Windows Terminal", splitDegradedReason: null, windowsTerminalProfile: null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDegradationAndProfile_AddNothing_NoDoubleSpaces(string? blank)
    {
        var status = DispatchStatusLine.Compose(CoreOk, launched: true, "Windows Terminal", splitDegradedReason: blank, windowsTerminalProfile: blank);
        Assert.Equal(CoreOk, status);
        Assert.DoesNotContain("  ", status); // no empty clause leaves a double space behind
    }

    // ── Launcher Note (carried inside the core message) survives composition unchanged ────────────

    [Fact]
    public void LauncherNote_InTheCoreMessage_IsPreservedVerbatim()
    {
        // FormatStatus appends the launcher's non-fatal note to the core; the composer must not touch it.
        const string coreWithNote = CoreOk + " Opened a new window (no tab support).";
        var status = DispatchStatusLine.Compose(coreWithNote, launched: true, "Windows Terminal", splitDegradedReason: null, windowsTerminalProfile: null);
        Assert.Equal(coreWithNote, status);
    }

    [Fact]
    public void LauncherNote_SurvivesAlongsideDegradationAndProfile()
    {
        const string coreWithNote = CoreOk + " Opened a new window (no tab support).";
        var status = DispatchStatusLine.Compose(coreWithNote, launched: true, "Windows Terminal", DegradeReason, Profile);
        Assert.Contains("Opened a new window (no tab support).", status);
        Assert.StartsWith(DegradeReason + " ", status);
        Assert.EndsWith(ProfileClause(Profile), status);
    }

    // ── Degradation leads ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Degradation_LeadsTheLine()
    {
        var status = DispatchStatusLine.Compose(CoreOkNonWt, launched: true, "gnome-terminal", DegradeReason, windowsTerminalProfile: null);
        Assert.Equal(DegradeReason + " " + CoreOkNonWt, status);
    }

    // ── Profile trails, only for a Windows Terminal host ──────────────────────────────────────────

    [Theory]
    [InlineData("Windows Terminal")]
    [InlineData("Windows Terminal (new tab)")]
    public void Profile_Trails_WhenAWindowsTerminalHostLaunched(string host)
    {
        var status = DispatchStatusLine.Compose(CoreOk, launched: true, host, splitDegradedReason: null, windowsTerminalProfile: Profile);
        Assert.Equal(CoreOk + ProfileClause(Profile), status);
    }

    [Theory]
    [InlineData("gnome-terminal")]
    [InlineData("PowerShell (pwsh)")]
    [InlineData("Windows PowerShell")]
    public void Profile_IsOmitted_WhenTheLaunchUsedANonWtHost(string host)
    {
        // A profile matches on directory alone, but the launch used a non-WT terminal — claiming it would mislead.
        var status = DispatchStatusLine.Compose(CoreOkNonWt, launched: true, host, splitDegradedReason: null, windowsTerminalProfile: Profile);
        Assert.Equal(CoreOkNonWt, status);
        Assert.DoesNotContain("profile", status, StringComparison.OrdinalIgnoreCase);
    }

    // ── Everything at once: degradation, core(+note), profile — present and ordered ───────────────

    [Fact]
    public void AllClauses_ArePresentAndOrdered()
    {
        const string coreWithNote = CoreOk + " Opened a new window (no tab support).";
        var status = DispatchStatusLine.Compose(coreWithNote, launched: true, "Windows Terminal", DegradeReason, Profile);

        var degradeAt = status.IndexOf(DegradeReason, StringComparison.Ordinal);
        var coreAt = status.IndexOf("Launched Claude", StringComparison.Ordinal);
        var profileAt = status.IndexOf("Windows Terminal profile", StringComparison.Ordinal);

        Assert.True(degradeAt >= 0 && coreAt >= 0 && profileAt >= 0);
        Assert.True(degradeAt < coreAt, "degradation must lead the core");
        Assert.True(coreAt < profileAt, "profile must trail the core");
        Assert.Equal(DegradeReason + " " + coreWithNote + ProfileClause(Profile), status);
    }

    // ── Failure short-circuits: no degradation, no profile, whatever facts are supplied ───────────

    [Fact]
    public void Failure_IsJustTheFailureMessage_EvenWithDegradationAndProfileSupplied()
    {
        // Nothing opened, so "too narrow, opening elsewhere" and "under profile X" would both describe a
        // launch that never happened — both are suppressed.
        var status = DispatchStatusLine.Compose(CoreFail, launched: false, launchedWith: null, DegradeReason, Profile);
        Assert.Equal(CoreFail, status);
    }

    [Fact]
    public void Compose_NullCoreMessage_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => DispatchStatusLine.Compose(null!, launched: true, "Windows Terminal", null, null));

    // ── WindowsTerminalProfileNote gate (mirrors DispatchCoordinatorTests' coverage) ──────────────

    [Theory]
    [InlineData("Windows Terminal")]
    [InlineData("Windows Terminal (new tab)")]
    public void ProfileNote_NamesTheProfile_ForAWindowsTerminalHost(string host)
    {
        var note = DispatchStatusLine.WindowsTerminalProfileNote(Profile, host);
        Assert.Equal(ProfileClause(Profile), note);
    }

    [Theory]
    [InlineData("gnome-terminal")]
    [InlineData("PowerShell (pwsh)")]
    [InlineData("Windows PowerShell")]
    [InlineData(null)]
    public void ProfileNote_IsNull_ForANonWtOrFailedHost(string? host)
        => Assert.Null(DispatchStatusLine.WindowsTerminalProfileNote(Profile, host));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProfileNote_IsNull_WhenNoProfileMatched(string? profile)
        => Assert.Null(DispatchStatusLine.WindowsTerminalProfileNote(profile, "Windows Terminal"));
}
