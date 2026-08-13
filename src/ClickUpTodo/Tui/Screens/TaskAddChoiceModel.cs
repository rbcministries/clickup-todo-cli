namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure classifier for the main list's <c>Ctrl+N</c> <b>Add (sibling) vs Sub-add (child)</b> choice
/// (contextual chords G, #544; the model recorded in slice A, <c>docs/plans/contextual-chord-model.md</c>
/// §4). Factored out of the Terminal.Gui glue so the sibling-vs-child decision is unit-testable without a
/// terminal — the same pure-glue split as <see cref="CommentReplyModel"/>.
/// </summary>
/// <remarks>
/// The child choice creates a subtask of the highlighted task (the merged #603 facade:
/// <c>NewTaskRequest.ParentTaskId</c> → ClickUp's top-level <c>parent</c>). A cursor that is <b>not the
/// user's own fileable work</b> — a header row / empty pane (no highlighted task), a #46 context parent,
/// or a #70/#179 foreign subtask — has no valid own-task parent to sub-add under, so it takes the
/// no-prompt sibling path: this is exactly the "own work" gate <see cref="NewTaskForm.ResolveListSeed"/>
/// applies (and that the main-list rename refuses on), so the two agree on which rows are the user's own.
/// </remarks>
public static class TaskAddChoiceModel
{
    /// <summary>
    /// The resolved <c>Ctrl+N</c> add flow for a main-list cursor.
    /// <para><paramref name="Prompt"/> — when <c>false</c>, add a sibling directly with no clarification
    /// (a header/empty row, or a cursor that isn't the user's own fileable work).</para>
    /// <para><paramref name="ParentTaskId"/> / <paramref name="ParentTaskName"/> — the highlighted parent
    /// for the Sub-add choice, both <c>null</c> when <paramref name="Prompt"/> is <c>false</c>.</para>
    /// </summary>
    public readonly record struct AddChoice(bool Prompt, string? ParentTaskId, string? ParentTaskName);

    /// <summary>The no-prompt sibling add — nothing to sub-add under.</summary>
    private static readonly AddChoice Sibling = new(Prompt: false, ParentTaskId: null, ParentTaskName: null);

    /// <summary>
    /// Classifies a main-list cursor into its <c>Ctrl+N</c> add flow. Returns the no-prompt sibling add
    /// when no task is highlighted (a blank/whitespace <paramref name="highlightedTaskId"/>, i.e. a header
    /// row or empty pane), or when the highlighted row is a #46 context parent
    /// (<paramref name="isContextParent"/>) or a #70/#179 foreign subtask
    /// (<paramref name="isForeignSubtask"/>) — rows that aren't the user's own work to file a subtask
    /// under. Otherwise returns a prompt carrying the highlighted task as the Sub-add parent.
    /// </summary>
    public static AddChoice ForCursor(
        string? highlightedTaskId,
        string? highlightedTaskName,
        bool isContextParent,
        bool isForeignSubtask)
    {
        if (string.IsNullOrWhiteSpace(highlightedTaskId) || isContextParent || isForeignSubtask)
            return Sibling;

        return new AddChoice(Prompt: true, ParentTaskId: highlightedTaskId, ParentTaskName: highlightedTaskName);
    }
}
