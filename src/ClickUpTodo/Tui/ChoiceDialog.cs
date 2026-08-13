using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui 2.4 deprecates the static `Application` facade; the static API is still the supported
// v2 pattern (see the same suppression in TodoApp / ConfirmDialog / NativeModalSpike), so silence CS0618
// for the nested-run choice modal.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// Contextual chords G (#544): a small reusable <b>native</b> multi-choice modal — the promotion of the
/// yes/no <see cref="ConfirmDialog"/> shape (itself the promotion of <see cref="NativeModalSpike"/>) into
/// the N-way choice surface slice A (<c>docs/plans/contextual-chord-model.md</c> §4) chose for the
/// epic's sibling-vs-child clarification. It renders one button per choice (in order) plus a Cancel, on
/// a nested <c>Application.Run(dialog)</c> disposed through
/// <see cref="TuiTeardown.DisposeSwallowingTeardownBug"/>, behind a single open-slot guard.
/// <para>
/// Per the #404/#554 spikes' caveat (recorded in A §4), the native path stays gated on the
/// <c>CLICKUP_TODO_NATIVE_MODAL</c> flag — the same gate as <see cref="ConfirmDialog"/> — until the
/// <c>windows</c> and <c>dotnet</c> drivers are confirmed (the <c>tui-validate</c> harness is ANSI-only);
/// callers keep a non-native fallback for the flag-off default.
/// </para>
/// </summary>
internal static class ChoiceDialog
{
    /// <summary>Whether the native choice-modal path is enabled — the same env gate as the spike and
    /// <see cref="ConfirmDialog"/>, so a build opts into <em>all</em> native modals or none.</summary>
    public static bool Enabled => NativeModalSpike.Enabled;

    /// <summary>
    /// A spike-style marker in the dialog title so the <c>tui-validate</c> harness can prove the native
    /// path was taken (distinct from <see cref="ConfirmDialog.TitleMarker"/> so the two surfaces are
    /// distinguishable on screen).
    /// </summary>
    public const string TitleMarker = "[native choice]";

    /// <summary>The result index returned when the modal is cancelled (Esc or the Cancel button).</summary>
    public const int Cancelled = -1;

    // Serialises open requests, exactly like ConfirmDialog._open / NativeModalSpike._open: set
    // synchronously on the UI thread in TryBeginOpen before the deferred nested run is queued, cleared
    // when Run's nested loop returns. The native path pushes nothing to _screens (no ActiveScreen to gate
    // on), so without it a double key-press buffered before the first idle-invoke ran could stack two
    // nested run-loops.
    private static bool _open;

    /// <summary>
    /// Claims the single choice-modal slot, returning <c>false</c> if one is already open or opening.
    /// Called synchronously on the UI thread before the nested run is deferred (mirrors
    /// <see cref="ConfirmDialog.TryBeginOpen"/>).
    /// </summary>
    public static bool TryBeginOpen()
    {
        if (_open)
            return false;
        _open = true;
        return true;
    }

    /// <summary>
    /// Opens a native choice <see cref="Dialog"/> on its own nested run-loop and, when it closes, invokes
    /// <paramref name="onResult"/> with the chosen <paramref name="choices"/> index (or
    /// <see cref="Cancelled"/> = <c>-1</c> for Esc / Cancel). The <b>first</b> choice is the default button
    /// and initial focus, so a reflexive <c>Enter</c> takes the primary choice; <c>Esc</c> cancels. The
    /// dispose is routed through the shared teardown guard (#346), and the whole body is wrapped so an
    /// exception building the dialog still clears the slot and reports a cancel rather than stranding the
    /// modal. Must be paired with a preceding successful <see cref="TryBeginOpen"/> and deferred out of the
    /// keypress by the caller (via <see cref="Application.Invoke(Action)"/>), like the other native modals.
    /// </summary>
    public static void Run(string title, string message, IReadOnlyList<string> choices, Action<int> onResult)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(onResult);

        var chosen = Cancelled;
        Dialog? dialog = null;
        try
        {
            var lines = (message ?? string.Empty).Split('\n');
            dialog = new Dialog
            {
                Title = $"{title} {TitleMarker}",
                // Near-full width, like ConfirmDialog, so a long parent-task name in the message reads in
                // full rather than clipping.
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
                // One spare row so an over-long name word-wraps rather than clipping vertically.
                Height = lines.Length + 1,
                Text = message ?? string.Empty,
            };
            body.TextFormatter.WordWrap = true;

            // One button per choice, left-to-right, then Cancel. The first choice is the default + focus,
            // so a reflexive Enter takes the primary action (on the main list that is "Add task", the
            // pre-existing Ctrl+N behaviour).
            var buttons = new List<Button>(choices.Count);
            for (var i = 0; i < choices.Count; i++)
            {
                var index = i;
                var button = new Button
                {
                    X = i == 0 ? 1 : Pos.Right(buttons[i - 1]) + 2,
                    Y = Pos.Bottom(body) + 1,
                    Text = choices[i],
                    IsDefault = i == 0,
                };
                button.Accepting += (_, e) =>
                {
                    e.Handled = true;
                    chosen = index;
                    Application.RequestStop(built);
                };
                buttons.Add(button);
            }

            var cancel = new Button
            {
                X = buttons.Count == 0 ? 1 : Pos.Right(buttons[^1]) + 2,
                Y = Pos.Bottom(body) + 1,
                Text = "Cancel",
            };
            cancel.Accepting += (_, e) =>
            {
                e.Handled = true;
                chosen = Cancelled;
                Application.RequestStop(built);
            };

            built.KeyDown += (_, key) =>
            {
                if (key.KeyCode == KeyCode.Esc)
                {
                    key.Handled = true;
                    chosen = Cancelled;
                    Application.RequestStop(built);
                }
            };

            // Focus the first choice once the toplevel is running (deferred to Initialized so the view is
            // part of the running dialog, as the spike does).
            if (buttons.Count > 0)
            {
                var first = buttons[0];
                built.Initialized += (_, _) => first.SetFocus();
            }

            built.Add(body);
            foreach (var button in buttons)
                built.Add(button);
            built.Add(cancel);
            Application.Run(built);
        }
        finally
        {
            if (dialog is not null)
                TuiTeardown.DisposeSwallowingTeardownBug(dialog, "ChoiceDialog");
            _open = false;
            onResult(chosen);
        }
    }
}
