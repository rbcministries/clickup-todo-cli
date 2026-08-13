namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure decision for the main-list task-rename overlay (contextual chords H, #545): normalize the
/// field text and classify what a submit means, so the Terminal.Gui-coupled <c>RenameTaskScreen</c>
/// stays a thin shell over unit-testable logic (mirrors <c>StatusPickerModel</c> / <c>SettingsForm</c>).
/// </summary>
public static class RenameTaskModel
{
    /// <summary>What submitting the field resolves to.</summary>
    public enum Outcome
    {
        /// <summary>The field is empty/whitespace — ClickUp has no empty-title concept, so keep the
        /// overlay open and prompt rather than dismissing on an empty Enter.</summary>
        Blank,

        /// <summary>The trimmed text equals the current title — nothing to write, just dismiss.</summary>
        Unchanged,

        /// <summary>A genuine new title — <see cref="Result.Name"/> is the trimmed value to write.</summary>
        Rename,
    }

    /// <summary>The classified submit and the normalized (trimmed) name it carries.</summary>
    public readonly record struct Result(Outcome Outcome, string Name);

    /// <summary>
    /// Classifies a rename submit: trims <paramref name="input"/>, returning <see cref="Outcome.Blank"/>
    /// for empty/whitespace, <see cref="Outcome.Unchanged"/> when it matches <paramref name="originalName"/>
    /// (both trimmed, ordinal), and <see cref="Outcome.Rename"/> otherwise with the trimmed value. The
    /// original is trimmed on the comparison too, so pressing Enter on a title that merely carries stray
    /// surrounding whitespace is a no-op rather than a needless normalize-only write.
    /// </summary>
    public static Result Classify(string? input, string originalName)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return new Result(Outcome.Blank, string.Empty);
        if (string.Equals(trimmed, (originalName ?? string.Empty).Trim(), StringComparison.Ordinal))
            return new Result(Outcome.Unchanged, trimmed);
        return new Result(Outcome.Rename, trimmed);
    }
}
