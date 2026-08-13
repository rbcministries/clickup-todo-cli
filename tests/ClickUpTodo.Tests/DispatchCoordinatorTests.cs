using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure half of the shared agent-dispatch coordinator (#345): <see cref="DispatchCoordinator.Plan"/>
/// and <see cref="DispatchCoordinator.ReconcileCache"/>. Asserting these directly locks the dashboard and
/// single-task hosts to one behaviour. Since #533 <c>Plan</c> is pure (no filesystem probes) — all
/// task-directory derivation moved to <see cref="DispatchWorkingDirectoryPreFill"/> (its own tests) — so
/// these cover the pick/mode resolution, the <see cref="ResolvedDispatch.CreateWorkingDir"/> containment
/// rule, the #462 WT-profile lookup, and cache reconciliation. The execution flows
/// (<c>RunInteractive</c>/<c>RunBackground</c>) are Terminal.Gui + real-process glue and aren't
/// CI-testable — covered by <c>tui-validate</c> + manual verification. No API / no Terminal.Gui here.
/// </summary>
public sealed class DispatchCoordinatorTests
{
    private const string Home = "/home/tester";
    private const string BaseDir = "/work";

    private static TaskDetail TaskWith(string id = "9xyz", string? customId = "ABC-12")
        => new() { Id = id, Name = "A task", CustomId = customId };

    // ── Working-dir resolution + the #533 CreateWorkingDir containment rule ──────────────────────

    [Fact]
    public void Plan_TaskDerived_ClearedField_ResolvesBaseDirAndCreatesIt()
    {
        var settings = new AgentDispatchSettings(); // defaults: TaskDerived, Interactive, NewWindow
        var request = new DispatchRequest("do the thing"); // blank/cleared working-dir field

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal("do the thing", plan.Prompt);
        // A cleared task-derived field resolves to the plain base dir (#533 decision 1) — no derivation.
        Assert.Equal(BaseDir, plan.WorkingDir);
        // The base dir lies inside the base tree (it is the tree), so it's created on first use.
        Assert.True(plan.CreateWorkingDir);
        Assert.False(plan.OneOff);
        Assert.False(plan.PostToComments);
        Assert.Equal(LaunchLocation.NewWindow, plan.LaunchLocation);
        Assert.Null(plan.ChosenDir);
    }

    [Fact]
    public void Plan_TaskDerived_PickInsideBaseTree_IsCreated()
    {
        var settings = new AgentDispatchSettings();
        // The pre-fill submits the {base}/{custom-id} (or repo-match) dir as an explicit pick; it lies
        // inside the base tree, so it's created on first use even though it isn't the mode default.
        var inTree = Path.Combine(BaseDir, "ABC-12");
        var request = new DispatchRequest("go", WorkingDirectory: inTree);

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal(inTree, plan.WorkingDir);
        Assert.Equal(inTree, plan.ChosenDir);
        Assert.True(plan.CreateWorkingDir);
    }

    [Fact]
    public void Plan_TaskDerived_PickOutsideBaseTree_IsNotCreated()
    {
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", WorkingDirectory: "/tmp/custom");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        // An explicit pick outside the tree we own overrides the mode and is never created (a typo must
        // not silently make a junk directory).
        Assert.Equal("/tmp/custom", plan.WorkingDir);
        Assert.False(plan.CreateWorkingDir);
        Assert.Equal("/tmp/custom", plan.ChosenDir);
    }

    [Fact]
    public void Plan_ExpandsLeadingTildeInAnExplicitPick()
    {
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", WorkingDirectory: "~/mine");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        var expected = Path.Combine(Home, "mine");
        Assert.Equal(expected, plan.WorkingDir);
        Assert.Equal(expected, plan.ChosenDir);
        Assert.False(plan.CreateWorkingDir); // ~/mine is outside the base tree
    }

    [Fact]
    public void Plan_HomeMode_ResolvesHome_AndIsNotCreated()
    {
        var settings = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home };
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal(Home, plan.WorkingDir);
        Assert.False(plan.CreateWorkingDir); // the home dir is the user's own, outside the base tree
    }

    [Fact]
    public void Plan_FixedMode_ResolvesFixedDir_AndIsNotCreated()
    {
        var settings = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "/opt/fixed",
        };
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal("/opt/fixed", plan.WorkingDir);
        Assert.False(plan.CreateWorkingDir);
    }

    [Fact]
    public void Plan_FixedMode_DirInsideBaseTree_IsStillNotCreated()
    {
        // Decision 4: Home/Fixed are entirely unaffected — never created — even a Fixed dir configured
        // inside the base tree (which the pure containment check alone would otherwise create). The mode
        // gate preserves the pre-#533 "the user's own dir isn't created here" behaviour.
        var settings = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = Path.Combine(BaseDir, "scratch"),
        };
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal(Path.Combine(BaseDir, "scratch"), plan.WorkingDir);
        Assert.False(plan.CreateWorkingDir);
    }

    [Fact]
    public void Plan_CarriesSessionModePostToCommentsAndLaunchLocation()
    {
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest(
            "go",
            SessionMode: AgentSessionMode.OneOff,
            PostToComments: true,
            LaunchLocation: LaunchLocation.NewTab);

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.True(plan.OneOff);
        Assert.True(plan.PostToComments);
        Assert.Equal(LaunchLocation.NewTab, plan.LaunchLocation);
    }

    [Fact]
    public void Plan_CarriesTheConfiguredPromptTemplate()
    {
        var settings = new AgentDispatchSettings { PromptTemplate = "custom {task} template" };
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal("custom {task} template", plan.Template);
    }

    // ── per-dispatch provider override (#498) ────────────────────────────────────────────────────

    private static AgentDispatchSettings TwoProviderSettings() => new()
    {
        Providers =
        [
            new DispatchProvider { Name = "Claude", Executable = "claude" },
            new DispatchProvider { Name = "Codex", Executable = "  codex  ", ExtraArgs = ["  --yolo ", "", "x"] },
        ],
        DefaultProviderName = "Claude",
    };

    [Fact]
    public void Plan_ProviderPick_CarriesTheChosenProvidersCleanedExeAndArgs()
    {
        var request = new DispatchRequest("go", Provider: "Codex");

        var plan = DispatchCoordinator.Plan(TwoProviderSettings(), request, TaskWith(), BaseDir, Home);

        Assert.Equal("codex", plan.ProviderExecutable);       // trimmed
        Assert.Equal(["--yolo", "x"], plan.ProviderExtraArgs); // trimmed, blanks dropped
    }

    [Fact]
    public void Plan_BlankProvider_LeavesTheOverrideNull()
    {
        // A dispatch that never touched the pane's provider control ⇒ no override ⇒ the dispatcher's
        // constructed default options launch unchanged (byte-identical to pre-#498).
        var plan = DispatchCoordinator.Plan(TwoProviderSettings(), new DispatchRequest("go"), TaskWith(), BaseDir, Home);

        Assert.Null(plan.ProviderExecutable);
        Assert.Null(plan.ProviderExtraArgs);
    }

    [Fact]
    public void Plan_UnknownProvider_FallsBackToTheDefaultProvider()
    {
        // A provider deleted between opening the pane and submitting resolves to the default, not a throw.
        var request = new DispatchRequest("go", Provider: "deleted");

        var plan = DispatchCoordinator.Plan(TwoProviderSettings(), request, TaskWith(), BaseDir, Home);

        Assert.Equal("claude", plan.ProviderExecutable);
        Assert.Empty(plan.ProviderExtraArgs!);
    }

    [Fact]
    public void Plan_TaskDerived_NullDefaultWorkingDirectory_FallsBackToTheDefaultBaseDir_AndCreatesIt()
    {
        var settings = new AgentDispatchSettings(); // TaskDerived
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), defaultWorkingDirectory: null, home: Home);

        // A blank/absent base dir resolves to {home}/<default folder>, not to null/empty, and is created.
        var expectedBase = SettingsForm.ResolveDefaultWorkingDirectory(null, Home);
        Assert.Equal(expectedBase, plan.WorkingDir);
        Assert.True(plan.CreateWorkingDir);
    }

    // ── #462 Windows Terminal profile match (Plan's one remaining injected I/O seam) ─────────────

    private static string WtSettings(string startingDir) => $$"""
        { "profiles": { "list": [ { "guid": "{proj}", "name": "Project", "startingDirectory": "{{startingDir}}" } ] } }
        """;

    // A loader that fails the test if it is ever called — proves settings.json is not read on the no-op
    // paths (toggle off, one-off).
    private static Func<string?> NeverLoad => () => throw new InvalidOperationException("settings.json must not be read here");

    [Fact]
    public void Plan_WtProfilesOn_Match_SetsWindowsTerminalProfile()
    {
        var settings = new AgentDispatchSettings { TryUseWindowsTerminalProfiles = true };

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home,
            loadWindowsTerminalSettings: () => WtSettings(BaseDir), expandEnvironment: s => s);

        Assert.Equal(BaseDir, plan.WorkingDir);
        Assert.Equal("{proj}", plan.WindowsTerminalProfile);
    }

    [Fact]
    public void Plan_WtProfilesOff_DoesNotReadSettings_AndLeavesProfileNull()
    {
        var settings = new AgentDispatchSettings(); // toggle off (default)

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home,
            loadWindowsTerminalSettings: NeverLoad, expandEnvironment: s => s);

        Assert.Null(plan.WindowsTerminalProfile);
    }

    [Fact]
    public void Plan_WtProfilesOn_NoMatchingProfile_LeavesProfileNull()
    {
        var settings = new AgentDispatchSettings { TryUseWindowsTerminalProfiles = true };

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home,
            loadWindowsTerminalSettings: () => WtSettings("/somewhere/else"), expandEnvironment: s => s);

        Assert.Null(plan.WindowsTerminalProfile);
    }

    [Fact]
    public void Plan_WtProfilesOn_OneOff_DoesNotReadSettings()
    {
        // A one-off (claude -p) has no terminal, so no profile applies and settings.json is never read.
        var settings = new AgentDispatchSettings { TryUseWindowsTerminalProfiles = true };

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go", SessionMode: AgentSessionMode.OneOff), TaskWith(), BaseDir, Home,
            loadWindowsTerminalSettings: NeverLoad, expandEnvironment: s => s);

        Assert.True(plan.OneOff);
        Assert.Null(plan.WindowsTerminalProfile);
    }

    [Fact]
    public void Plan_WtProfilesOn_NoSettingsFile_LeavesProfileNull()
    {
        var settings = new AgentDispatchSettings { TryUseWindowsTerminalProfiles = true };

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home,
            loadWindowsTerminalSettings: () => null, expandEnvironment: s => s);

        Assert.Null(plan.WindowsTerminalProfile);
    }

    [Fact]
    public void WindowsTerminalProfileNote_NamesTheProfile_OnlyWhenMatchedAndWtActuallyLaunched()
    {
        var settings = new AgentDispatchSettings { TryUseWindowsTerminalProfiles = true };

        var matched = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home,
            loadWindowsTerminalSettings: () => WtSettings(BaseDir), expandEnvironment: s => s);

        // Only when the launch actually used a Windows Terminal host is the profile note emitted.
        var note = DispatchCoordinator.WindowsTerminalProfileNote(matched, "Windows Terminal");
        Assert.NotNull(note);
        Assert.Contains("{proj}", note);
        Assert.Contains("Windows Terminal profile", note);
        Assert.NotNull(DispatchCoordinator.WindowsTerminalProfileNote(matched, "Windows Terminal (new tab)"));

        // A profile matched on directory, but the launch used a non-WT host (an explicit PreferredTerminal,
        // a custom command, or wt absent) — the profile never applied, so no misleading note.
        Assert.Null(DispatchCoordinator.WindowsTerminalProfileNote(matched, "PowerShell (pwsh)"));
        Assert.Null(DispatchCoordinator.WindowsTerminalProfileNote(matched, "Windows PowerShell"));
        // A failed launch (null LaunchedWith) never claims a profile.
        Assert.Null(DispatchCoordinator.WindowsTerminalProfileNote(matched, null));

        // No profile matched → never a note, whatever launched.
        var noMatch = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home,
            loadWindowsTerminalSettings: () => null, expandEnvironment: s => s);
        Assert.Null(DispatchCoordinator.WindowsTerminalProfileNote(noMatch, "Windows Terminal"));
    }

    // ── Split-pane viability floor (#505/#515, slice J) ──────────────────────────────────────────

    [Fact]
    public void Plan_SplitPane_WideTerminal_StaysASplit_NoReason()
    {
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", LaunchLocation: LaunchLocation.SplitPane);

        // 200 cols, even side-by-side (default Auto geometry) → two 100-col panes, above the 60 floor.
        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home, terminalColumns: 200);

        Assert.Equal(LaunchLocation.SplitPane, plan.LaunchLocation);
        Assert.Null(plan.SplitDegradedReason);
    }

    [Fact]
    public void Plan_SplitPane_NarrowTerminal_DegradesToTab_WithReason()
    {
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", LaunchLocation: LaunchLocation.SplitPane);

        // 100 cols, even → two 50-col panes, below the 60 floor → degrade to a tab. This is the
        // "panes accumulate" / repeated-dispatch case: each split shrinks the width the next one sees,
        // so the floor is what stops the Nth dispatch producing an unusable pane.
        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home, terminalColumns: 100);

        Assert.Equal(LaunchLocation.NewTab, plan.LaunchLocation);
        Assert.NotNull(plan.SplitDegradedReason);
        // The LaunchLocation degrades to NewTab, but the human reason must NOT promise a literal "tab":
        // #589/#590 made SplitViability's message host-agnostic ("… opening elsewhere instead") because the
        // NewTab surface can resolve to a Zellij pane or a window fallback (the sibling over-promise #591
        // fixes in AppHostLaunch). Assert the stable explanatory text instead of the retired "tab" wording.
        Assert.Contains("too narrow to split", plan.SplitDegradedReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tab", plan.SplitDegradedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_SplitPane_NoTerminalWidth_LeavesTheSplitUntouched()
    {
        // Every pre-#515 caller passes no width (the default) — and a headless context has no live
        // driver — so the floor self-disables and the launch location is byte-identical to the request.
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", LaunchLocation: LaunchLocation.SplitPane);

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal(LaunchLocation.SplitPane, plan.LaunchLocation);
        Assert.Null(plan.SplitDegradedReason);
    }

    [Fact]
    public void Plan_SplitPane_OneOff_NeverEvaluatesTheFloor()
    {
        // A one-off claude -p run has no terminal, so an in-place location is meaningless — the floor must
        // not fire even on a narrow terminal (it stays the requested value, and the host ignores it).
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", SessionMode: AgentSessionMode.OneOff, LaunchLocation: LaunchLocation.SplitPane);

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home, terminalColumns: 40);

        Assert.True(plan.OneOff);
        Assert.Equal(LaunchLocation.SplitPane, plan.LaunchLocation);
        Assert.Null(plan.SplitDegradedReason);
    }

    [Theory]
    [InlineData(LaunchLocation.NewTab)]
    [InlineData(LaunchLocation.NewWindow)]
    public void Plan_NonSplitRequest_NeverDegrades_EvenOnANarrowTerminal(LaunchLocation location)
    {
        // The floor only ever downgrades an interactive SplitPane → NewTab; a NewTab/NewWindow request is
        // left exactly as asked, whatever the width.
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", LaunchLocation: location);

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home, terminalColumns: 20);

        Assert.Equal(location, plan.LaunchLocation);
        Assert.Null(plan.SplitDegradedReason);
    }

    [Fact]
    public void Plan_SplitPane_DegradingToTab_DoesNotPerturbTheWorkingDirectory()
    {
        // The viability floor reads only the requested launch location — it must not touch the #461 /
        // #96 directory resolution, so a degraded split keeps the exact same working dir / ChosenDir a
        // viable one would have.
        var settings = new AgentDispatchSettings();
        var inTree = Path.Combine(BaseDir, "ABC-12");
        var request = new DispatchRequest("go", WorkingDirectory: inTree, LaunchLocation: LaunchLocation.SplitPane);

        var viable = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home, terminalColumns: 200);
        var degraded = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home, terminalColumns: 100);

        Assert.Equal(LaunchLocation.SplitPane, viable.LaunchLocation);
        Assert.Equal(LaunchLocation.NewTab, degraded.LaunchLocation);
        // The launch-location degradation aside, everything directory-related is identical.
        Assert.Equal(viable.WorkingDir, degraded.WorkingDir);
        Assert.Equal(inTree, degraded.WorkingDir);
        Assert.Equal(viable.ChosenDir, degraded.ChosenDir);
        Assert.Equal(inTree, degraded.ChosenDir);
        Assert.Equal(viable.CreateWorkingDir, degraded.CreateWorkingDir);
    }

    // ── Cache reconciliation (#96), now with the host-supplied AutoDerivedDefault baseline ───────

    [Fact]
    public void ReconcileCache_StoresAnExplicitPick_ThenClearsOnRevertToDefault()
    {
        var cache = new Dictionary<string, string>();
        var settings = new AgentDispatchSettings();

        // First dispatch with an explicit, non-default pick → cache stores it (returns changed=true). The
        // baseline (what the pre-fill would produce) is the task-derived {base}/{custom-id}.
        var pick = DispatchCoordinator.Plan(settings, new DispatchRequest("go", WorkingDirectory: "/tmp/custom"), TaskWith(), BaseDir, Home);
        var baseline = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(TaskWith(), settings, BaseDir, Home, _ => false, _ => []);
        Assert.True(DispatchCoordinator.ReconcileCache(cache, "9xyz", pick.ChosenDir, baseline));
        Assert.Equal("/tmp/custom", cache["9xyz"]);

        // Re-running the identical pick is a no-op (returns changed=false, entry unchanged).
        Assert.False(DispatchCoordinator.ReconcileCache(cache, "9xyz", pick.ChosenDir, baseline));
        Assert.Equal("/tmp/custom", cache["9xyz"]);

        // Reverting to the default (blank pick) clears the entry (returns changed=true).
        var reverted = DispatchCoordinator.Plan(settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home);
        Assert.True(DispatchCoordinator.ReconcileCache(cache, "9xyz", reverted.ChosenDir, baseline));
        Assert.False(cache.ContainsKey("9xyz"));
    }

    [Fact]
    public void ReconcileCache_AcceptingTheAutoDerivedPreFillUnchanged_WritesNoEntry()
    {
        var cache = new Dictionary<string, string>();
        var settings = new AgentDispatchSettings();

        // The pre-fill submits the {base}/{custom-id} dir; the reconciliation baseline is the same value,
        // so accepting it unchanged clears rather than stores an entry (no cache poisoning on every dispatch).
        var accepted = Path.Combine(BaseDir, "ABC-12");
        var plan = DispatchCoordinator.Plan(settings, new DispatchRequest("go", WorkingDirectory: accepted), TaskWith(), BaseDir, Home);
        var baseline = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(TaskWith(), settings, BaseDir, Home, _ => false, _ => []);

        Assert.Equal(accepted, baseline);
        Assert.False(DispatchCoordinator.ReconcileCache(cache, "9xyz", plan.ChosenDir, baseline));
        Assert.False(cache.ContainsKey("9xyz"));
    }

    [Fact]
    public void ReconcileCache_FixedModeTildeExpandedPickEqualToDefault_RevertsTheCache()
    {
        var settings = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "~/fixed",
        };
        var expandedFixed = Path.Combine(Home, "fixed");

        // An explicit "~/fixed" pick expands to the same absolute path the Fixed default resolves to, and
        // AutoDerivedDefault ~-expands to match — so reconciling clears any stored entry.
        var plan = DispatchCoordinator.Plan(settings, new DispatchRequest("go", WorkingDirectory: "~/fixed"), TaskWith(), BaseDir, Home);
        var baseline = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(TaskWith(), settings, BaseDir, Home);
        Assert.Equal(expandedFixed, plan.ChosenDir);
        Assert.Equal(expandedFixed, baseline);

        var cache = new Dictionary<string, string> { ["9xyz"] = "/old/pick" };
        Assert.True(DispatchCoordinator.ReconcileCache(cache, "9xyz", plan.ChosenDir, baseline));
        Assert.False(cache.ContainsKey("9xyz"));
    }

    // ── RememberProvider (persist the last-used pick, #498) ───────────────────────────────────────

    [Fact]
    public void RememberProvider_StoresANewPick_AndReturnsChanged()
    {
        var settings = new AgentDispatchSettings();
        Assert.True(DispatchCoordinator.RememberProvider(settings, "Codex"));
        Assert.Equal("Codex", settings.LastDispatchProviderName);
    }

    [Fact]
    public void RememberProvider_RePickingTheRememberedProvider_IsANoOp()
    {
        var settings = new AgentDispatchSettings { LastDispatchProviderName = "Codex" };
        Assert.False(DispatchCoordinator.RememberProvider(settings, "Codex"));
        Assert.Equal("Codex", settings.LastDispatchProviderName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RememberProvider_BlankPick_IsANoOp_AndLeavesTheRememberedValue(string? pick)
    {
        // A dispatch that never touched the provider control (single-provider host) must not clobber the
        // remembered pick.
        var settings = new AgentDispatchSettings { LastDispatchProviderName = "Codex" };
        Assert.False(DispatchCoordinator.RememberProvider(settings, pick));
        Assert.Equal("Codex", settings.LastDispatchProviderName);
    }
}
