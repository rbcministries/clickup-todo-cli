namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure logic for the exit-confirmation modal (issue #299, multi-tab epic #292 sub-issue 7),
/// factored out of the Terminal.Gui glue so the answer rules are unit-testable without a terminal —
/// the same pure-glue split as <see cref="DescriptionEditorModel"/> / <see cref="PromptTemplateEditor"/>.
/// The glue (<see cref="ExitConfirmScreen"/>) classifies a Terminal.Gui <c>Key</c> into a
/// <see cref="ConfirmKey"/>; this decides what that answer means.
/// </summary>
public static class ExitConfirmModel
{
    /// <summary>
    /// The question the modal asks. Named here (rather than inline in the screen) so the prompt, the
    /// help copy and the tests all reference one string.
    /// </summary>
    public const string Prompt = "Are you sure you want to exit?";

    /// <summary>The two answers, plus everything that isn't one. Y/Enter are <see cref="Yes"/>;
    /// N/Esc are <see cref="No"/>; any other key is <see cref="Other"/>.</summary>
    public enum ConfirmKey
    {
        Yes,
        No,
        Other,
    }

    /// <summary>What the glue should do for a classified key.</summary>
    public enum ConfirmAction
    {
        /// <summary>Confirmed — stop the app (quit the dashboard / close the single-task tab).</summary>
        Exit,

        /// <summary>Declined — dismiss the modal and return to the root view the user came from.</summary>
        Cancel,

        /// <summary>Not an answer — keep asking.</summary>
        Ignore,
    }

    /// <summary>
    /// Maps an answer to its action. An unrecognised key is <see cref="ConfirmAction.Ignore"/>d — the
    /// modal stays up until the user actually answers: the destructive answer here is "yes", so a stray
    /// keypress must never exit, and silently dismissing on any key would make a mistyped keystroke look
    /// like the app had ignored the quit. (This is deliberately stricter than the repo's inline
    /// draft-discard prompts, where "anything else" means "keep editing" because there the safe answer
    /// <em>is</em> dismissal.)
    /// </summary>
    public static ConfirmAction Route(ConfirmKey key) => key switch
    {
        ConfirmKey.Yes => ConfirmAction.Exit,
        ConfirmKey.No => ConfirmAction.Cancel,
        _ => ConfirmAction.Ignore,
    };
}
