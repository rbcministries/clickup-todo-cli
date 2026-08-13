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
        ScreenContext.RenameTask => HelpItemSets.RenameTask,
        ScreenContext.NewTask => HelpItemSets.NewTask,
        ScreenContext.PromptTemplateEditor => HelpItemSets.PromptTemplateEditor,
        ScreenContext.DispatchProviders => HelpItemSets.DispatchProviders,
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

    // #539 (contextual chords B) moved Settings F2 → F10 to free F2 for the rename slices; H (#545) now
    // claims F2 for the main-list task rename (contextual-chord-model.md §5-H). Pinned so Settings stays
    // on F10 and F2 is used *only* by RenameTask — a future slice can't quietly repurpose it.
    [Fact]
    public void Settings_IsF10_OnMainList_AndF2_IsRenameTaskOnly()
    {
        Assert.Equal("F10", Keybindings.Token(ScreenContext.MainList, KeyAction.Settings));
        Assert.Equal("F2", Keybindings.Token(ScreenContext.MainList, KeyAction.RenameTask));

        // Every F2 binding is the RenameTask action (today only on the main list).
        Assert.All(
            Keybindings.All.Where(e => e.Value == "F2"),
            e => Assert.Equal(KeyAction.RenameTask, e.Key.Action));
    }

    // Split-pane epic E (#507): OpenInSplitPane is a sibling launch mode of OpenInNewTab, not a mode of
    // it — a distinct action on a distinct chord. Pinned so the two never get merged or their chords
    // swapped: new tab stays Ctrl+Enter, split pane is Ctrl+Alt+Enter, and they parse to different keys.
    [Fact]
    public void OpenInSplitPane_IsCtrlAltEnter_DistinctFromNewTabsCtrlEnter()
    {
        Assert.Equal("Ctrl+Enter", Keybindings.Token(ScreenContext.MainList, KeyAction.OpenInNewTab));
        Assert.Equal("Ctrl+Alt+Enter", Keybindings.Token(ScreenContext.MainList, KeyAction.OpenInSplitPane));

        Assert.True(Key.TryParse("Ctrl+Enter", out var newTab));
        Assert.True(Key.TryParse("Ctrl+Alt+Enter", out var splitPane));
        Assert.NotEqual(newTab.KeyCode, splitPane.KeyCode);
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

    // ── Contextual chords C (#540): the Task Detail sub-context layer ────────────────────────────────

    public static readonly TheoryData<DetailSubContext> DetailSubContexts =
        [.. Enum.GetValues<DetailSubContext>()];

    // The crux of #540: the "new" chord resolves to the checklist add on the Checklists tab and to the
    // comment composer on every other tab. AddChecklistItem and AddComment share the token in the base
    // table; the sub-context is what disambiguates which is live.
    [Fact]
    public void ResolveDetail_CtrlN_IsAddChecklistItem_OnChecklistsTab_AndAddComment_Elsewhere()
    {
        Assert.Equal(KeyAction.AddChecklistItem, Keybindings.ResolveDetail(DetailSubContext.Checklists, "Ctrl+N"));
        Assert.Equal(KeyAction.AddComment, Keybindings.ResolveDetail(DetailSubContext.Comments, "Ctrl+N"));
        Assert.Equal(KeyAction.AddComment, Keybindings.ResolveDetail(DetailSubContext.TaskTree, "Ctrl+N"));
        Assert.Equal(KeyAction.AddComment, Keybindings.ResolveDetail(DetailSubContext.Default, "Ctrl+N"));
    }

    // #540 retargets the #458 stopgap: the add-item chord is now Ctrl+N (shared with AddComment), and F7
    // is bound to nothing anywhere. Pinned so a later slice can't silently resurrect F7.
    [Fact]
    public void AddChecklistItem_IsCtrlN_AndNoBindingUsesF7()
    {
        Assert.Equal("Ctrl+N", Keybindings.Token(ScreenContext.Detail, KeyAction.AddChecklistItem));
        Assert.DoesNotContain(Keybindings.All, e => e.Value == "F7");
    }

    // #543 (contextual chords F) retargets the last #458 stopgap: delete moves F9 → the conventional
    // Delete key, and F9 is bound to nothing anywhere. Pinned so a later slice can't resurrect F9 (the
    // sibling of AddChecklistItem_IsCtrlN_AndNoBindingUsesF7 for F7). With #540's F7 and this, none of
    // the #458 F7/F8/F9 stopgaps survive except F8 (rename), which slice D moves to F2.
    [Fact]
    public void DeleteChecklistItem_IsDelete_AndNoBindingUsesF9()
    {
        Assert.Equal("Delete", Keybindings.Token(ScreenContext.Detail, KeyAction.DeleteChecklistItem));
        Assert.DoesNotContain(Keybindings.All, e => e.Value == "F9");
    }

    // The anti-collision invariant the whole sub-context model rests on (contextual-chord-model.md §2.2):
    // within one sub-context no token resolves to two live actions — otherwise ResolveDetail would be
    // ambiguous and the footer could advertise one meaning while dispatch fired another.
    [Theory]
    [MemberData(nameof(DetailSubContexts))]
    public void DetailBindings_HaveNoTokenCollision_WithinASubContext(DetailSubContext sub)
    {
        var tokens = Keybindings.DetailBindings(sub).Select(b => b.Token).ToList();
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    // ResolveDetail round-trips DetailBindings: every live binding resolves back to its own action, and a
    // token no sub-context action binds resolves to null (a chord that tab doesn't own is inert).
    [Theory]
    [MemberData(nameof(DetailSubContexts))]
    public void ResolveDetail_RoundTripsEveryLiveBinding_AndIsNullForAnUnboundToken(DetailSubContext sub)
    {
        foreach (var (action, token) in Keybindings.DetailBindings(sub))
            Assert.Equal(action, Keybindings.ResolveDetail(sub, token));

        Assert.Null(Keybindings.ResolveDetail(sub, "F24"));
    }

    // The display side never drifts from the sub-context table — the #540 generalisation of
    // Footer_ShowsTheTableKey_ForEveryBinding. Every live (action, token) in a sub-context is shown on
    // that sub-context's Task Detail footer under the same token, for both the tree-present and
    // tree-absent footer variants; this is what proves the Ctrl+N label is right per tab.
    [Theory]
    [MemberData(nameof(DetailSubContexts))]
    public void DetailFooter_PerSubContext_ShowsEveryLiveBinding(DetailSubContext sub)
    {
        foreach (var hasTaskTree in new[] { false, true })
        {
            var footer = HelpItemSets.DetailFooter(
                commentComposerVisible: false, descriptionEditorVisible: false, replyPickerVisible: false,
                hasTaskTree: hasTaskTree, sub: sub);

            foreach (var (action, token) in Keybindings.DetailBindings(sub))
                Assert.True(
                    footer.Any(i => i.IsAction && i.ActionKey == token),
                    $"{sub} footer (hasTaskTree={hasTaskTree}) should show {action} under '{token}'");
        }
    }
}
