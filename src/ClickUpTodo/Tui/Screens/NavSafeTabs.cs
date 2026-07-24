using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A <see cref="Tabs"/> whose built-in arrow-key tab navigation is neutralised, because it crashes the
/// app in Terminal.Gui 2.4.10.
/// <para>
/// The stock control binds <c>CursorUp/Down/Left/Right</c> to a navigation handler that, when you cycle
/// past the first or last tab, wraps around and calls <c>SetFocus()</c> on the far tab's header. In
/// 2.4.10 that throws
/// <c>InvalidOperationException: FocusChanging was not cancelled and the HasFocus value did not change</c>,
/// taking the whole process down — the reported crash on <c>→</c> from the last tab and <c>←</c> from
/// the first. It reaches the handler from either a focused tab header
/// (<c>BorderView.OnCommandNotBound</c> → <c>Tabs.InvokeCommand</c>) or a bare arrow bubbled up from a
/// pane, so intercepting it at the tab control covers both.
/// </para>
/// <para>
/// The Task Detail screen owns tab switching itself via <c>Ctrl+←/→</c> (see <see cref="DetailTabNav"/>
/// and <c>TaskDetailScreen.CycleTab</c>) and always parks focus in the pane body, so the native
/// arrow navigation is both unused and unsafe. Re-registering the four nav commands as inert no-ops
/// (<see cref="View.AddCommand(Command, Func{bool?})"/> replaces the base handler) removes it entirely.
/// Panes' own bare-arrow scrolling is untouched: those keys are consumed by the focused pane before they
/// reach the tab control, and any that do bubble up now do nothing instead of crashing.
/// </para>
/// </summary>
internal sealed class NavSafeTabs : Tabs
{
    public NavSafeTabs()
    {
        // Replace the base Tabs' NavCommandHandler for each navigation command with an inert no-op that
        // reports "not handled" (false) — never the crashing SelectNextTab/SelectPreviousTab. Every route
        // that reaches this control with a nav command dispatches through InvokeCommand, so this one
        // registration disables native arrow navigation regardless of where the key originated.
        AddCommand(Command.Up, static () => false);
        AddCommand(Command.Down, static () => false);
        AddCommand(Command.Left, static () => false);
        AddCommand(Command.Right, static () => false);
    }
}
