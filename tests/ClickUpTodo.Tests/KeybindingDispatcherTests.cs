using ClickUpTodo.Tui;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Input;

namespace ClickUpTodo.Tests;

/// <summary>
/// The dispatch side of #355: a <see cref="KeybindingDispatcher"/> resolves each action's key from the
/// central <see cref="Keybindings"/> table, so dispatch and the footer read the same source of truth.
/// Keys are built with <see cref="Key.TryParse"/> — the same path a footer click uses — so these tests
/// exercise exactly what a real press converges on, with no Terminal.Gui driver.
/// </summary>
public sealed class KeybindingDispatcherTests
{
    private static Key Parse(string token)
    {
        Assert.True(Key.TryParse(token, out var key), $"'{token}' should parse");
        return key;
    }

    [Fact]
    public void Dispatch_FiresTheHandlerBoundToTheActionsKey()
    {
        var fired = 0;
        var dispatcher = new KeybindingDispatcher(ScreenContext.MainList)
            .On(KeyAction.QuickUpdate, () => fired++);

        var handled = dispatcher.Dispatch(Parse(Keybindings.Token(ScreenContext.MainList, KeyAction.QuickUpdate)));

        Assert.True(handled);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Dispatch_ReturnsFalse_AndFiresNothing_ForAnUnboundKey()
    {
        var fired = 0;
        var dispatcher = new KeybindingDispatcher(ScreenContext.MainList)
            .On(KeyAction.QuickUpdate, () => fired++);

        // F5 (Refresh) is not registered on this dispatcher — a bare press must pass through untouched.
        var handled = dispatcher.Dispatch(Parse("F5"));

        Assert.False(handled);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void On_Throws_WhenTheContextDoesNotBindTheAction()
    {
        var dispatcher = new KeybindingDispatcher(ScreenContext.Help);

        // The Help screen binds no QuickUpdate — registering one is a wiring bug surfaced immediately.
        Assert.Throws<KeyNotFoundException>(() => dispatcher.On(KeyAction.QuickUpdate, () => { }));
    }

    [Fact]
    public void On_Throws_WhenTwoRegistrationsCollideOnTheSameKey()
    {
        var dispatcher = new KeybindingDispatcher(ScreenContext.MainList)
            .On(KeyAction.QuickUpdate, () => { });

        // Re-binding the same key (here via the same action, Ctrl+U) is a wiring bug: fail fast rather
        // than let the second registration silently shadow the first.
        Assert.Throws<InvalidOperationException>(() => dispatcher.On(KeyAction.QuickUpdate, () => { }));
    }

    // Closes the loop for the migrated context: every main-list command's table key dispatches to its
    // own handler and to no other. Together with the footer-agreement guard in KeybindingsTests, this
    // proves dispatch == table == footer for the main list.
    [Fact]
    public void EveryMainListAction_DispatchesToItsOwnHandler()
    {
        var actions = Keybindings.ActionsFor(ScreenContext.MainList).ToList();
        KeyAction? lastFired = null;

        var dispatcher = new KeybindingDispatcher(ScreenContext.MainList);
        foreach (var action in actions)
        {
            var captured = action;
            dispatcher.On(captured, () => lastFired = captured);
        }

        foreach (var action in actions)
        {
            lastFired = null;
            var handled = dispatcher.Dispatch(Parse(Keybindings.Token(ScreenContext.MainList, action)));

            Assert.True(handled, $"{action} key should dispatch");
            Assert.Equal(action, lastFired);
        }
    }

    // Same closure for the QuickOpen context (#398 slice): every QuickOpen command's table key
    // (Open/Help/Back) dispatches to its own handler and to no other. With the footer-agreement
    // guard in KeybindingsTests, this proves dispatch == table == footer for QuickOpen too.
    [Fact]
    public void EveryQuickOpenAction_DispatchesToItsOwnHandler()
    {
        var actions = Keybindings.ActionsFor(ScreenContext.QuickOpen).ToList();
        KeyAction? lastFired = null;

        var dispatcher = new KeybindingDispatcher(ScreenContext.QuickOpen);
        foreach (var action in actions)
        {
            var captured = action;
            dispatcher.On(captured, () => lastFired = captured);
        }

        foreach (var action in actions)
        {
            lastFired = null;
            var handled = dispatcher.Dispatch(Parse(Keybindings.Token(ScreenContext.QuickOpen, action)));

            Assert.True(handled, $"{action} key should dispatch");
            Assert.Equal(action, lastFired);
        }
    }
}
