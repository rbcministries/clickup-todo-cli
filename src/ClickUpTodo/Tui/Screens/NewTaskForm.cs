using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure input-handling logic for the New Task screen (#213), factored out of the Terminal.Gui glue so
/// it can be unit-tested (mirrors <see cref="FilterSortGroupForm"/> / <c>SettingsForm</c>): validate the
/// entered fields and build the domain-facing <see cref="NewTaskRequest"/> the create facade (#209)
/// consumes. Only Name is required; an empty description is omitted (sent as <c>null</c> so ClickUp
/// applies the list default), and the assignee ids are de-duped with non-positive ids dropped, order
/// preserved. Status/Priority/Due date are not part of this screen (#215 adds the optional ones).
/// </summary>
public static class NewTaskForm
{
    /// <summary>The validation error shown when Save is pressed with a blank name.</summary>
    public const string NameRequiredError = "A task name is required.";

    /// <summary>
    /// Validates the entered fields and, on success, builds the <see cref="NewTaskRequest"/>:
    /// <paramref name="name"/> trimmed and required (else <see cref="NameRequiredError"/>);
    /// <paramref name="description"/> trimmed, becoming <c>null</c> when blank so the facade omits it;
    /// <paramref name="assigneeIds"/> de-duped preserving first-appearance order, dropping non-positive
    /// ids. Returns false with a non-null <paramref name="error"/> and a null <paramref name="request"/>
    /// when invalid.
    /// </summary>
    public static bool TryBuild(
        string? name,
        string? description,
        IReadOnlyList<long> assigneeIds,
        out NewTaskRequest? request,
        out string? error)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length == 0)
        {
            request = null;
            error = NameRequiredError;
            return false;
        }

        var trimmedDescription = (description ?? string.Empty).Trim();

        var assignees = new List<long>();
        var seen = new HashSet<long>();
        foreach (var id in assigneeIds)
        {
            if (id > 0 && seen.Add(id))
                assignees.Add(id);
        }

        request = new NewTaskRequest
        {
            Name = trimmedName,
            Description = trimmedDescription.Length == 0 ? null : trimmedDescription,
            Assignees = assignees,
        };
        error = null;
        return true;
    }
}
