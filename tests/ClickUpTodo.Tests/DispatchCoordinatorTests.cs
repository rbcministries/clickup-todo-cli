using System.Text.Json;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure half of the shared agent-dispatch coordinator (#345): <see cref="DispatchCoordinator.Plan"/>
/// (the resolution block lifted out of <c>TodoApp.DispatchAgent</c>) and <see cref="DispatchCoordinator.ReconcileCache"/>.
/// Asserting these directly locks the dashboard and single-task hosts to one behaviour. The execution
/// flows (<c>RunInteractive</c>/<c>RunBackground</c>) are Terminal.Gui + real-process glue and aren't
/// CI-testable — covered by <c>tui-validate</c> + manual verification, exactly as the code they replaced.
/// No API / no Terminal.Gui here.
/// </summary>
public sealed class DispatchCoordinatorTests
{
    private const string Home = "/home/tester";
    private const string BaseDir = "/work";

    private static TaskDetail TaskWith(string id = "9xyz", string? customId = "ABC-12")
        => new() { Id = id, Name = "A task", CustomId = customId };

    [Fact]
    public void Plan_TaskDerived_NoPick_ResolvesBaseDirAndSeedsOutputSubdir()
    {
        var settings = new AgentDispatchSettings(); // defaults: TaskDerived, Interactive, NewWindow
        var request = new DispatchRequest("do the thing");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal("do the thing", plan.Prompt);
        Assert.Equal(BaseDir, plan.WorkingDir);
        Assert.True(plan.UseTaskDerived);
        // Task-derived output subdir is the custom id (preferred over the raw id), filesystem-safe.
        Assert.Equal(AgentPromptComposer.OutputSubdirectoryToken(TaskWith()), plan.OutputSubdir);
        Assert.Equal("ABC-12", plan.OutputSubdir);
        Assert.False(plan.OneOff);
        Assert.False(plan.PostToComments);
        Assert.Equal(LaunchLocation.NewWindow, plan.LaunchLocation);
        Assert.Null(plan.ChosenDir);
        Assert.Equal(BaseDir, plan.ResolvedDefault);
    }

    [Fact]
    public void Plan_TaskDerived_ExplicitPick_OverridesModeAndSuppressesSubdir()
    {
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go", WorkingDirectory: "/tmp/custom");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        // An explicit pick (#95) wins over the configured mode and means "no forced ./{id} subdir".
        Assert.Equal("/tmp/custom", plan.WorkingDir);
        Assert.False(plan.UseTaskDerived);
        Assert.Null(plan.OutputSubdir);
        Assert.Equal("/tmp/custom", plan.ChosenDir);
        // resolvedDefault is what the mode would use with no pick — the base dir, for cache reconciliation.
        Assert.Equal(BaseDir, plan.ResolvedDefault);
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
        Assert.False(plan.UseTaskDerived);
    }

    [Fact]
    public void Plan_HomeMode_ResolvesHomeWithNoSubdir()
    {
        var settings = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home };
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal(Home, plan.WorkingDir);
        Assert.False(plan.UseTaskDerived);
        Assert.Null(plan.OutputSubdir);
        Assert.Equal(Home, plan.ResolvedDefault);
    }

    [Fact]
    public void Plan_FixedMode_ResolvesFixedDirWithNoSubdir()
    {
        var settings = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "/opt/fixed",
        };
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal("/opt/fixed", plan.WorkingDir);
        Assert.False(plan.UseTaskDerived);
        Assert.Null(plan.OutputSubdir);
        Assert.Equal("/opt/fixed", plan.ResolvedDefault);
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
    public void Plan_TaskDerivedOutputSubdir_FallsBackToTaskIdWhenNoCustomId()
    {
        var settings = new AgentDispatchSettings();
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(id: "task9", customId: null), BaseDir, Home);

        Assert.Equal("task9", plan.OutputSubdir);
    }

    [Fact]
    public void Plan_CarriesTheConfiguredPromptTemplate()
    {
        var settings = new AgentDispatchSettings { PromptTemplate = "custom {task} template" };
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), BaseDir, Home);

        Assert.Equal("custom {task} template", plan.Template);
    }

    [Fact]
    public void Plan_TaskDerived_NullDefaultWorkingDirectory_FallsBackToTheDefaultBaseDir()
    {
        var settings = new AgentDispatchSettings(); // TaskDerived
        var request = new DispatchRequest("go");

        var plan = DispatchCoordinator.Plan(settings, request, TaskWith(), defaultWorkingDirectory: null, home: Home);

        // A blank/absent base dir resolves to {home}/<default folder>, not to null/empty.
        var expectedBase = SettingsForm.ResolveDefaultWorkingDirectory(null, Home);
        Assert.Equal(expectedBase, plan.WorkingDir);
        Assert.Equal(expectedBase, plan.ResolvedDefault);
        Assert.True(plan.UseTaskDerived);
    }

    [Fact]
    public void Plan_FixedMode_ResolvedDefaultIsTildeExpanded_SoAnEquivalentPickRevertsTheCache()
    {
        var settings = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "~/fixed",
        };
        var expandedFixed = Path.Combine(Home, "fixed");

        // An explicit "~/fixed" pick expands to the same absolute path the Fixed default resolves to,
        // and ResolvedDefault is ~-expanded to match — the case the coordinator comment calls out.
        var plan = DispatchCoordinator.Plan(settings, new DispatchRequest("go", WorkingDirectory: "~/fixed"), TaskWith(), BaseDir, Home);
        Assert.Equal(expandedFixed, plan.ChosenDir);
        Assert.Equal(expandedFixed, plan.ResolvedDefault);

        // So reconciling a pick equal to the (expanded) Fixed default clears any stored entry rather
        // than persisting a redundant one.
        var cache = new Dictionary<string, string> { ["9xyz"] = "/old/pick" };
        Assert.True(DispatchCoordinator.ReconcileCache(cache, "9xyz", plan));
        Assert.False(cache.ContainsKey("9xyz"));
    }

    // ── #461 Repository sub-directory match ──────────────────────────────────

    private static TaskDetail TaskWithRepo(string repoValue, string id = "9xyz", string? customId = "ABC-12")
        => new()
        {
            Id = id,
            Name = "A task",
            CustomId = customId,
            CustomFields = [new CustomFieldItem("Repository", "text", JsonDocument.Parse($"\"{repoValue}\"").RootElement.Clone())],
        };

    private static Func<string, bool> Exists(params string[] dirs)
    {
        var set = new HashSet<string>(dirs, StringComparer.Ordinal);
        return set.Contains;
    }

    private static Func<string, IReadOnlyList<string>> Children(params string[] names) => _ => names;

    [Fact]
    public void Plan_TaskDerived_RepoMatch_LaunchesInCheckoutAndSuppressesOutputSubdir()
    {
        var settings = new AgentDispatchSettings();
        var matched = Path.Combine(BaseDir, "proj");

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWithRepo("proj"), BaseDir, Home,
            Exists(matched), Children("proj"));

        Assert.Equal(matched, plan.WorkingDir);
        // Owner decision: no ./{custom-id} instruction inside a real checkout.
        Assert.Null(plan.OutputSubdir);
        // The base-dir-creation / mode flag is unchanged by a match (the matched dir already exists).
        Assert.True(plan.UseTaskDerived);
        Assert.Equal(matched, plan.RepositoryDir);
        // resolvedDefault reflects the match so an equal explicit pick still reverts the #96 cache.
        Assert.Equal(matched, plan.ResolvedDefault);
    }

    [Fact]
    public void Plan_TaskDerived_RepoValuePresentButNoMatchingDir_IsByteIdenticalToToday()
    {
        var settings = new AgentDispatchSettings();

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWithRepo("proj"), BaseDir, Home,
            Exists(), Children());

        Assert.Equal(BaseDir, plan.WorkingDir);
        Assert.Equal("ABC-12", plan.OutputSubdir); // subdir emitted exactly as today
        Assert.True(plan.UseTaskDerived);
        Assert.Null(plan.RepositoryDir);
        Assert.Equal(BaseDir, plan.ResolvedDefault);
    }

    [Fact]
    public void Plan_TaskDerived_RepoMatch_CaseInsensitiveChildScan()
    {
        var settings = new AgentDispatchSettings();
        var onDisk = Path.Combine(BaseDir, "My-Proj");

        // Exact-case `/work/my-proj` is absent (case-sensitive FS), but a `My-Proj` child exists.
        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWithRepo("my-proj"), BaseDir, Home,
            Exists(onDisk), Children("My-Proj"));

        Assert.Equal(onDisk, plan.WorkingDir);
        Assert.Equal(onDisk, plan.RepositoryDir);
    }

    [Fact]
    public void Plan_ExplicitPickEqualToMatchedDir_RevertsTheCache()
    {
        var settings = new AgentDispatchSettings();
        var matched = Path.Combine(BaseDir, "proj");

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go", WorkingDirectory: matched), TaskWithRepo("proj"), BaseDir, Home,
            Exists(matched), Children("proj"));

        Assert.Equal(matched, plan.WorkingDir);
        Assert.False(plan.UseTaskDerived);     // an explicit pick, not the mode
        Assert.Null(plan.OutputSubdir);
        Assert.Null(plan.RepositoryDir);       // the pick, not the match, drove the dir → nothing to report
        Assert.Equal(matched, plan.ResolvedDefault);

        // So a pick equal to the (repo-matched) default clears any stored entry rather than persisting it.
        var cache = new Dictionary<string, string> { ["9xyz"] = "/old/pick" };
        Assert.True(DispatchCoordinator.ReconcileCache(cache, "9xyz", plan));
        Assert.False(cache.ContainsKey("9xyz"));
    }

    [Fact]
    public void Plan_HomeMode_WithRepositoryField_IsUnaffectedAndNeverProbes()
    {
        var settings = new AgentDispatchSettings { WorkingDirectory = AgentWorkingDirectory.Home };
        var probed = false;

        var plan = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWithRepo("proj"), BaseDir, Home,
            _ => { probed = true; return true; },
            _ => { probed = true; return []; });

        Assert.Equal(Home, plan.WorkingDir);
        Assert.Null(plan.RepositoryDir);
        Assert.False(probed); // repo matching is task-derived-only
    }

    [Fact]
    public void RepositoryMatchNote_NamesTheDirectory_OnlyWhenAMatchDroveTheWorkingDir()
    {
        var settings = new AgentDispatchSettings();
        var matched = Path.Combine(BaseDir, "proj");

        var withMatch = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWithRepo("proj"), BaseDir, Home,
            Exists(matched), Children("proj"));
        var note = DispatchCoordinator.RepositoryMatchNote(withMatch);
        Assert.NotNull(note);
        Assert.Contains(matched, note);
        Assert.Contains("Repository", note);

        var noMatch = DispatchCoordinator.Plan(
            settings, new DispatchRequest("go"), TaskWithRepo("proj"), BaseDir, Home, Exists(), Children());
        Assert.Null(DispatchCoordinator.RepositoryMatchNote(noMatch));
    }

    [Fact]
    public void ReconcileCache_StoresAnExplicitPick_ThenClearsOnRevertToDefault()
    {
        var cache = new Dictionary<string, string>();
        var settings = new AgentDispatchSettings();

        // First dispatch with an explicit, non-default pick → cache stores it (returns changed=true).
        var pick = DispatchCoordinator.Plan(settings, new DispatchRequest("go", WorkingDirectory: "/tmp/custom"), TaskWith(), BaseDir, Home);
        Assert.True(DispatchCoordinator.ReconcileCache(cache, "9xyz", pick));
        Assert.Equal("/tmp/custom", cache["9xyz"]);

        // Re-running the identical pick is a no-op (returns changed=false, entry unchanged).
        Assert.False(DispatchCoordinator.ReconcileCache(cache, "9xyz", pick));
        Assert.Equal("/tmp/custom", cache["9xyz"]);

        // Reverting to the default (blank pick) clears the entry (returns changed=true).
        var reverted = DispatchCoordinator.Plan(settings, new DispatchRequest("go"), TaskWith(), BaseDir, Home);
        Assert.True(DispatchCoordinator.ReconcileCache(cache, "9xyz", reverted));
        Assert.False(cache.ContainsKey("9xyz"));
    }
}
