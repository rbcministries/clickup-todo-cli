using ClickUpTodo.Tui.Screens;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui 2.4 deprecates the static `Application` facade; the static API is still the supported
// v2 pattern (see the same suppression in TodoApp), so silence CS0618 for the nested-run prototype.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// #404 spike: a flag-gated prototype that opens the F1 Help surface as a <b>native</b> Terminal.Gui
/// modal — a nested <c>Application.Run(dialog)</c> — instead of the hand-mounted <c>_screens</c>
/// <see cref="HelpScreen"/>, so the two hosting mechanisms can be measured A/B under the
/// <c>tui-validate</c> PTY harness. This is the pattern #3/#38 deliberately avoided (a second
/// run-loop competing with the single outer <c>Application.Run(_window)</c> loop and the background
/// refresh), so the spike measures whether the ANSI-renderer hardening since that rejection
/// (<see cref="DiffFlushAnsiBackend"/>, <see cref="TuiTeardown"/>) has removed the original blockers.
///
/// Enabled only when the <c>CLICKUP_TODO_NATIVE_MODAL</c> environment variable is set; off by default,
/// so production behaviour is byte-identical. See <c>docs/plans/native-modals-spike.md</c>.
/// </summary>
internal static class NativeModalSpike
{
    /// <summary>Whether the native-modal spike path is enabled (env flag, read once at startup).</summary>
    public static bool Enabled { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLICKUP_TODO_NATIVE_MODAL"));

    /// <summary>
    /// A spike marker in the native <see cref="Dialog"/>'s title, distinguishing it on screen from the
    /// <c>_screens</c> <see cref="HelpScreen"/> ("Keyboard shortcuts"). The A/B harness asserts on this
    /// to prove leg B actually took the native path (a silently no-op'd flag would otherwise render the
    /// identical <see cref="HelpScreen"/> and pass the same assertions).
    /// </summary>
    public const string TitleMarker = "[native modal spike]";

    // Serialises open requests: set synchronously in ShowHelp before the deferred nested run is
    // queued, cleared when RunHelpDialog's nested loop returns. Without it the native path — which
    // pushes nothing to _screens, so ActiveScreen stays null — could queue two RunHelpDialog invokes
    // from two F1 presses buffered before the first idle-invoke ran, stacking two nested run-loops.
    private static bool _open;

    /// <summary>
    /// Claims the single native-modal slot, returning false if one is already open or opening. Called
    /// synchronously on the UI thread from <c>ShowHelp</c> before the nested run is deferred.
    /// </summary>
    public static bool TryBeginOpen()
    {
        if (_open)
            return false;
        _open = true;
        return true;
    }

    /// <summary>
    /// Opens the keyboard-shortcut help as a native <see cref="Dialog"/> run on its own nested
    /// run-loop; Esc or Enter closes it. Renders <see cref="HelpScreen.ShortcutsText"/> — the same body
    /// the <c>_screens</c> <see cref="HelpScreen"/> shows (the title carries <see cref="TitleMarker"/>)
    /// — so the A/B differs only in the hosting mechanism. The dialog dispose is routed through the
    /// shared teardown guard so the prototype exercises the same Terminal.Gui 2.4.10 dispose mitigation
    /// (#346) a real migration of the #402 transient-modal category would need. Must be paired with a
    /// preceding successful <see cref="TryBeginOpen"/>.
    /// </summary>
    public static void RunHelpDialog()
    {
        var dialog = new Dialog
        {
            Title = $"Keyboard shortcuts {TitleMarker}",
            Width = Dim.Fill(4),
            Height = Dim.Fill(2),
        };

        var body = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            Text = HelpScreen.ShortcutsText,
        };
        dialog.Add(body);

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Esc or KeyCode.Enter)
            {
                key.Handled = true;
                Application.RequestStop(dialog);
            }
        };

        try
        {
            Application.Run(dialog);
        }
        finally
        {
            TuiTeardown.DisposeSwallowingTeardownBug(dialog, "NativeModalSpike Dialog");
            _open = false;
        }
    }
}
