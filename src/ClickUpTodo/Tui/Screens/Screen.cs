using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Base for a full-window screen that the dashboard swaps in over the task list, in place of a
/// nested modal <c>Dialog</c> run on its own <c>Application.Run</c> loop. Keeping everything inside
/// the single toplevel is what kept the main list snappy (see #3) and avoids a second run-loop
/// competing with the background refresh (see #38).
/// <para>
/// A screen fills the same area as the list frame (leaving the bottom status + hint lines visible),
/// raises <see cref="Closed"/> when it's done, and exposes <see cref="OnShown"/> so the host can set
/// initial focus once the screen is mounted. The host (TodoApp) owns mounting/teardown and reads any
/// result off the concrete screen in its close handler.
/// </para>
/// </summary>
public abstract class Screen : FrameView
{
    protected Screen()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill(2); // leave the status + hint lines at the bottom visible, like the list frame
        CanFocus = true;
    }

    /// <summary>Raised when the screen wants the host to tear it down and restore the task list.</summary>
    public event EventHandler? Closed;

    /// <summary>Signals the host to close this screen (e.g. on Esc, Save, or a selection).</summary>
    protected void Close() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by the host once the screen is mounted, so it can focus its primary control.</summary>
    public virtual void OnShown() { }
}
