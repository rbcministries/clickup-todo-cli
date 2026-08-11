using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Input;

namespace ClickUpTodo.Tests;

/// <summary>
/// Guards the central <c>(context, action) → key</c> table (#355): that it is internally consistent
/// (a given action uses one key everywhere it appears — generalizing #290's Quick-Updates/Refresh
/// guards) and that the contextual help footer (<see cref="HelpItemSets"/>) never drifts from it.
/// </summary>
public sealed class KeybindingsTests
{
    /// <summary>The one <see cref="HelpItemSets"/> set each <see cref="ScreenContext"/> renders.</summary>
    private static IReadOnlyList<HelpItem> FooterFor(ScreenContext context) => context switch
    {
        ScreenContext.MainList => HelpItemSets.MainList,
        ScreenContext.Detail => HelpItemSets.Detail,
        ScreenContext.DetailDescriptionEditor => HelpItemSets.DetailDescriptionEditor,
        ScreenContext.Settings => HelpItemSets.Settings,
        ScreenContext.FilterSortGroup => HelpItemSets.FilterSortGroup,
        ScreenContext.QuickUpdates => HelpItemSets.QuickUpdates,
        ScreenContext.QuickOpen => HelpItemSets.QuickOpen,
        ScreenContext.NewTask => HelpItemSets.NewTask,
        ScreenContext.PromptTemplateEditor => HelpItemSets.PromptTemplateEditor,
        ScreenContext.NotificationsFeed => HelpItemSets.NotificationsFeed,
        ScreenContext.AgentRun => HelpItemSets.AgentRun,
        ScreenContext.Help => HelpItemSets.Help,
        ScreenContext.ExitConfirm => HelpItemSets.ExitConfirm,
        _ => throw new ArgumentOutOfRangeException(nameof(context)),
    };

    // The heart of #355, generalizing #290: a semantic action resolves to the same key in *every*
    // context that binds it — so "quick update" is Ctrl+U on both the list and Detail, "refresh" is F5
    // everywhere, "help" is F1 everywhere, and so on. A drift (e.g. rebinding refresh on one screen)
    // fails here.
    [Theory]
    [MemberData(nameof(GovernedActions))]
    public void AllBindingsOfAnAction_ShareOneKey(KeyAction action)
    {
        var keys = Keybindings.All
            .Where(e => e.Key.Action == action)
            .Select(e => e.Value)
            .Distinct()
            .ToList();

        Assert.Single(keys);
    }

    public static readonly TheoryData<KeyAction> GovernedActions =
        [.. Keybindings.All.Select(e => e.Key.Action).Distinct()];

    // The display side never drifts from the source of truth: every table binding is shown on that
    // context's footer, under the same (parseable) key. This is what lets HelpItemSets stay hand-written
    // while remaining provably in sync — the "asserts against it" half of the acceptance criteria.
    [Theory]
    [MemberData(nameof(AllBindings))]
    public void Footer_ShowsTheTableKey_ForEveryBinding(ScreenContext context, KeyAction action, string token)
    {
        var footer = FooterFor(context);

        Assert.True(
            footer.Any(i => i.IsAction && i.ActionKey == token),
            $"{context} footer should show {action} under '{token}'");
    }

    public static readonly TheoryData<ScreenContext, KeyAction, string> AllBindings = BuildAllBindings();

    private static TheoryData<ScreenContext, KeyAction, string> BuildAllBindings()
    {
        var data = new TheoryData<ScreenContext, KeyAction, string>();
        foreach (var ((context, action), token) in Keybindings.All)
            data.Add(context, action, token);
        return data;
    }

    // Every token in the table is a key Terminal.Gui can actually parse — otherwise a dispatcher built
    // from it would silently bind nothing (mirrors HelpLineTests.EveryActionItem_ReRaisesAParseableKey).
    [Theory]
    [MemberData(nameof(AllBindings))]
    public void EveryToken_IsParseable(ScreenContext context, KeyAction action, string token)
    {
        _ = context;
        _ = action;
        Assert.True(Key.TryParse(token, out _), $"'{token}' should parse");
    }

    // The specific #290 invariants, pinned by name so a regression reads clearly.
    [Fact]
    public void QuickUpdate_IsCtrlU_OnListAndDetail()
    {
        Assert.Equal("Ctrl+U", Keybindings.Token(ScreenContext.MainList, KeyAction.QuickUpdate));
        Assert.Equal("Ctrl+U", Keybindings.Token(ScreenContext.Detail, KeyAction.QuickUpdate));
    }

    [Fact]
    public void Refresh_IsF5_OnEveryRefreshableContext()
    {
        Assert.Equal("F5", Keybindings.Token(ScreenContext.MainList, KeyAction.Refresh));
        Assert.Equal("F5", Keybindings.Token(ScreenContext.Detail, KeyAction.Refresh));
        Assert.Equal("F5", Keybindings.Token(ScreenContext.NotificationsFeed, KeyAction.Refresh));
    }

    [Fact]
    public void Help_IsF1_AndBack_IsEsc_Everywhere()
    {
        foreach (var (key, token) in Keybindings.All)
        {
            if (key.Action == KeyAction.Help)
                Assert.Equal("F1", token);
            if (key.Action == KeyAction.Back)
                Assert.Equal("Esc", token);
        }
    }

    // #539 (contextual chords B): Settings moved F2 → F10 to free F2 for the later rename slices
    // (D #541 / E #542 / H #545). Pinned so a future run can't re-introduce an F2 binding without
    // deciding the #538 rename model first — F2 must stay bound to nothing in every context.
    [Fact]
    public void Settings_IsF10_OnMainList_AndNoBindingUsesF2()
    {
        Assert.Equal("F10", Keybindings.Token(ScreenContext.MainList, KeyAction.Settings));
        Assert.DoesNotContain(Keybindings.All, e => e.Value == "F2");
    }

    // The Help screen is the help; it must not bind a Help action (it would advertise F1 → itself).
    [Fact]
    public void HelpContext_DoesNotBindHelp()
        => Assert.False(Keybindings.TryToken(ScreenContext.Help, KeyAction.Help, out _));

    [Fact]
    public void Token_ThrowsForAnUnboundPair()
        => Assert.Throws<KeyNotFoundException>(
            () => Keybindings.Token(ScreenContext.Help, KeyAction.QuickUpdate));

    [Fact]
    public void ActionsFor_ReturnsOnlyThatContextsActions()
    {
        var mainList = Keybindings.ActionsFor(ScreenContext.MainList).ToList();

        Assert.Contains(KeyAction.QuickUpdate, mainList);
        Assert.Contains(KeyAction.Quit, mainList);
        // Detail-only commands never leak into the list context.
        Assert.DoesNotContain(KeyAction.DispatchToClaude, mainList);
        Assert.DoesNotContain(KeyAction.EditDescription, mainList);
    }
}
