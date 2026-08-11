using ClickUpTodo.Configuration;
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

    // The Filter·Sort·Group (#554) modal's own slot, distinct from the help slot above so native Help
    // can stack *over* an open F3 modal (both slots claimed at once) — the native analogue of the
    // _screens LIFO stack. Same rationale as _open: the native path pushes nothing to _screens.
    private static bool _fsgOpen;

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
    /// Claims the Filter·Sort·Group (#554) native-modal slot, returning false if one is already open or
    /// opening. Called synchronously on the UI thread from <c>OpenViewSettings</c> before the nested run
    /// is deferred (distinct from <see cref="TryBeginOpen"/> so Help can stack over the F3 modal).
    /// </summary>
    public static bool TryBeginOpenFilterSortGroup()
    {
        if (_fsgOpen)
            return false;
        _fsgOpen = true;
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

    /// <summary>
    /// #554: opens Filter · Sort · Group as a native <see cref="Dialog"/> on its own nested run-loop —
    /// the focusable-form analogue of <see cref="RunHelpDialog"/> — so the two hosts can be measured A/B
    /// for the thing Help could not exercise: <b>intra-modal input latency</b> (typing/Tab across the
    /// form's <see cref="TextField"/>/<see cref="ListView"/>s) and <b>result-marshalling</b>. The form
    /// itself is built by the shared <see cref="FilterSortGroupFormBuilder"/>, so it is identical to the
    /// <c>_screens</c> <see cref="FilterSortGroupScreen"/> and the A/B differs only in the hosting
    /// mechanism (the title carries <see cref="TitleMarker"/> so the harness can prove leg B took the
    /// native path). Esc / Cancel closes with no result; Save marshals a <see cref="ViewSettings"/> back
    /// through <paramref name="apply"/>. F1 stacks native Help over the modal, deferred out of the
    /// keypress via <see cref="Application.Invoke(Action)"/> like <c>ShowHelp</c> so the nested help loop
    /// is not entered re-entrantly. The dispose is routed through the shared teardown guard (#346). Must
    /// be paired with a preceding successful <see cref="TryBeginOpenFilterSortGroup"/>.
    /// </summary>
    public static void RunFilterSortGroupDialog(ViewSettings current, Action<ViewSettings?> apply, Action<string> flash)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(flash);

        var dialog = new Dialog
        {
            Title = $"Filter · Sort · Group {TitleMarker}",
            Width = Dim.Fill(4),
            Height = Dim.Fill(2),
        };

        var form = FilterSortGroupFormBuilder.Build(current, flash, () => Application.RequestStop(dialog));
        dialog.Add([.. form.Controls]);

        // Start focus on the field picker, matching the _screens host's OnShown, so the A/B measures the
        // same intra-modal Tab path. Deferred to Initialized so the view is part of the running toplevel.
        dialog.Initialized += (_, _) => form.PrimaryFocus.SetFocus();

        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                Application.RequestStop(dialog);
            }
            else if (key.KeyCode == KeyCode.F1)
            {
                // Modal stacking: open native Help *over* the F3 dialog (the native analogue of the
                // _screens LIFO stack fsg_check.py exercises). Deferred via Application.Invoke so the
                // nested help loop is not entered re-entrantly from inside KeyDown (mirrors ShowHelp),
                // and guarded by the help slot so a buffered double-F1 can't stack two help dialogs.
                key.Handled = true;
                if (TryBeginOpen())
                    Application.Invoke(RunHelpDialog);
            }
        };

        try
        {
            Application.Run(dialog);
        }
        finally
        {
            TuiTeardown.DisposeSwallowingTeardownBug(dialog, "NativeModalSpike FilterSortGroup Dialog");
            _fsgOpen = false;
            // Marshal the result back on the UI thread (the nested loop returns into the outer loop's
            // invoke) — null on Esc/Cancel, the saved view on Save. This is the axis Help never had.
            apply(form.Result);
        }
    }
}
