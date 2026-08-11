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

    /// <summary>
    /// The screen's shortcuts for the shared contextual footer (#103). The host renders these on the
    /// single window-owned help line while this screen is active, replacing the list's line — so each
    /// screen declares its footer here instead of hand-rolling a hint <c>Label</c>.
    /// </summary>
    public abstract IReadOnlyList<HelpItem> HelpItems { get; }

    /// <summary>
    /// Raised to show a transient message on the host's status line (e.g. an inline validation error),
    /// now that screens no longer own a hint Label to overwrite. The host routes it to its flash row.
    /// </summary>
    public event EventHandler<string>? FlashRequested;

    /// <summary>Asks the host to flash <paramref name="message"/> on the shared status line.</summary>
    protected void RequestFlash(string message) => FlashRequested?.Invoke(this, message);

    /// <summary>
    /// Raised to show or clear a low-precedence hover hint on the host's status line (#408): a non-null
    /// argument names the link under the pointer, null clears it back to the steady status. Distinct from
    /// <see cref="FlashRequested"/> because a hint persists while the mouse rests on a link (a flash is a
    /// one-shot message a flash/status update overrides); the host routes it to the footer's hover slot.
    /// </summary>
    public event EventHandler<string?>? HoverHintChanged;

    /// <summary>Asks the host to show <paramref name="hint"/> (or clear it, when null) on the status line.</summary>
    protected void RequestHoverHint(string? hint) => HoverHintChanged?.Invoke(this, hint);

    /// <summary>Raised on F1 so the host opens the Help screen over this one (#103).</summary>
    public event EventHandler? HelpRequested;

    /// <summary>Asks the host to open Help (bound to F1 in each screen's key handler).</summary>
    protected void RequestHelp() => HelpRequested?.Invoke(this, EventArgs.Empty);
}
