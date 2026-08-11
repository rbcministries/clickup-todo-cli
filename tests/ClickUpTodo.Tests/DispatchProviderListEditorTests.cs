using ClickUpTodo.Configuration;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure multi-provider dispatch editor (#547): the add / rename-dedup / set-exe /
/// set-args / delete / set-default / build logic the (CI-untestable) <c>DispatchProvidersScreen</c> is a
/// thin shell over.
/// </summary>
public sealed class DispatchProviderListEditorTests
{
    private static List<DispatchProvider> Two() =>
    [
        new() { Name = "Claude", Executable = "claude", ExtraArgs = [] },
        new() { Name = "Codex", Executable = "codex", ExtraArgs = ["--model", "o"] },
    ];

    // ── seeding ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyList_SeedsASingleBuiltInDefault()
    {
        var editor = new DispatchProviderListEditor([], "");

        var only = Assert.Single(editor.Providers);
        Assert.Equal(AgentDispatchSettings.DefaultProviderDisplayName, only.Name);
        Assert.Equal(AgentDispatchSettings.DefaultExecutable, only.Executable);
        Assert.Empty(only.ExtraArgs);
        Assert.Equal(AgentDispatchSettings.DefaultProviderDisplayName, editor.DefaultProviderName);
        Assert.True(editor.IsDefault(0));
    }

    [Fact]
    public void NullList_SeedsASingleBuiltInDefault()
    {
        var editor = new DispatchProviderListEditor(null, null);
        Assert.Single(editor.Providers);
        Assert.Equal(AgentDispatchSettings.DefaultProviderDisplayName, editor.DefaultProviderName);
    }

    [Fact]
    public void Constructor_WorksOnDeepCopies_NotTheCallersList()
    {
        var source = Two();
        var editor = new DispatchProviderListEditor(source, "Claude");

        source[0].Executable = "mutated";
        source[0].ExtraArgs.Add("mutated");
        source.Add(new DispatchProvider { Name = "Extra" });

        Assert.Equal(2, editor.Count);
        Assert.Equal("claude", editor.Providers[0].Executable);
        Assert.Empty(editor.Providers[0].ExtraArgs);
    }

    [Fact]
    public void Constructor_DedupesDuplicateIncomingNames_KeepingTheFirst_SoOnlyOneIsDefault()
    {
        List<DispatchProvider> dupes =
        [
            new() { Name = "Claude", Executable = "claude" },
            new() { Name = "Claude", Executable = "claude-2" },
        ];

        var editor = new DispatchProviderListEditor(dupes, "Claude");

        Assert.Equal("Claude", editor.Providers[0].Name);
        Assert.Equal("Claude (2)", editor.Providers[1].Name);
        // Exactly one provider is the default (the first, whose name is unchanged).
        Assert.True(editor.IsDefault(0));
        Assert.False(editor.IsDefault(1));
    }

    [Fact]
    public void Constructor_RepairsABlankIncomingName()
    {
        var editor = new DispatchProviderListEditor([new DispatchProvider { Name = "  ", Executable = "x" }], "");
        Assert.Equal("Provider", Assert.Single(editor.Providers).Name);
    }

    [Fact]
    public void Constructor_LeavesUniqueNamesUnchanged()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        Assert.Equal("Claude", editor.Providers[0].Name);
        Assert.Equal("Codex", editor.Providers[1].Name);
    }

    [Fact]
    public void DefaultName_ResolvesToMatch_ElseFirst()
    {
        Assert.Equal("Codex", new DispatchProviderListEditor(Two(), "Codex").DefaultProviderName);
        // Unmatched name (incl. a case-only mismatch — comparison is Ordinal) falls back to the first.
        Assert.Equal("Claude", new DispatchProviderListEditor(Two(), "missing").DefaultProviderName);
        Assert.Equal("Claude", new DispatchProviderListEditor(Two(), "codex").DefaultProviderName);
    }

    // ── add ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Add_AppendsAUniquelyNamedClaudeProvider_AndReturnsItsIndex()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");

        var i = editor.Add();

        Assert.Equal(2, i);
        Assert.Equal(3, editor.Count);
        Assert.Equal("New provider", editor.Providers[2].Name);
        Assert.Equal("claude", editor.Providers[2].Executable);
        // The default is unchanged by an add.
        Assert.Equal("Claude", editor.DefaultProviderName);
    }

    [Fact]
    public void Add_Twice_DedupesTheGeneratedName()
    {
        var editor = new DispatchProviderListEditor([], "");

        editor.Add();
        editor.Add();

        Assert.Equal("New provider", editor.Providers[1].Name);
        Assert.Equal("New provider (2)", editor.Providers[2].Name);
    }

    // ── rename + dedup ─────────────────────────────────────────────────────────────

    [Fact]
    public void SetName_TrimsAndApplies()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetName(1, "  Local GPT  ");
        Assert.Equal("Local GPT", editor.Providers[1].Name);
    }

    [Fact]
    public void SetName_Blank_FallsBackToProvider()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetName(1, "   ");
        Assert.Equal("Provider", editor.Providers[1].Name);
    }

    [Fact]
    public void SetName_CollidingName_GetsANumericSuffix()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetName(1, "Claude");
        Assert.Equal("Claude (2)", editor.Providers[1].Name);
    }

    [Fact]
    public void SetName_ToItsOwnCurrentName_KeepsIt_NoSuffix()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetName(0, "Claude");
        Assert.Equal("Claude", editor.Providers[0].Name);
    }

    [Fact]
    public void SetName_OfTheDefault_MovesTheDefaultToTheNewName()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");

        editor.SetName(0, "Claude Opus");

        Assert.Equal("Claude Opus", editor.DefaultProviderName);
        Assert.True(editor.IsDefault(0));
    }

    [Fact]
    public void SetName_OfANonDefault_LeavesTheDefault()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetName(1, "Renamed");
        Assert.Equal("Claude", editor.DefaultProviderName);
    }

    // ── executable + args ──────────────────────────────────────────────────────────

    [Fact]
    public void SetExecutable_Trims()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetExecutable(0, "  claude-x  ");
        Assert.Equal("claude-x", editor.Providers[0].Executable);
    }

    [Fact]
    public void SetExtraArgs_TrimsAndDropsBlanks()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetExtraArgs(0, ["  --model ", "  ", "opus"]);
        Assert.Equal(["--model", "opus"], editor.Providers[0].ExtraArgs);
    }

    // ── set default ────────────────────────────────────────────────────────────────

    [Fact]
    public void SetDefault_ChoosesThatProvider()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");

        editor.SetDefault(1);

        Assert.Equal("Codex", editor.DefaultProviderName);
        Assert.True(editor.IsDefault(1));
        Assert.False(editor.IsDefault(0));
    }

    // ── delete ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_ANonDefault_KeepsTheDefault()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");

        var select = editor.Delete(1);

        Assert.Single(editor.Providers);
        Assert.Equal("Claude", editor.DefaultProviderName);
        Assert.Equal(0, select);
    }

    [Fact]
    public void Delete_TheDefault_ReassignsToTheFirstRemaining()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");

        editor.Delete(0);

        Assert.Equal("Codex", Assert.Single(editor.Providers).Name);
        Assert.Equal("Codex", editor.DefaultProviderName);
        Assert.True(editor.IsDefault(0));
    }

    [Fact]
    public void Delete_TheLastProvider_ReSeedsTheBuiltInDefault()
    {
        var editor = new DispatchProviderListEditor([new DispatchProvider { Name = "Only", Executable = "x" }], "Only");

        var select = editor.Delete(0);

        var only = Assert.Single(editor.Providers);
        Assert.Equal(AgentDispatchSettings.DefaultProviderDisplayName, only.Name);
        Assert.Equal(AgentDispatchSettings.DefaultExecutable, only.Executable);
        Assert.Equal(AgentDispatchSettings.DefaultProviderDisplayName, editor.DefaultProviderName);
        Assert.Equal(0, select);
    }

    [Fact]
    public void Delete_OutOfRange_IsANoOp()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.Delete(5);
        editor.Delete(-1);
        Assert.Equal(2, editor.Count);
    }

    // ── build / normalize ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_CoalescesBlankExecutableToClaude_AndCleansArgs()
    {
        var editor = new DispatchProviderListEditor([], "");
        editor.SetExecutable(0, "   ");
        editor.SetExtraArgs(0, ["--x", " ", "--y "]);

        var result = editor.Build();

        var only = Assert.Single(result.Providers);
        Assert.Equal("claude", only.Executable);
        Assert.Equal(["--x", "--y"], only.ExtraArgs);
    }

    [Fact]
    public void Build_ReturnsDeepCopies_IsolatedFromLaterMutation()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");

        var result = editor.Build();
        editor.SetExecutable(0, "changed");
        editor.SetName(0, "Renamed");

        Assert.Equal("claude", result.Providers[0].Executable);
        Assert.Equal("Claude", result.Providers[0].Name);
    }

    [Fact]
    public void Build_CarriesTheChosenDefault()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetDefault(1);

        Assert.Equal("Codex", editor.Build().DefaultProviderName);
    }

    [Fact]
    public void Build_PreservesTheProviderKind()
    {
        var editor = new DispatchProviderListEditor(
            [new DispatchProvider { Name = "A", Executable = "a", Kind = DispatchProviderKind.LocalCli }], "A");

        Assert.Equal(DispatchProviderKind.LocalCli, Assert.Single(editor.Build().Providers).Kind);
    }

    // ── round-trip through AgentDispatchSettings ───────────────────────────────────

    [Fact]
    public void Build_ProjectsTheChosenDefault_ThroughToLauncherOptions()
    {
        var editor = new DispatchProviderListEditor(Two(), "Claude");
        editor.SetDefault(1);
        var result = editor.Build();

        var settings = new AgentDispatchSettings { Providers = result.Providers, DefaultProviderName = result.DefaultProviderName };
        var options = settings.ToLauncherOptions();

        Assert.Equal("codex", options.ClaudeExecutable);
        Assert.Equal(["--model", "o"], options.ExtraArgs);
    }
}
