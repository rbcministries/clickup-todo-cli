using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The handle a <see cref="QuickOpenFormBuilder"/> hands back: the built controls to mount, the control
/// to focus first (the input <see cref="TextField"/>), and the marshalled <see cref="QuickOpenRequest"/>
/// result (set on a submit gesture, left null on Cancel/Esc). Lets the two hosts — the <c>_screens</c>
/// <see cref="QuickOpenScreen"/> and the #618 native-modal <c>Dialog</c> — share the identical form so an
/// A/B differs only in the hosting mechanism (mirroring <see cref="FilterSortGroupFormHandle"/>, #554).
/// </summary>
public sealed class QuickOpenFormHandle
{
    /// <summary>The form controls to add to the host surface, in tab/paint order.</summary>
    public IReadOnlyList<View> Controls { get; internal set; } = [];

    /// <summary>The control the host should focus once mounted (the input field).</summary>
    public View PrimaryFocus { get; internal set; } = default!;

    /// <summary>The submitted (trimmed) text + chosen launch intent, or null if the form was cancelled.</summary>
    public QuickOpenRequest? Result { get; internal set; }

    /// <summary>The three submit gestures (Enter/Ctrl+Enter/Ctrl+Alt+Enter → Submit), attached to the
    /// input field by the builder. Exposed so each host also wires it at its <em>surface</em> level, so a
    /// chord fires whether the field or a button holds focus — matching the pre-extraction
    /// screen-level wiring. Back (Esc) and Help (F1) are the host's, not the form's.</summary>
    internal KeybindingDispatcher SubmitKeys { get; set; } = default!;

    /// <summary>Dispatches <paramref name="key"/> against the form's submit gestures, returning true when
    /// it was one (the host should then mark the key handled).</summary>
    public bool DispatchSubmit(Key key) => SubmitKeys.Dispatch(key);
}

/// <summary>
/// Builds the Ctrl+O quick-open form — the prompt label, the id/custom-id/URL input field, and the
/// Open/New tab/Split pane/Cancel button row — into a host-agnostic <see cref="QuickOpenFormHandle"/>.
/// Extracted from <see cref="QuickOpenScreen"/>'s constructor (slice E, #618) so the same form can be
/// hosted either as the hand-mounted <c>_screens</c> screen (leg A) or as a native Terminal.Gui
/// <c>Dialog</c> on a nested run-loop (leg B), with the caller wiring only the host-specific bits: how a
/// flash is surfaced, and how the surface closes.
/// <para>
/// The blank-input flash, the three submit gestures and the button row live here, so the launch modes (B,
/// #615) are written once and both hosts get them. Context command keys (Esc = Back, F1 = Help) stay on
/// the host, which knows its own back/help affordance (the <see cref="FilterSortGroupFormBuilder"/>
/// precedent).
/// </para>
/// </summary>
public static class QuickOpenFormBuilder
{
    /// <summary>
    /// Builds the form. <paramref name="flash"/> surfaces the blank-input hint on the host's status line;
    /// <paramref name="close"/> tears the host surface down (a submit sets
    /// <see cref="QuickOpenFormHandle.Result"/> first; Cancel leaves it null). <paramref name="overrides"/>
    /// is the config launch-chord override (#506) applied to the submit dispatcher, so a rebound
    /// <c>Ctrl+Enter</c> / <c>Ctrl+Alt+Enter</c> fires in both hosts; <c>null</c> ⇒ the shipped defaults.
    /// </summary>
    public static QuickOpenFormHandle Build(Action<string> flash, Action close, LaunchChordOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(flash);
        ArgumentNullException.ThrowIfNull(close);

        var handle = new QuickOpenFormHandle();

        var prompt = new Label
        {
            X = 1,
            Y = 1,
            Text = "Task id, custom id, or URL:",
            CanFocus = false,
        };

        var input = new TextField
        {
            X = 1,
            Y = Pos.Bottom(prompt),
            Width = Dim.Fill(1),
        };

        // Records the trimmed input + intent and closes when the field is non-blank; a blank field flashes
        // a hint and stays open (for every intent) so an empty gesture never dismisses the surface. The
        // parse/resolve/launch runs in the host once this surface has closed.
        void Submit(QuickOpenIntent intent)
        {
            if (QuickOpenRequest.From(input.Text?.ToString(), intent) is { } request)
            {
                handle.Result = request;
                close();
            }
            else
            {
                flash("Enter a task id, custom id, or ClickUp task URL.");
            }
        }

        // Open stays the default (Enter); New tab / Split pane are the Tab-reachable, driver-robust path
        // for the two chords (#615, #503), and Cancel closes. Each button funnels through the same
        // Submit(intent) the chords do, so the two entry paths can't drift.
        var open = new Button { X = 1, Y = Pos.Bottom(input) + 1, Text = "Open", IsDefault = true };
        var newTab = new Button { X = Pos.Right(open) + 2, Y = Pos.Bottom(input) + 1, Text = "New tab" };
        var splitPane = new Button { X = Pos.Right(newTab) + 2, Y = Pos.Bottom(input) + 1, Text = "Split pane" };
        var cancel = new Button { X = Pos.Right(splitPane) + 2, Y = Pos.Bottom(input) + 1, Text = "Cancel" };
        open.Accepting += (_, e) =>
        {
            // Swallow the Accept so the default-button activation doesn't also bubble as an Enter.
            e.Handled = true;
            Submit(QuickOpenIntent.OpenHere);
        };
        newTab.Accepting += (_, e) =>
        {
            e.Handled = true;
            Submit(QuickOpenIntent.NewTab);
        };
        splitPane.Accepting += (_, e) =>
        {
            e.Handled = true;
            Submit(QuickOpenIntent.SplitPane);
        };
        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            close();
        };

        // #355/#398: the three submit gestures dispatch through the central table rather than a
        // hand-rolled key switch, so the keys and their footer labels (HelpItemSets.QuickOpen) cannot
        // drift. Attached to the input field (the per-form key); the host wires DispatchSubmit at its
        // surface level too, so a chord fires from a focused button as well.
        handle.SubmitKeys = new KeybindingDispatcher(ScreenContext.QuickOpen, overrides)
            .On(KeyAction.Open, () => Submit(QuickOpenIntent.OpenHere))
            .On(KeyAction.OpenInNewTab, () => Submit(QuickOpenIntent.NewTab))
            .On(KeyAction.OpenInSplitPane, () => Submit(QuickOpenIntent.SplitPane));
        input.KeyDown += (_, key) =>
        {
            if (handle.SubmitKeys.Dispatch(key))
                key.Handled = true;
        };

        handle.Controls = [prompt, input, open, newTab, splitPane, cancel];
        handle.PrimaryFocus = input;
        return handle;
    }
}
