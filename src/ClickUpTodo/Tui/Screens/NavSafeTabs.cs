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
        // reports the key as *handled* (true) — never the crashing SelectNextTab/SelectPreviousTab. Every
        // route that reaches this control with a nav command dispatches through InvokeCommand, so this one
        // registration disables native arrow navigation regardless of where the key originated.
        //
        // It must return true, not false: a bare arrow only reaches the tab control after the focused pane
        // declined it (a TextView at a scroll edge, or the Task Tree ListView with the selection already at
        // the top/bottom row). Returning false leaves the key unhandled, so Terminal.Gui falls through to
        // default focus traversal — which lands on a tab header and silently switches tabs. That was the
        // reported asymmetry: ↓ moved the tree selection (the ListView consumed it) but ↑ from the top row
        // jumped to the previous tab. Consuming it here makes an arrow at a pane boundary a genuine no-op.
        AddCommand(Command.Up, static () => true);
        AddCommand(Command.Down, static () => true);
        AddCommand(Command.Left, static () => true);
        AddCommand(Command.Right, static () => true);
    }
}
