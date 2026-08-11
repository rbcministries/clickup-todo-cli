using System.Text.Json;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="DispatchWorkingDirectoryPreFill"/> (#533) — the single place a task-derived
/// Dispatch working directory is decided. Asserting the pure precedence directly locks the dashboard and
/// single-task hosts (which both call it through the shared <c>workingDirectoryPreFill</c> delegate) to
/// one behaviour. No API / no Terminal.Gui here; the filesystem is injected.
/// </summary>
public sealed class DispatchWorkingDirectoryPreFillTests
{
    private const string Home = "/home/tester";
    private const string BaseDir = "/work";

    private static TaskDetail Task(string id = "9xyz", string? customId = "ABC-12", string? repo = null)
        => new()
        {
            Id = id,
            Name = "A task",
            CustomId = customId,
            CustomFields = repo is null
                ? []
                : [new CustomFieldItem("Repository", "text", JsonDocument.Parse($"\"{repo}\"").RootElement.Clone())],
        };

    private static AgentDispatchSettings TaskDerived => new(); // default mode
    private static AgentDispatchSettings HomeMode => new() { WorkingDirectory = AgentWorkingDirectory.Home };

    private static Func<string, bool> Exists(params string[] dirs)
    {
        var set = new HashSet<string>(dirs, StringComparer.Ordinal);
        return set.Contains;
    }

    private static Func<string, IReadOnlyList<string>> Children(params string[] names) => _ => names;

    private static Dictionary<string, string> NoCache() => new();

    // ── PreFill: task-derived precedence (cache → repo match → {base}/{custom-id}) ──────────────

    [Fact]
    public void PreFill_TaskDerived_CacheWins()
    {
        var cache = new Dictionary<string, string> { ["9xyz"] = "/my/explicit/pick" };
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            cache, "9xyz", Task(repo: "proj"), TaskDerived, BaseDir,
            Exists(Path.Combine(BaseDir, "proj")), Children("proj"));

        Assert.Equal("/my/explicit/pick", result); // the #96 cache beats a repo match
    }

    [Fact]
    public void PreFill_TaskDerived_RepoMatch_ReturnsCheckoutDir()
    {
        var matched = Path.Combine(BaseDir, "proj");
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            NoCache(), "9xyz", Task(repo: "proj"), TaskDerived, BaseDir, Exists(matched), Children("proj"));

        Assert.Equal(matched, result);
    }

    [Fact]
    public void PreFill_TaskDerived_NoRepoField_ReturnsCustomIdSubdir()
    {
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            NoCache(), "9xyz", Task(customId: "ABC-12"), TaskDerived, BaseDir, Exists(), Children());

        Assert.Equal(Path.Combine(BaseDir, "ABC-12"), result);
    }

    [Fact]
    public void PreFill_TaskDerived_RepoFieldButNoMatchingDir_FallsBackToCustomIdSubdir()
    {
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            NoCache(), "9xyz", Task(customId: "ABC-12", repo: "proj"), TaskDerived, BaseDir, Exists(), Children());

        Assert.Equal(Path.Combine(BaseDir, "ABC-12"), result);
    }

    [Fact]
    public void PreFill_TaskDerived_BlankCustomId_FallsBackToTaskIdSubdir()
    {
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            NoCache(), "task9", Task(id: "task9", customId: null), TaskDerived, BaseDir, Exists(), Children());

        Assert.Equal(Path.Combine(BaseDir, "task9"), result);
    }

    [Fact]
    public void PreFill_TaskDerived_RepoMatch_CaseInsensitiveChildScan()
    {
        var onDisk = Path.Combine(BaseDir, "My-Proj");
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            NoCache(), "9xyz", Task(repo: "my-proj"), TaskDerived, BaseDir, Exists(onDisk), Children("My-Proj"));

        Assert.Equal(onDisk, result);
    }

    // ── PreFill: Home / Fixed are untouched (decision 4) ────────────────────────────────────────

    [Fact]
    public void PreFill_HomeMode_NoCache_IsBlank_NoDerivation()
    {
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            NoCache(), "9xyz", Task(repo: "proj"), HomeMode, BaseDir,
            _ => throw new InvalidOperationException("Home mode must not probe the filesystem"),
            _ => throw new InvalidOperationException("Home mode must not probe the filesystem"));

        Assert.Equal("", result); // blank ⇒ "use my configured mode"
    }

    [Fact]
    public void PreFill_FixedMode_NoCache_IsBlank()
    {
        var fixedSettings = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "/opt/fixed",
        };
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            NoCache(), "9xyz", Task(), fixedSettings, BaseDir, Exists(), Children());

        Assert.Equal("", result);
    }

    [Fact]
    public void PreFill_HomeMode_CacheStillPreFills_UnchangedFromToday()
    {
        var cache = new Dictionary<string, string> { ["9xyz"] = "/my/pick" };
        var result = DispatchWorkingDirectoryPreFill.PreFill(
            cache, "9xyz", Task(), HomeMode, BaseDir, Exists(), Children());

        Assert.Equal("/my/pick", result); // the #96 cache is mode-independent
    }

    // ── AutoDerivedDefault: the cache-reconciliation baseline (excludes the cache) ───────────────

    [Fact]
    public void AutoDerivedDefault_TaskDerived_NoMatch_IsCustomIdSubdir()
    {
        var result = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(
            Task(customId: "ABC-12"), TaskDerived, BaseDir, Home, Exists(), Children());

        Assert.Equal(Path.Combine(BaseDir, "ABC-12"), result);
    }

    [Fact]
    public void AutoDerivedDefault_TaskDerived_RepoMatch_IsCheckoutDir()
    {
        var matched = Path.Combine(BaseDir, "proj");
        var result = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(
            Task(repo: "proj"), TaskDerived, BaseDir, Home, Exists(matched), Children("proj"));

        Assert.Equal(matched, result);
    }

    [Fact]
    public void AutoDerivedDefault_HomeMode_IsHome()
    {
        var result = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(Task(), HomeMode, BaseDir, Home);
        Assert.Equal(Home, result);
    }

    [Fact]
    public void AutoDerivedDefault_FixedMode_IsTildeExpanded()
    {
        var settings = new AgentDispatchSettings
        {
            WorkingDirectory = AgentWorkingDirectory.Fixed,
            FixedWorkingDirectory = "~/fixed",
        };
        var result = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(Task(), settings, BaseDir, Home);
        Assert.Equal(Path.Combine(Home, "fixed"), result);
    }

    // ── The subtle invariant: an accepted-unchanged pre-fill == AutoDerivedDefault, so the #96 cache
    //    is cleared (not poisoned) on every dispatch. Verified for both the repo-match and no-match legs.

    [Fact]
    public void PreFill_NoCache_EqualsAutoDerivedDefault_TaskDerived_NoMatch()
    {
        var task = Task(customId: "ABC-12", repo: "proj");
        var prefill = DispatchWorkingDirectoryPreFill.PreFill(NoCache(), "9xyz", task, TaskDerived, BaseDir, Exists(), Children());
        var baseline = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(task, TaskDerived, BaseDir, Home, Exists(), Children());

        Assert.Equal(baseline, prefill);
    }

    [Fact]
    public void PreFill_NoCache_EqualsAutoDerivedDefault_TaskDerived_RepoMatch()
    {
        var matched = Path.Combine(BaseDir, "proj");
        var task = Task(repo: "proj");
        var prefill = DispatchWorkingDirectoryPreFill.PreFill(NoCache(), "9xyz", task, TaskDerived, BaseDir, Exists(matched), Children("proj"));
        var baseline = DispatchWorkingDirectoryPreFill.AutoDerivedDefault(task, TaskDerived, BaseDir, Home, Exists(matched), Children("proj"));

        Assert.Equal(baseline, prefill);
        Assert.Equal(matched, prefill);
    }
}
