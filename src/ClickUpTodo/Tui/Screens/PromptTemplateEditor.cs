using ClickUpTodo.Agent;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure logic for the dispatch prompt-template editor (#100), factored out of the Terminal.Gui glue so
/// it is unit-testable (mirrors <c>SettingsForm</c> / <c>StatusPickerModel</c>): seeding the editor,
/// normalizing edited text back to a stored value, and the reset-to-default decision.
/// </summary>
public static class PromptTemplateEditor
{
    /// <summary>
    /// The text to seed the editor with: the saved template when set, otherwise the
    /// <see cref="AgentPromptComposer.DefaultTemplate"/> — so a first-time user edits down from a
    /// working example rather than a blank box.
    /// </summary>
    public static string Seed(string? saved)
        => string.IsNullOrWhiteSpace(saved) ? AgentPromptComposer.DefaultTemplate : saved;

    /// <summary>
    /// Normalizes edited text into the value to persist: CRLF/CR are folded to <c>\n</c> (so the
    /// composer's platform-independent output is preserved) and trailing whitespace is trimmed. Text
    /// equal to the <see cref="AgentPromptComposer.DefaultTemplate"/> collapses to <c>""</c> so the
    /// "blank ⇒ default" invariant stays clean (no redundant copy of the default is stored).
    /// </summary>
    public static string Normalize(string? text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
        return string.Equals(normalized, AgentPromptComposer.DefaultTemplate, StringComparison.Ordinal)
            ? string.Empty
            : normalized;
    }

    /// <summary>
    /// The reset-to-default decision: on <paramref name="confirmed"/> the editor text becomes the
    /// <see cref="AgentPromptComposer.DefaultTemplate"/>; declining leaves the current edits untouched.
    /// </summary>
    public static string ApplyReset(bool confirmed, string current)
        => confirmed ? AgentPromptComposer.DefaultTemplate : current;
}
