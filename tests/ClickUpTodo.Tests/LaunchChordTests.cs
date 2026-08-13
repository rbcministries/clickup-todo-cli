using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Input;

namespace ClickUpTodo.Tests;

/// <summary>
/// The configurable launch-chord override layer (#506, split-pane epic #502 slice D): the two app-wide
/// launch gestures (<see cref="KeyAction.OpenInNewTab"/> = Ctrl+Enter, <see cref="KeyAction.OpenInSplitPane"/>
/// = Ctrl+Alt+Enter) can be rebound via config. These guard that the override resolves ahead of the default,
/// an invalid persisted override is dropped (load-time defense), a bad/colliding chord is rejected at save
/// time, and — the whole point of the seam — the footer and the dispatcher agree on a rebound gesture.
/// </summary>
public sealed class LaunchChordTests
{
    private static LaunchChordOverrides Overrides(string? newTab = null, string? splitPane = null)
        => LaunchChordOverrides.FromConfig(new LaunchChordSettings { NewTab = newTab, SplitPane = splitPane });

    private static Key Parse(string token)
    {
        Assert.True(Key.TryParse(token, out var key), $"'{token}' should parse");
        return key;
    }

    // ── LaunchChordOverrides.FromConfig / For ────────────────────────────────────────────────────────

    [Fact]
    public void FromConfig_KeepsAValidToken_AndResolvesItForItsAction()
    {
        var overrides = Overrides(newTab: "Alt+Enter");

        Assert.Equal("Alt+Enter", overrides.For(KeyAction.OpenInNewTab));
        Assert.Null(overrides.For(KeyAction.OpenInSplitPane));
        Assert.True(overrides.HasAny);
    }

    [Fact]
    public void FromConfig_DropsAnUnparseableToken_LoadTimeDefense()
    {
        // A corrupt / hand-edited / older-or-newer override must degrade to the default, never crash.
        var overrides = Overrides(newTab: "NotAKey", splitPane: "Alt+Enter");

        Assert.Null(overrides.For(KeyAction.OpenInNewTab));
        Assert.Equal("Alt+Enter", overrides.For(KeyAction.OpenInSplitPane));
    }

    [Fact]
    public void FromConfig_TrimsWhitespace_AndTreatsBlankAsNoOverride()
    {
        Assert.Equal("Alt+Enter", Overrides(newTab: "  Alt+Enter  ").For(KeyAction.OpenInNewTab));
        Assert.Null(Overrides(newTab: "   ").For(KeyAction.OpenInNewTab));
    }

    [Fact]
    public void FromConfig_Null_OrDefaultSettings_IsNone()
    {
        Assert.False(LaunchChordOverrides.FromConfig(null).HasAny);
        Assert.False(LaunchChordOverrides.FromConfig(new LaunchChordSettings()).HasAny);
        Assert.False(LaunchChordOverrides.None.HasAny);
    }

    [Fact]
    public void For_ReturnsNull_ForANonLaunchAction()
        => Assert.Null(Overrides(newTab: "Alt+Enter").For(KeyAction.QuickUpdate));

    // ── Keybindings.Token override overload ──────────────────────────────────────────────────────────

    [Fact]
    public void Token_WithOverride_ReturnsOverrideForLaunchActions_DefaultForEverythingElse()
    {
        var overrides = Overrides(newTab: "Alt+Enter", splitPane: "Shift+Enter");

        Assert.Equal("Alt+Enter", Keybindings.Token(ScreenContext.MainList, KeyAction.OpenInNewTab, overrides));
        Assert.Equal("Shift+Enter", Keybindings.Token(ScreenContext.MainList, KeyAction.OpenInSplitPane, overrides));
        // The same override applies wherever the action is bound (one key app-wide).
        Assert.Equal("Alt+Enter", Keybindings.Token(ScreenContext.QuickOpen, KeyAction.OpenInNewTab, overrides));
        // A non-launch action is unaffected.
        Assert.Equal("Ctrl+U", Keybindings.Token(ScreenContext.MainList, KeyAction.QuickUpdate, overrides));
    }

    [Fact]
    public void Token_WithNoOverride_MatchesTheParameterlessDefault()
    {
        foreach (var ((context, action), _) in Keybindings.All)
            Assert.Equal(
                Keybindings.Token(context, action),
                Keybindings.Token(context, action, LaunchChordOverrides.None));
    }

    [Fact]
    public void TryToken_WithOverride_AppliesIt_AndStillFailsForAnUnboundPair()
    {
        var overrides = Overrides(newTab: "Alt+Enter");

        Assert.True(Keybindings.TryToken(ScreenContext.MainList, KeyAction.OpenInNewTab, overrides, out var token));
        Assert.Equal("Alt+Enter", token);
        Assert.False(Keybindings.TryToken(ScreenContext.Help, KeyAction.QuickUpdate, overrides, out _));
    }

    [Fact]
    public void ContextsBinding_LaunchActions_AreTheMainListAndQuickOpen()
    {
        var contexts = Keybindings.ContextsBinding(KeyAction.OpenInNewTab).ToList();

        Assert.Contains(ScreenContext.MainList, contexts);
        Assert.Contains(ScreenContext.QuickOpen, contexts);
        // Task Detail hardcodes the launch gestures outside the table, so it isn't in the binding set.
        Assert.DoesNotContain(ScreenContext.Detail, contexts);
    }

    // ── SettingsForm.ValidateLaunchChord ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_AcceptsAFreeParseableChord()
        => Assert.True(
            SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, "Alt+Enter", LaunchChordOverrides.None).IsValid);

    [Fact]
    public void Validate_AcceptsBlank_AsClearingTheOverride()
    {
        Assert.True(SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, "", LaunchChordOverrides.None).IsValid);
        Assert.True(SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, "   ", LaunchChordOverrides.None).IsValid);
        Assert.True(SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, null, LaunchChordOverrides.None).IsValid);
    }

    [Fact]
    public void Validate_RejectsAnUnparseableToken_WithAMessage()
    {
        var result = SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, "wat", LaunchChordOverrides.None);

        Assert.False(result.IsValid);
        Assert.Contains("wat", result.Error);
    }

    [Fact]
    public void Validate_RejectsAChordAlreadyBoundInTheSameContext()
    {
        // Ctrl+U is Quick Update on the task list; new-tab may not steal it.
        var result = SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, "Ctrl+U", LaunchChordOverrides.None);

        Assert.False(result.IsValid);
        Assert.Contains("already bound", result.Error);
    }

    [Fact]
    public void Validate_RejectsRebindingOneLaunchChordOntoTheOthersEffectiveChord()
    {
        // Split pane is (still) Ctrl+Alt+Enter; binding new-tab there must collide against the sibling's
        // effective token supplied via `current`.
        var current = Overrides(splitPane: "Alt+Enter");
        var result = SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, "Alt+Enter", current);

        Assert.False(result.IsValid);
        Assert.Contains("split pane", result.Error);
    }

    [Fact]
    public void Validate_AllowsReassigningAnActionToItsOwnCurrentChord()
    {
        // A launch action never collides with itself: re-entering new-tab's own current override is Ok.
        var current = Overrides(newTab: "Alt+Enter");
        Assert.True(
            SettingsForm.ValidateLaunchChord(KeyAction.OpenInNewTab, "Alt+Enter", current).IsValid);
    }

    // ── HelpItemSets.WithConfiguredLaunchChords ──────────────────────────────────────────────────────

    [Fact]
    public void Footer_WithNoOverride_IsReturnedUnchanged()
    {
        var set = HelpItemSets.MainList;
        Assert.Same(set, HelpItemSets.WithConfiguredLaunchChords(set, LaunchChordOverrides.None));
    }

    [Fact]
    public void Footer_WithOverride_RelabelsTheLaunchItems_ToTheConfiguredChord()
    {
        var overrides = Overrides(newTab: "Alt+Enter");
        var footer = HelpItemSets.WithConfiguredLaunchChords(HelpItemSets.MainList, overrides);

        // The new-tab item now re-raises (and displays) the configured chord…
        Assert.Contains(footer, i => i.IsAction && i.ActionKey == "Alt+Enter");
        // …and the default Ctrl+Enter is no longer advertised as an action.
        Assert.DoesNotContain(footer, i => i.IsAction && i.ActionKey == "Ctrl+Enter");
        // Split pane, unoverridden, keeps its default.
        Assert.Contains(footer, i => i.IsAction && i.ActionKey == "Ctrl+Alt+Enter");
    }

    // The seam's whole purpose: what the footer advertises equals what the dispatcher resolves, under an
    // override, for every table-driven launch context.
    [Theory]
    [InlineData(ScreenContext.MainList)]
    [InlineData(ScreenContext.QuickOpen)]
    public void Footer_And_Dispatcher_Agree_UnderAnOverride(ScreenContext context)
    {
        var overrides = Overrides(newTab: "Alt+Enter", splitPane: "Ctrl+Shift+Enter");
        var footer = HelpItemSets.WithConfiguredLaunchChords(
            context == ScreenContext.MainList ? HelpItemSets.MainList : HelpItemSets.QuickOpen, overrides);

        foreach (var action in Keybindings.LaunchActions)
        {
            var dispatched = Keybindings.Token(context, action, overrides);
            Assert.Contains(footer, i => i.IsAction && i.ActionKey == dispatched);
        }
    }

    // ── KeybindingDispatcher honours the override ────────────────────────────────────────────────────

    [Fact]
    public void Dispatcher_WithOverride_FiresTheNewChord_AndNotTheOldDefault()
    {
        var fired = 0;
        var overrides = Overrides(newTab: "Alt+Enter");
        var dispatcher = new KeybindingDispatcher(ScreenContext.MainList, overrides)
            .On(KeyAction.OpenInNewTab, () => fired++);

        Assert.True(dispatcher.Dispatch(Parse("Alt+Enter")));
        Assert.Equal(1, fired);

        // The shipped default no longer triggers the rebound gesture.
        Assert.False(dispatcher.Dispatch(Parse("Ctrl+Enter")));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Dispatcher_WithoutOverride_KeepsTheShippedDefault()
    {
        var fired = 0;
        var dispatcher = new KeybindingDispatcher(ScreenContext.MainList)
            .On(KeyAction.OpenInNewTab, () => fired++);

        Assert.True(dispatcher.Dispatch(Parse("Ctrl+Enter")));
        Assert.Equal(1, fired);
    }
}
