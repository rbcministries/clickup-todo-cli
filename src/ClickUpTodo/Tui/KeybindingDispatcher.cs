using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace ClickUpTodo.Tui;

/// <summary>
/// Dispatches key presses to handlers using the central <see cref="Keybindings"/> table as the single
/// source of truth (#355). A screen builds one dispatcher for its <see cref="ScreenContext"/>, registers
/// <c>(action → handler)</c> pairs with <see cref="On"/> (which resolves the key from the table, so the
/// binding is never re-typed at the call site), and calls <see cref="Dispatch"/> from its
/// <c>KeyDown</c> handler.
/// <para>
/// Matching is by <c>Terminal.Gui</c> <see cref="Key.KeyCode"/>: the table token is parsed once with
/// <see cref="Key.TryParse"/> — the same path a footer click uses (<c>TodoApp</c>) — which yields the
/// canonical <see cref="KeyCode"/> a physical press produces, so a click, a re-raised chord, and a real
/// keypress all converge here.
/// </para>
/// </summary>
public sealed class KeybindingDispatcher(ScreenContext context)
{
    private readonly Dictionary<KeyCode, Action> _handlers = [];

    /// <summary>The context whose <see cref="Keybindings"/> entries this dispatcher resolves against.</summary>
    public ScreenContext Context { get; } = context;

    /// <summary>
    /// Bind <paramref name="handler"/> to the key the table maps <paramref name="action"/> to in this
    /// dispatcher's <see cref="Context"/>. Throws if the context does not bind the action
    /// (<see cref="KeyNotFoundException"/>) or the token is not a parseable key
    /// (<see cref="InvalidOperationException"/>) — both are wiring bugs surfaced at construction/startup.
    /// </summary>
    public KeybindingDispatcher On(KeyAction action, Action handler)
    {
        var token = Keybindings.Token(Context, action);
        if (!Key.TryParse(token, out var key))
            throw new InvalidOperationException(
                $"Keybinding '{token}' for {Context}/{action} is not a parseable key.");

        _handlers[key.KeyCode] = handler;
        return this;
    }

    /// <summary>
    /// Invoke the handler bound to <paramref name="key"/> and return <c>true</c> when one fired, so the
    /// caller can set <c>key.Handled</c> and stop. Returns <c>false</c> for an unbound key, leaving the
    /// caller's remaining handling (movement, aliases, type-ahead) untouched.
    /// </summary>
    public bool Dispatch(Key key)
    {
        if (_handlers.TryGetValue(key.KeyCode, out var handler))
        {
            handler();
            return true;
        }

        return false;
    }
}
