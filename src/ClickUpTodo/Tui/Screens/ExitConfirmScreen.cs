using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The exit-confirmation modal (#299, multi-tab epic #292 sub-issue 7): the guard on the one genuinely
/// destructive <c>Esc</c> — leaving the app. Both hosts mount it from their <c>RequestExit()</c>
/// chokepoint (the seam #298/#401 established), so the dashboard's list root and single-task mode's
/// launch-task root ask the same question with the same keys and the same footer text.
/// <para>
/// It is a <b>transient modal</b> in the terms of <c>docs/navigation-model.md</c>: it rides the host's
/// existing <c>ShowScreen</c>/<c>CloseScreen</c> view-stack (which hides the layer beneath and restores
/// it — cursor and tab intact — on close) and is never a <see cref="ClickUpTodo.Tui.NavigationHistory{T}"/>
/// entry. Keyboard-only by design: the prompt is a <see cref="Label"/> and the screen owns a single
/// <c>KeyDown</c> handler, so it adds no focusable view and cannot disturb the single-focus input model
/// (#3/#38). The footer's answer hints are clickable through the shared help bar (#289), so the mouse
/// affordance comes for free without this screen handling mouse events.
/// </para>
/// <para>
/// The host reads <see cref="Confirmed"/> in its close handler: <c>true</c> ⇒ stop the app, <c>false</c>
/// ⇒ the modal was dismissed and the root view is restored. Answer rules live in the pure
/// <see cref="ExitConfirmModel"/>.
/// </para>
/// </summary>
public sealed class ExitConfirmScreen : Screen
{
    /// <summary>The answer line under the prompt — the keys, spelled out (the footer repeats them).</summary>
    public const string AnswerHint = "[Y]es / [Enter] — exit now      [N]o / [Esc] — stay in the app";

    public ExitConfirmScreen()
    {
        Title = "Confirm exit";

        var body = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            Text = $"\n  {ExitConfirmModel.Prompt}\n\n  {AnswerHint}\n",
        };

        KeyDown += (_, key) =>
        {
            // Every key is consumed while the question is up: an unanswered keypress must not leak to the
            // root view beneath (which is only hidden, not torn down).
            key.Handled = true;
            switch (ExitConfirmModel.Route(Classify(key)))
            {
                case ExitConfirmModel.ConfirmAction.Exit:
                    Confirmed = true;
                    Close();
                    break;
                case ExitConfirmModel.ConfirmAction.Cancel:
                    Close();
                    break;
                default:
                    // Ignore — not an answer; keep asking.
                    break;
            }
        };

        Add(body);
    }

    /// <summary>True once the user answered yes — the host's close handler stops the app.</summary>
    public bool Confirmed { get; private set; }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.ExitConfirm;

    public override void OnShown() => SetFocus();

    /// <summary>
    /// Classifies a keypress into one of <see cref="ExitConfirmModel.ConfirmKey"/>'s answers.
    /// <para>
    /// <c>Shift</c> is stripped, so both cases of the letters answer (the repo's existing Y/N confirm
    /// idiom — see <c>PromptTemplateEditorScreen</c>). Terminal.Gui encodes lowercase <c>y</c> as a bare
    /// <c>KeyCode.Y</c> and uppercase <c>Y</c> as <c>KeyCode.Y | ShiftMask</c> — which is also why this
    /// screen classifies by hand instead of going through <see cref="ClickUpTodo.Tui.KeybindingDispatcher"/>:
    /// that matches the table token's exact <see cref="KeyCode"/>, and <c>Key.TryParse("Y")</c> yields the
    /// <em>shifted</em> one, so dispatch would miss a plain <c>y</c>.
    /// </para>
    /// <para>
    /// An arbitrary <c>Ctrl</c>/<c>Alt</c> chord is never an answer — a half-remembered chord shouldn't be
    /// read as "yes, exit". The app's own <b>quit</b> chords are the exception: pressing the key that
    /// raised this question again (<c>Ctrl+Q</c>, or <c>Ctrl+C</c> which <c>TodoApp</c> routes to the same
    /// exit seam) is an unambiguous second "yes", not a stray press — and swallowing them would leave the
    /// terminal's conventional escape hatch dead while a modal asks a question.
    /// </para>
    /// Public and static so the mapping is unit-testable without instantiating the view (the suite never
    /// calls <c>Application.Init</c>).
    /// </summary>
    public static ExitConfirmModel.ConfirmKey Classify(Key key)
    {
        if (key.IsAlt)
            return ExitConfirmModel.ConfirmKey.Other;

        if (key.IsCtrl)
            return (key.KeyCode & ~KeyCode.CtrlMask) switch
            {
                // Keep these in step with the quit paths that route to RequestExit (Ctrl+Q is
                // Keybindings[MainList, Quit]; Ctrl+C is its undisplayed alias in TodoApp.OnListKey).
                KeyCode.Q or KeyCode.C => ExitConfirmModel.ConfirmKey.Yes,
                _ => ExitConfirmModel.ConfirmKey.Other,
            };

        return (key.KeyCode & ~KeyCode.ShiftMask) switch
        {
            KeyCode.Y or KeyCode.Enter => ExitConfirmModel.ConfirmKey.Yes,
            KeyCode.N or KeyCode.Esc => ExitConfirmModel.ConfirmKey.No,
            _ => ExitConfirmModel.ConfirmKey.Other,
        };
    }
}
