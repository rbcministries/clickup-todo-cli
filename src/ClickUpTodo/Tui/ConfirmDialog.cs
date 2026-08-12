using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui 2.4 deprecates the static `Application` facade; the static API is still the supported
// v2 pattern (see the same suppression in TodoApp / NativeModalSpike), so silence CS0618 for the
// nested-run confirm modal.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// Contextual chords F (#543): a small reusable <b>native</b> confirm modal — the promotion of the
/// flag-gated <see cref="NativeModalSpike"/> shape (a nested <c>Application.Run(dialog)</c> disposed
/// through <see cref="TuiTeardown.DisposeSwallowingTeardownBug"/>, behind a single open-slot guard)
/// into the reusable confirmation surface slice A (<c>docs/plans/contextual-chord-model.md</c> §4)
/// chose for the epic's destructive actions. F lands it (the first real modal); G (#544) reuses the
/// same shape for its sibling-vs-child choice dialog.
/// <para>
/// Per the #404/#554 spikes' caveat (recorded in A §4), the native path stays gated on the
/// <c>CLICKUP_TODO_NATIVE_MODAL</c> flag — the same gate as the spike — until the <c>windows</c> and
/// <c>dotnet</c> drivers are confirmed (the <c>tui-validate</c> harness is ANSI-only); callers keep a
/// non-native fallback (the #458 inline armed confirm) for the flag-off default. See
/// <c>docs/plans/contextual-delete-confirmation.md</c>.
/// </para>
/// </summary>
internal static class ConfirmDialog
{
    /// <summary>Whether the native confirm-modal path is enabled — the same env gate as the spike, so a
    /// build either opts into <em>all</em> native modals or none.</summary>
    public static bool Enabled => NativeModalSpike.Enabled;

    /// <summary>
    /// A spike-style marker in the dialog title so the <c>tui-validate</c> harness can prove the native
    /// path was taken (a silently no-op'd flag would otherwise render the bespoke inline confirm and pass
    /// different assertions).
    /// </summary>
    public const string TitleMarker = "[native confirm]";

    // Serialises open requests, exactly like NativeModalSpike._open: set synchronously on the UI thread
    // in TryBeginOpen before the deferred nested run is queued, cleared when Run's nested loop returns.
    // Without it a double key-press buffered before the first idle-invoke ran could stack two nested
    // run-loops (the native path pushes nothing to _screens, so there is no ActiveScreen to gate on).
    private static bool _open;

    /// <summary>
    /// Claims the single confirm-modal slot, returning <c>false</c> if one is already open or opening.
    /// Called synchronously on the UI thread before the nested run is deferred (mirrors
    /// <see cref="NativeModalSpike.TryBeginOpen"/>).
    /// </summary>
    public static bool TryBeginOpen()
    {
        if (_open)
            return false;
        _open = true;
        return true;
    }

    /// <summary>
    /// Opens a native confirm <see cref="Dialog"/> on its own nested run-loop and, when it closes,
    /// invokes <paramref name="onResult"/> with the user's choice (<c>true</c> = confirmed,
    /// <c>false</c> = cancelled). <b>Cancel</b> is the default button and initial focus, so a reflexive
    /// <c>Enter</c> cancels a destructive action; <c>Esc</c> also cancels. The dispose is routed through
    /// the shared teardown guard (#346), and the whole body is wrapped so an exception building the
    /// dialog still clears the slot and reports a cancel rather than stranding the modal. Must be paired
    /// with a preceding successful <see cref="TryBeginOpen"/> and deferred out of the keypress by the
    /// caller (via <see cref="Application.Invoke(Action)"/>), like <c>NativeModalSpike.ShowHelp</c>.
    /// </summary>
    public static void Run(string title, string message, string confirmLabel, Action<bool> onResult)
    {
        ArgumentNullException.ThrowIfNull(onResult);

        var confirmed = false;
        Dialog? dialog = null;
        try
        {
            var lines = (message ?? string.Empty).Split('\n');
            dialog = new Dialog
            {
                Title = $"{title} {TitleMarker}",
                // Near-full width, like the spike's dialogs, so a long checklist name in the message reads in
                // full rather than clipping — the confirm must always show what is about to be deleted.
                Width = Dim.Fill(6),
                // borders (2) + message (+1 spare row for a wrap) + a spacer row + the button row.
                Height = lines.Length + 6,
            };
            var built = dialog;

            var body = new Label
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill(1),
                // One spare row so an over-long source line word-wraps rather than clipping vertically.
                Height = lines.Length + 1,
                Text = message ?? string.Empty,
            };
            // Defence-in-depth against a very long name: wrap instead of truncating horizontally.
            body.TextFormatter.WordWrap = true;

            var confirm = new Button { X = 1, Y = Pos.Bottom(body) + 1, Text = confirmLabel };
            var cancel = new Button { X = Pos.Right(confirm) + 2, Y = Pos.Bottom(body) + 1, Text = "Cancel", IsDefault = true };

            confirm.Accepting += (_, e) =>
            {
                e.Handled = true;
                confirmed = true;
                Application.RequestStop(built);
            };
            cancel.Accepting += (_, e) =>
            {
                e.Handled = true;
                confirmed = false;
                Application.RequestStop(built);
            };
            built.KeyDown += (_, key) =>
            {
                if (key.KeyCode == KeyCode.Esc)
                {
                    key.Handled = true;
                    confirmed = false;
                    Application.RequestStop(built);
                }
            };

            // Focus Cancel once the toplevel is running, so the safe choice is the one a stray Enter
            // takes (deferred to Initialized so the view is part of the running dialog, as the spike does).
            built.Initialized += (_, _) => cancel.SetFocus();

            built.Add(body, confirm, cancel);
            Application.Run(built);
        }
        finally
        {
            if (dialog is not null)
                TuiTeardown.DisposeSwallowingTeardownBug(dialog, "ConfirmDialog");
            _open = false;
            onResult(confirmed);
        }
    }
}
